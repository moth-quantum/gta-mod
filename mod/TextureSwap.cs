
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using GTA;

namespace QDay;


public static unsafe class TextureSwap
{
    private static grcTextureFactoryDX11** textureFactoryGlobal = null;
    private static IntPtr fragmentSetTextureAddr = IntPtr.Zero;

    private static grcTextureFactoryDX11* TextureFactory => textureFactoryGlobal != null ? *textureFactoryGlobal : null;

    public static bool IsReady => TextureFactory != null && fragmentSetTextureAddr != IntPtr.Zero;

    public static bool Init()
    {
        textureFactoryGlobal = null;
        fragmentSetTextureAddr = IntPtr.Zero;

        IntPtr addr = Game.FindPattern("48 8B 0D ? ? ? ? 48 8B DA 48 8B 01 49 8B D0 49 8B F0 FF 90");
        if (addr == IntPtr.Zero)
        {
            Log.Write("TextureSwap: grcTextureFactory pattern not found");
        }
        else
        {
            textureFactoryGlobal = (grcTextureFactoryDX11**)(addr + *(int*)(addr + 3) + 7);
            Log.Write($"TextureSwap: grcTextureFactory global at 0x{(long)textureFactoryGlobal:X}, currently 0x{(long)TextureFactory:X}");
        }

        fragmentSetTextureAddr = Game.FindPattern("48 89 5C 24 ? 57 48 83 EC ? 48 8B FA 8B D9 66 45 85 C0");
        if (fragmentSetTextureAddr == IntPtr.Zero)
            Log.Write("TextureSwap: FragmentSetTexture pattern not found");
        else
            Log.Write($"TextureSwap: FragmentSetTexture at 0x{fragmentSetTextureAddr.ToInt64():X}");

        bool found = textureFactoryGlobal != null && fragmentSetTextureAddr != IntPtr.Zero;
        Log.Write(found ? "TextureSwap: patterns found" : "TextureSwap: disabled, init failed");
        return found ? InstallTextureHook() : false;
    }

    // Embedded DDS files from recorded/qday_textures_mild.
    // Keyed by texture name without extension, case-insensitive: "ah_bboards1" -> "textures.ah_bboards1.dds".
    private const string TexturePrefix = "textures.";
    private static Dictionary<string, string> embeddedTextures;

    private static Dictionary<string, string> EmbeddedTextures
    {
        get
        {
            if (embeddedTextures == null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string res in typeof(TextureSwap).Assembly.GetManifestResourceNames())
                {
                    if (res.StartsWith(TexturePrefix, StringComparison.Ordinal))
                        map[Path.GetFileNameWithoutExtension(res.Substring(TexturePrefix.Length))] = res;
                }
                Log.Write($"TextureSwap: {map.Count} embedded textures");
                embeddedTextures = map;
            }
            return embeddedTextures;
        }
    }

    public static IEnumerable<string> EmbeddedTextureNames => EmbeddedTextures.Keys;

    public static bool HasEmbeddedTexture(string name) => name != null && EmbeddedTextures.ContainsKey(StripDds(name));

    // Returns the DDS file bytes for a texture name ("ah_bboards1" or "ah_bboards1.dds"), or null if none is embedded.
    public static byte[] GetEmbeddedTexture(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (!EmbeddedTextures.TryGetValue(StripDds(name), out string res))
        {
            Log.Write($"TextureSwap: no embedded texture '{name}'");
            return null;
        }

        using Stream s = typeof(TextureSwap).Assembly.GetManifestResourceStream(res);
        if (s == null)
        {
            Log.Write($"TextureSwap: cannot open resource '{res}'");
            return null;
        }

        var data = new byte[s.Length];
        int read = 0;
        while (read < data.Length)
        {
            int n = s.Read(data, read, data.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read != data.Length)
        {
            Log.Write($"TextureSwap: short read on '{res}' ({read}/{data.Length} bytes)");
            return null;
        }
        return data;
    }

    private static string StripDds(string name) =>
        name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;

    public static string MakeMemoryFileName(IntPtr buffer, int bufferSize, bool freeBufferOnClose, string name) =>
        $"memory:${buffer.ToInt64():X16},{bufferSize},{(freeBufferOnClose ? 1 : 0)}:{name}";

    // ---- FragmentSetTexture hook -------------------------------------
    // void FragmentSetTexture(int slot, grcTextureDX11* texture, u16 samplerStateHandler)
    // Binds a pixel shader texture slot, so every texture drawn through a pixel shader passes here.
    //
    // The function starts with
    //   48 89 5C 24 08  mov  [rsp+8], rbx
    //   57              push rdi
    // The first instruction is exactly 5 bytes and becomes `jmp rel32` to a stub allocated within 2 GB:
    //   stub+0x00: mov rax, <managed detour>; jmp rax       (rax is volatile on entry)
    //   stub+0x10: the stolen mov, then jmp [rip] to SetTexture+5 (the original)
    // The stolen mov has no RIP-relative operand, so it runs unchanged from the stub. The patch ends on
    // an instruction boundary and is one aligned 8-byte write.

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FragmentSetTextureFn(int slot, grcTextureDX11* texture, ushort samplerStateHandler);

    private const int StolenBytes = 5;

    private static FragmentSetTextureFn detourDelegate;   // keeps the thunk alive while the hook is installed
    private static delegate* unmanaged[Stdcall]<int, grcTextureDX11*, ushort, void> originalFragmentSetTexture;
    private static byte* hookStub;
    private static long originalPrologue;
    private static volatile bool hookInstalled;

    private static readonly ConcurrentQueue<string> hookLog = new();   // render thread -> script thread

    public static bool HookInstalled => hookInstalled;

    public static bool InstallTextureHook()
    {
        if (hookInstalled)
            return true;

        byte* target = (byte*)fragmentSetTextureAddr;
        if (target == null)
        {
            Log.Write("TextureSwap: hook not installed, FragmentSetTexture not found");
            return false;
        }

        if (((long)target & 7) != 0)
        {
            Log.Write($"TextureSwap: hook not installed, SetTexture at 0x{(long)target:X} is not 8-byte aligned");
            return false;
        }

        if (hookStub == null)
        {
            hookStub = AllocNear(target, 0x1000);
            if (hookStub == null)
            {
                Log.Write("TextureSwap: hook not installed, no free memory within 2 GB of SetTexture");
                return false;
            }
        }

        BuildReplacements();

        detourDelegate = FragmentSetTextureDetour;
        IntPtr detour = Marshal.GetFunctionPointerForDelegate(detourDelegate);

        byte* p = hookStub;
        // stub+0x00:  mov rax, imm64
        p[0] = 0x48;
        p[1] = 0xB8;
        *(long*)(p + 2) = detour.ToInt64();
        // stub+0x0A:  jmp rax
        p[10] = 0xFF;
        p[11] = 0xE0;

        // stub+0x10: stolen bytes; jmp qword ptr [rip+0]; dq target+StolenBytes
        byte* t = hookStub + 0x10;
        for (int i = 0; i < StolenBytes; i++) t[i] = target[i];
        byte* j = t + StolenBytes;
        j[0] = 0xFF;
        j[1] = 0x25;
        *(int*)(j + 2) = 0;
        *(long*)(j + 6) = (long)(target + StolenBytes);
        originalFragmentSetTexture = (delegate* unmanaged[Stdcall]<int, grcTextureDX11*, ushort, void>)t;
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)hookStub, (UIntPtr)0x20);

        long rel = (long)hookStub - ((long)target + 5);
        if (rel < int.MinValue || rel > int.MaxValue)
        {
            Log.Write($"TextureSwap: hook not installed, stub at 0x{(long)hookStub:X} is out of rel32 range");
            return false;
        }

        originalPrologue = *(long*)target;
        long patched = (originalPrologue & unchecked((long)0xFFFFFF0000000000)) | ((long)(uint)(int)rel << 8) | 0xE9;

        hookInstalled = true;
        if (!WriteCode(target, patched))
        {
            hookInstalled = false;
            Log.Write("TextureSwap: hook not installed, VirtualProtect failed");
            return false;
        }

        Log.Write($"TextureSwap: FragmentSetTexture hooked, stub at 0x{(long)hookStub:X}");
        return true;
    }

    // Puts the original bytes back. The stub memory is left allocated
    public static void RemoveTextureHook()
    {
        if (!hookInstalled)
            return;

        if (WriteCode((byte*)fragmentSetTextureAddr, originalPrologue))
            Log.Write("TextureSwap: SetTexture unhooked");
        else
            Log.Write("TextureSwap: unhook failed, VirtualProtect failed");
        hookInstalled = false;
        FlushHookLog();
    }

    // Script thread, every tick: writes the lines the detour queued.
    public static void Tick()
    {
        if (!hookInstalled)
            return;

        FlushHookLog();
    }

    private static void FlushHookLog()
    {
        while (hookLog.TryDequeue(out string line))
            Log.Write(line);
    }

    // ---- replacement textures --------------------------------------------------
    // A texture passed to FragmentSetTexture is swapped for the embedded DDS with the same name
    // ("ah_bboards1" -> textures.ah_bboards1.dds), when there is one. Each replacement is created once, on
    // the first call that needs it, and shared by every texture of that name.

    private sealed class Replacement
    {
        public string Key;              // embedded texture name, for GetEmbeddedTexture and the log
        public grcTextureDX11* Texture; // created texture, null until created or if creating it failed
        public bool Tried;              // creation attempted: a failure is never retried
    }

    private static readonly Dictionary<uint, Replacement> replacements = new();

    private static void BuildReplacements()
    {
        replacements.Clear();
        foreach (string key in EmbeddedTextures.Keys)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(key.ToLowerInvariant());
            uint hash;
            fixed (byte* p = name)
                hash = HashName(p, name.Length);

            if (replacements.TryGetValue(hash, out Replacement other))
            {
                Log.Write($"TextureSwap: '{key}' and '{other.Key}' have the same hash 0x{hash:X8}, '{key}' is not used");
                continue;
            }
            replacements[hash] = new Replacement { Key = key };
        }
    }

    private static uint HashName(byte* name, int maxLength = int.MaxValue)
    {
        uint h = 0;
        for (int i = 0; i < maxLength && name[i] != 0; i++)
        {
            byte c = name[i];
            if (c >= 'A' && c <= 'Z')
                c += 'a' - 'A';
            h += c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    private static grcTextureDX11* GetReplacement(grcTextureDX11* texture)
    {
        byte* name = (byte*)texture->name;
        if (name == null)
            return null;

        if (!replacements.TryGetValue(HashName(name), out Replacement r))
            return null;
        if (r.Texture != null || r.Tried)
            return r.Texture;
        return CreateReplacement(r);
    }

    private static grcTextureDX11* CreateReplacement(Replacement r)
    {
        grcTextureFactoryDX11* factory = TextureFactory;
        if (factory == null)
            return null;

        r.Tried = true;
        try
        {
            byte[] data = GetEmbeddedTexture(r.Key);
            if (data == null)
            {
                hookLog.Enqueue($"TextureSwap: cannot read embedded '{r.Key}', not swapping it");
                return null;
            }

            // Pinned only for the call: CreateByFilename opens, reads and closes the memory file before it returns.
            string fileName;
            fixed (byte* buffer = data)
            {
                fileName = MakeMemoryFileName((IntPtr)buffer, data.Length, false, r.Key + ".dds");
                r.Texture = factory->CreateByFilename(fileName);
            }

            if (r.Texture == null)
                hookLog.Enqueue($"TextureSwap: CreateByFilename('{fileName}') returned null, not swapping '{r.Key}'");
            else
                hookLog.Enqueue($"TextureSwap: replacement '{r.Key}' created at 0x{(long)r.Texture:X}");
            return r.Texture;
        }
        catch (Exception e)
        {
            hookLog.Enqueue($"TextureSwap: creating replacement '{r.Key}' failed: {e.Message}");
            return null;
        }
    }

    private static volatile bool replacementEnabled = true;

    public static bool ReplacementEnabled
    {
        get => replacementEnabled;
        set => replacementEnabled = value;
    }

    private static void FragmentSetTextureDetour(int slot, grcTextureDX11* texture, ushort samplerStateHandle)
    {
        grcTextureDX11* passed = texture;
        try
        {
            if (replacementEnabled && texture != null)
            {
                grcTextureDX11* replacement = GetReplacement(texture);
                if (replacement != null)
                    passed = replacement;
            }
        }
        catch
        {
        }

        originalFragmentSetTexture(slot, passed, samplerStateHandle);
    }

    private static bool WriteCode(byte* addr, long value)
    {
        if (!VirtualProtect((IntPtr)addr, (UIntPtr)8, PAGE_EXECUTE_READWRITE, out uint old))
            return false;
        Interlocked.Exchange(ref *(long*)addr, value);
        VirtualProtect((IntPtr)addr, (UIntPtr)8, old, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)addr, (UIntPtr)8);
        return true;
    }

    // Finds free memory within rel32 reach of target (allocations are 64 KB aligned).
    private static byte* AllocNear(byte* target, uint size)
    {
        const long Range = 0x7FF00000;
        const long Granularity = 0x10000;
        static long AlignUp(long v) => (v + Granularity - 1) & ~(Granularity - 1);

        long lo = Math.Max((long)target - Range, Granularity);
        long hi = (long)target + Range;
        for (long p = AlignUp(lo); p + size <= hi;)
        {
            if (VirtualQuery((IntPtr)p, out MEMORY_BASIC_INFORMATION mbi, (UIntPtr)sizeof(MEMORY_BASIC_INFORMATION)) == UIntPtr.Zero)
                break;

            long regionEnd = mbi.BaseAddress.ToInt64() + (long)mbi.RegionSize.ToUInt64();
            if (mbi.State == MEM_FREE && p + size <= regionEnd)
            {
                IntPtr r = VirtualAlloc((IntPtr)p, (UIntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (r != IntPtr.Zero)
                    return (byte*)r;
            }

            long next = AlignUp(regionEnd);
            p = next > p ? next : p + Granularity;
        }
        return null;
    }

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_FREE = 0x10000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION buffer, UIntPtr length);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Explicit)]
    struct grcTextureFactoryDX11
    {
        [FieldOffset(0)] VTable* vtable;


        public grcTextureDX11* CreateByFilename(string filename)
        {
            IntPtr filenamePtr = Marshal.StringToHGlobalAnsi(filename);
            grcTextureDX11* tex = this.vtable->CreateByFilename(ref this, filenamePtr.ToPointer(), null);
            Marshal.FreeHGlobal(filenamePtr);
            return tex;
        }


        [StructLayout(LayoutKind.Explicit)]
        struct VTable
        {
            [FieldOffset(0x20)] public delegate* unmanaged[Thiscall]<ref grcTextureFactoryDX11, void*, void*, grcTextureDX11*> CreateByFilename;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct grcTextureDX11
    {
        [FieldOffset(0x28)] public IntPtr name;
    }
}

