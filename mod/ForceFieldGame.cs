// Force field on sealed roads, the game side: draws, pushes, adds particles and blips, using the logic
// in ForceField.cs. Calls natives, so script thread only.
//
// The wall colour comes from R_lut only, by way of EntanglementTable. Nothing here picks a colour.
// Blips outlive the script, so Clear has to run on every way out. Particles are one-shot bursts where a vehicle or ped hits a wall.
// Walls near the player push every vehicle and ped near them, not just the player.
//
// Unverified until seen in game: the particle name and whether it takes a tint, DRAW_POLY on rotated
// quads, and whether velocity holds a ped. docs/forcefield-test.md covers each. Also unverified (11 Nov
// visual pass): DRAW_TEXTURED_POLY_WITH_THREE_COLOURS over the centre texel of PlainTxd/PlainTexture
// (assumed close to white). 1,536 calls for one wall crashed the game (about 1,400 a frame is the
// limit), so it's now 288 calls a wall, the nearest 4 walls only, and never more than 1,200 a frame. If the
// texture dictionary doesn't stream in, the walls fall back to flat DRAW_POLY cells without the light.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QDay;

public static class ForceFieldGame
{
    const string ParticleAsset = "veh_xs_vehicle_mods";
    const string ParticleName = "exp_xs_mine_kinetic";
    const float ParticleScale = 1f;
    const float ParticleLift = 1f;                 // metres above the road
    const int ParticleGapMs = 400;                 // a held accelerator pushes every few ticks; one burst per this long
    const float RemoveMargin = 20f;                // metres past the radius before removal, so nothing flickers on the boundary
    const int RagdollMs = 1200;
    const int RagdollGapMs = 2000;
    const int LogGapMs = 2000;                     // a held accelerator pushes every few ticks
    const float TrackMargin = 30f;                 // metres past a wall's half width where an entity starts being tracked for it

    const float TestWallAhead = 25f;
    const float TestWallWidth = 12f;
    const int MaxTestWalls = 12;

    sealed class Site(Wall wall, bool test)
    {
        public readonly Wall Wall = wall;
        public readonly WallSegment Segment = ForceField.Segment(wall);
        public readonly Dictionary<int, Tracked> Tracks = [];   // by entity handle, for the entities near this wall
        public readonly bool Test = test;
        public Blip Blip;
        public Color Colour = Color.White;         // the colour it was last drawn in, for the hit burst
    }

    // One entity's side of one wall, and the last tick it was near enough to be checked.
    sealed class Tracked
    {
        public readonly WallTrack Track = new();
        public int Seen;
    }

    static readonly List<Site> sites = [];
    static readonly List<Site> active = [];        // walls within CheckRadius of the player this tick
    static readonly List<int> stale = [];
    static readonly Dictionary<int, int> nextRagdoll = [];   // by ped handle
    static int tickId;
    static readonly ParticleEffectAsset asset = new(ParticleAsset);
    static readonly EntanglementTable table = LoadTable();
    static readonly LightField light = LoadLight();

    // The plain texture under the per-corner colours. Rockstar draws the Deadline trails with this native
    // and this dictionary; every corner reads the centre texel, so it acts as one flat colour. Change both
    // here if that texel turns out not to be white.
    const string PlainTxd = "deadline", PlainTexture = "deadline_trail_01";
    const float PlainU = 0.5f, PlainV = 0.5f;
    const int PlainTimeoutMs = 10000;
    static int plainRequestedAt = -1;
    static bool plainReady, plainGaveUp;
    static int nextPushLog, nextParticle;
    static bool particleFailureLogged;

    /// <summary>Null with the reason logged if the embedded table is bad. The walls still push, but aren't drawn: no colour is made up.</summary>
    static EntanglementTable LoadTable()
    {
        try
        {
            using var s = typeof(ForceFieldGame).Assembly.GetManifestResourceStream("recorded.entanglement_lut.bin")
                          ?? throw new FileNotFoundException("no embedded entanglement_lut.bin");
            using MemoryStream m = new();
            s.CopyTo(m);
            var t = EntanglementTable.Parse(m.ToArray());
            Log.Write(t.LogLine);
            return t;
        }
        catch (Exception e)
        {
            Log.Write($"forcefield: table refused, walls will push but not draw: {e.Message}");
            return null;
        }
    }

    /// <summary>Puts these walls up, replacing any from an earlier call. Debug test walls stay.</summary>
    public static void SetWalls(IEnumerable<Wall> walls)
    {
        for (int i = sites.Count - 1; i >= 0; i--)
            if (!sites[i].Test)
            {
                Remove(sites[i]);
                sites.RemoveAt(i);
            }
        foreach (var w in walls) sites.Add(new(w, false));
        Log.Write($"forcefield: {sites.Count} walls up");
    }

    /// <summary>Removes every wall, blip and particle. Safe to call with nothing up.</summary>
    public static void Clear()
    {
        if (plainRequestedAt >= 0)
        {
            Function.Call(Hash.SET_STREAMED_TEXTURE_DICT_AS_NO_LONGER_NEEDED, PlainTxd);
            plainRequestedAt = -1;
            plainReady = false;
        }
        RoadTestGame.Clear();   // before the check below, and on every way out that clears the walls
        if (sites.Count == 0) return;
        foreach (var site in sites) Remove(site);
        Log.Write($"forcefield: cleared {sites.Count} walls");
        sites.Clear();
        active.Clear();
        nextRagdoll.Clear();
    }

    static void Remove(Site site)
    {
        site.Blip?.Delete();
        site.Blip = null;
        site.Tracks.Clear();
    }

    /// <summary>Debug only: a wall across the way the player is facing, going through ForceField.Segment like any other.</summary>
    public static void DropTestWall()
    {
        var ped = Game.Player.Character;
        Entity ent = ped.IsInVehicle() ? ped.CurrentVehicle : ped;
        int count = 0;
        foreach (var site in sites) if (site.Test) count++;
        if (count >= MaxTestWalls)
        {
            Log.Write($"DEBUG F4 ignored, already {MaxTestWalls} test walls");
            return;
        }

        float heading = ent.Heading;
        var (fx, fy) = ForceField.Forward(heading);
        var at = ent.Position + new Vector3(fx, fy, 0f) * TestWallAhead;
        // From just above the player, so a bridge overhead isn't taken for the road.
        float ground = World.GetGroundHeight(new Vector3(at.X, at.Y, ent.Position.Z + 3f));
        float z = ground > 0f && Math.Abs(ground - ent.Position.Z) < 15f ? ground : ent.Position.Z - 1f;
        sites.Add(new(new() { X = at.X, Y = at.Y, Z = z, Heading = heading, Width = TestWallWidth }, true));
        Log.Write($"DEBUG F4 test wall {count + 1} at {at.X:0.0}, {at.Y:0.0}, {z:0.0} heading {heading:0.0} width {TestWallWidth}");
    }

    /// <summary>The most recent F4 wall, for the road switching experiment.</summary>
    public static bool LastTestWall(out WallSegment segment)
    {
        for (int i = sites.Count - 1; i >= 0; i--)
            if (sites[i].Test)
            {
                segment = sites[i].Segment;
                return true;
            }
        segment = default;
        return false;
    }

    /// <summary>
    /// Call every tick in every phase: walls are only up during the escape, and test walls at any time.
    /// Only walls within CheckRadius of the player are checked, and they push every vehicle and ped near
    /// them, the player included. The world's vehicles and peds are fetched once a tick, and only if a wall is near.
    /// </summary>
    public static void Tick()
    {
        if (sites.Count == 0) return;

        var player = Game.Player.Character;
        if (!player.Exists() || player.IsDead)
        {
            Clear();
            return;
        }

        tickId++;
        Entity playerMover = player.IsInVehicle() ? player.CurrentVehicle : player;
        var pos = playerMover.Position;
        var cam = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);

        // Only the nearest MaxDrawnWalls walls in range are drawn, and never more than MaxDrawCallsPerFrame calls.
        drawn.Clear();
        active.Clear();
        foreach (var site in sites)
        {
            float d = pos.DistanceTo2D(new Vector3(site.Segment.CentreX, site.Segment.CentreY, 0f));
            UpdateBlip(site, d);
            if (d <= ForceField.CheckRadius)
            {
                drawn.Add((d, site));
                active.Add(site);
            }
            else site.Tracks.Clear();
        }
        drawn.Sort((a, b) => a.dist.CompareTo(b.dist));
        if (drawn.Count > ForceField.MaxDrawnWalls) drawn.RemoveRange(ForceField.MaxDrawnWalls, drawn.Count - ForceField.MaxDrawnWalls);
        callsThisFrame = 0;

        if (active.Count > 0)
        {
            int handle = playerMover.Handle;
            foreach (var v in World.GetAllVehicles()) Check(v, v.Handle == handle);
            foreach (var p in World.GetAllPeds()) Check(p, p.Handle == handle);
            foreach (var site in active) Prune(site);
            if (nextRagdoll.Count > 256) PruneRagdoll();
        }

        if (table != null)
            foreach (var (_, site) in drawn) site.Colour = Draw(site, cam);
    }

    // One entity against the active walls. Its position is read once; whether it can be pushed, its velocity
    // and everything else only when it's near a wall, so the hundreds of far-off entities cost one native each.
    static void Check(Entity e, bool isPlayer)
    {
        var pos = e.Position;
        Vector3 vel = default;
        bool vetted = false;
        bool isVehicle = e is Vehicle;
        foreach (var site in active)
        {
            var s = site.Segment;
            float dx = pos.X - s.CentreX, dy = pos.Y - s.CentreY, near = s.Width / 2 + TrackMargin;
            if (dx * dx + dy * dy > near * near) continue;

            if (!vetted)
            {
                if (!Pushable(e)) return;
                vel = e.Velocity;
                vetted = true;
            }

            int h = e.Handle;
            if (!site.Tracks.TryGetValue(h, out var t)) site.Tracks[h] = t = new();
            t.Seen = tickId;
            var result = ForceField.Step(s, t.Track, isVehicle ? ForceField.Vehicle : ForceField.OnFoot, pos.X, pos.Y, pos.Z, vel.X, vel.Y);
            if (result.Kind != PushKind.None)
            {
                Burst(site, pos);   // before Apply moves it, so it's level with where it hit
                Apply(result, e, isVehicle, isPlayer, ref pos, ref vel);
            }
        }
    }

    // Trains are on rails, and moving one by velocity would derail it. A ped in a vehicle moves with it, so
    // the vehicle is what's pushed. The dead are left where they fall.
    static bool Pushable(Entity e) => e switch
    {
        Vehicle v => !v.Model.IsTrain,
        Ped p => !p.IsDead && !p.IsInVehicle(),
        _ => false,
    };

    // Forgets entities that weren't near this wall this tick, and those gone from the world.
    static void Prune(Site site)
    {
        stale.Clear();
        foreach (var kv in site.Tracks)
            if (kv.Value.Seen != tickId) stale.Add(kv.Key);
        foreach (int h in stale) site.Tracks.Remove(h);
    }

    static void PruneRagdoll()
    {
        stale.Clear();
        int now = Game.GameTime;
        foreach (var kv in nextRagdoll)
            if (kv.Value <= now) stale.Add(kv.Key);
        foreach (int h in stale) nextRagdoll.Remove(h);
    }

    static void Apply(in PushResult r, Entity mover, bool isVehicle, bool isPlayer, ref Vector3 pos, ref Vector3 vel)
    {
        int now = Game.GameTime;
        if (now >= nextPushLog)
        {
            nextPushLog = now + LogGapMs;
            Log.Write($"forcefield: {(r.Kind == PushKind.PutBack ? "put back" : "pushed")} {(isPlayer ? "player" : "npc")} {(isVehicle ? "vehicle" : "on foot")} at {vel.Length():0.0} m/s");
        }

        // A ped's animation is likely to override its velocity, so it goes limp first and takes the push that way.
        if (!isVehicle && mover is Ped ped && (!nextRagdoll.TryGetValue(ped.Handle, out int next) || now >= next))
        {
            nextRagdoll[ped.Handle] = now + RagdollGapMs;
            ped.Ragdoll(RagdollMs, RagdollType.Balance);
        }

        if (r.Kind == PushKind.PutBack)
        {
            pos = new Vector3(r.X, r.Y, pos.Z);
            mover.PositionNoOffset = pos;
        }
        vel = new Vector3(r.Vx, r.Vy, vel.Z);
        mover.Velocity = vel;
    }

    /// <summary>The embedded QPAM light frames, or null with the reason logged: the walls are then drawn without the light.</summary>
    static LightField LoadLight()
    {
        try
        {
            var frames = new byte[ForceField.LightFrames][];
            int size = 0;
            for (int f = 0; f < frames.Length; f++)
            {
                using var s = typeof(ForceFieldGame).Assembly.GetManifestResourceStream($"forcefield.light_{f}.png")
                              ?? throw new FileNotFoundException($"no embedded light_{f}.png");
                using var bmp = new Bitmap(s);
                if (f == 0) size = bmp.Width;
                if (bmp.Width != size || bmp.Height != size) throw new InvalidDataException($"light_{f}.png is {bmp.Width} x {bmp.Height}, expected {size} x {size}");
                var px = new byte[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        px[y * size + x] = bmp.GetPixel(x, y).R;
                frames[f] = px;
            }
            var field = new LightField(frames, size, ForceField.LightCell);
            Log.Write($"forcefield: QPAM light loaded, {field.Frames} frames of {size} x {size}");
            return field;
        }
        catch (Exception e)
        {
            Log.Write($"forcefield: no QPAM light, walls drawn without it: {e.Message}");
            return null;
        }
    }

    /// <summary>Streams the plain texture in, once per set of walls; after PlainTimeoutMs without it, falls back for good.</summary>
    static bool PlainTextureReady()
    {
        if (plainReady) return true;
        if (plainGaveUp) return false;
        int now = Game.GameTime;
        if (plainRequestedAt < 0) plainRequestedAt = now;
        Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, PlainTxd, false);
        if (Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, PlainTxd))
        {
            plainReady = true;
            Log.Write($"forcefield: {PlainTxd} streamed in, drawing smooth per-corner colours with the QPAM light");
            return true;
        }
        if (now - plainRequestedAt > PlainTimeoutMs)
        {
            plainGaveUp = true;
            Log.Write($"forcefield: {PlainTxd} didn't stream in within {PlainTimeoutMs / 1000} s, drawing flat cells without the light");
        }
        return false;
    }

    // One grid corner: where it is, and its colour (0 to 255) and alpha with the light applied.
    struct Corner
    {
        public float X, Y, Z, R, G, B;
        public int A;
    }

    static readonly Corner[,] corners = new Corner[ForceField.Cols + 1, ForceField.Rows + 1];
    static readonly List<(float dist, Site site)> drawn = [];
    static int callsThisFrame;

    // One layer of Cols x Rows cells. Colour (for the view angle and film thickness), haze and the QPAM
    // light are worked out at every grid corner and blended across each triangle by the three-colour
    // native, so the wall has no facets. Without the plain texture, each triangle is drawn flat in its
    // corners' average colour, without the light. A triangle shows one side only, so each is drawn in
    // both windings: CallsPerWall calls. Returns the colour at the bottom middle of the wall.
    static Color Draw(Site site, Vector3 cam)
    {
        var s = site.Segment;
        bool smooth = PlainTextureReady() && light != null;
        double seconds = Game.GameTime / 1000.0;
        int frame = ForceField.LightFrame(Game.GameTime);
        float rise = (float)(ForceField.LightRise * seconds % ForceField.LightTileMetres);
        float width = s.Width, height = ForceField.WallHeight;
        Color middle = default;
        for (int i = 0; i <= ForceField.Cols; i++)
            for (int j = 0; j <= ForceField.Rows; j++)
            {
                float f = (float)i / ForceField.Cols, z = height * j / ForceField.Rows, along = (f - 0.5f) * width;
                float px = s.PointX(f), py = s.PointY(f);
                float cos = ForceField.ViewCos(s, px, py, s.Z + z, cam.X, cam.Y, cam.Z);
                var (cr, cg, cb) = table.Colour(cos, ForceField.ThicknessAt(seconds, along, z));
                int alpha = (int)Math.Round(ForceField.BaseAlpha * ForceField.Haze(along, z, height, width));
                if (smooth)
                {
                    // u along the wall and v up it, in tiles; the image's v runs down.
                    float lit = light.Sample(frame, along / ForceField.LightTileMetres, -(z - rise) / ForceField.LightTileMetres);
                    cr = ForceField.Lit(cr, lit);
                    cg = ForceField.Lit(cg, lit);
                    cb = ForceField.Lit(cb, lit);
                    alpha = ForceField.LitAlpha(alpha, lit);
                }
                corners[i, j] = new Corner { X = px, Y = py, Z = s.Z + z, R = (float)(cr * 255), G = (float)(cg * 255), B = (float)(cb * 255), A = Math.Min(255, alpha) };
                if (i == ForceField.Cols / 2 && j == 0)
                    middle = Color.FromArgb(Math.Min(255, alpha), Byte(cr * 255), Byte(cg * 255), Byte(cb * 255));
            }

        for (int i = 0; i < ForceField.Cols; i++)
            for (int j = 0; j < ForceField.Rows; j++)
            {
                // lower-right (i,j)(i+1,j)(i+1,j+1), then upper-left (i,j)(i+1,j+1)(i,j+1)
                Triangle(corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], smooth);
                Triangle(corners[i, j], corners[i + 1, j + 1], corners[i, j + 1], smooth);
            }
        return middle;
    }

    static void Triangle(in Corner a, in Corner b, in Corner c, bool smooth)
    {
        if (a.A < 2 && b.A < 2 && c.A < 2) return;
        if (callsThisFrame + 2 > ForceField.MaxDrawCallsPerFrame) return;   // the hard stop: the game crashes past ~1,400
        callsThisFrame += 2;
        if (smooth)
        {
            Smooth(a, b, c);
            Smooth(c, b, a);
        }
        else
        {
            int r = Byte((a.R + b.R + c.R) / 3), g = Byte((a.G + b.G + c.G) / 3), bl = Byte((a.B + b.B + c.B) / 3), al = (a.A + b.A + c.A) / 3;
            Poly(a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, r, g, bl, al);
            Poly(c.X, c.Y, c.Z, b.X, b.Y, b.Z, a.X, a.Y, a.Z, r, g, bl, al);
        }
    }

    // The native takes each corner's R, G and B as floats (cast to int inside) and its alpha as an int.
    static void Smooth(in Corner a, in Corner b, in Corner c) =>
        Function.Call(Hash.DRAW_TEXTURED_POLY_WITH_THREE_COLOURS,
                      a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z,
                      a.R, a.G, a.B, a.A, b.R, b.G, b.B, b.A, c.R, c.G, c.B, c.A,
                      PlainTxd, PlainTexture,
                      PlainU, PlainV, 1f, PlainU, PlainV, 1f, PlainU, PlainV, 1f);

    static int Byte(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)Math.Round(v);

    static void Poly(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, int r, int g, int b, int a) =>
        Function.Call(Hash.DRAW_POLY, x1, y1, z1, x2, y2, z2, x3, y3, z3, r, g, b, a);

    static void UpdateBlip(Site site, float dist)
    {
        if (site.Blip == null && dist <= ForceField.BlipRadius)
        {
            site.Blip = World.CreateBlip(new Vector3(site.Segment.CentreX, site.Segment.CentreY, site.Segment.Z));
            site.Blip.Sprite = BlipSprite.CrossTheLine;
            site.Blip.Color = BlipColor.GreyDark;
            site.Blip.Scale = 1.0f;
            site.Blip.IsShortRange = true;
            site.Blip.Name = "Sealed Road";
            site.Blip.RotationFloat = site.Wall.Heading;
        }
        else if (site.Blip != null && dist > ForceField.BlipRadius + RemoveMargin)
        {
            site.Blip.Delete();
            site.Blip = null;
        }
    }

    /// <summary>A one-shot burst on the wall, at the point nearest what hit it, tinted to the wall's colour. At most one per ParticleGapMs.</summary>
    static void Burst(Site site, Vector3 at)
    {
        if (Game.GameTime < nextParticle) return;
        asset.Request();
        if (!asset.IsLoaded) return;   // the next hit picks it up
        nextParticle = Game.GameTime + ParticleGapMs;

        // The point on the wall nearest what hit it.
        var w = site.Segment;
        float dx = w.BX - w.AX, dy = w.BY - w.AY, len2 = dx * dx + dy * dy;
        float f = len2 > 0f ? Math.Max(0f, Math.Min(1f, ((at.X - w.AX) * dx + (at.Y - w.AY) * dy) / len2)) : 0.5f;

        var c = site.Colour;
        Function.Call(Hash.SET_PARTICLE_FX_NON_LOOPED_COLOUR, c.R / 255f, c.G / 255f, c.B / 255f);
        if (!World.CreateParticleEffectNonLooped(asset, ParticleName, new Vector3(w.PointX(f), w.PointY(f), w.Z + ParticleLift), Vector3.Zero, ParticleScale)
            && !particleFailureLogged)
        {
            particleFailureLogged = true;
            Log.Write($"forcefield: could not start particle {ParticleAsset}/{ParticleName}");
        }
    }
}
