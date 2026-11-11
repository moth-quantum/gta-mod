// Force field on sealed roads, the game side: draws, pushes, adds particles and blips, using the logic
// in ForceField.cs. Calls natives, so script thread only.
//
// The wall colour comes from R_lut only, by way of EntanglementTable. Nothing here picks a colour.
// Blips and looped particles outlive the script, so Clear has to run on every way out.
//
// Unverified until seen in game: the particle name and whether it takes a tint, DRAW_POLY on rotated
// quads, and whether velocity holds a ped. docs/forcefield-test.md covers each. Also unverified (11 Nov
// visual pass): DRAW_TEXTURED_POLY_WITH_THREE_COLOURS over the centre texel of PlainTxd/PlainTexture
// (assumed close to white), and the frame rate with up to 1,536 triangles a wall in range. If the
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
    const string ParticleAsset = "core";
    const string ParticleName = "ent_amb_elec_crackle";
    const int ParticlesPerWall = 3;
    const float ParticleScale = 1f;
    const float ParticleLift = 1f;                 // metres above the road
    const int ParticleRetryMs = 5000;
    const float RemoveMargin = 20f;                // metres past the radius before removal, so nothing flickers on the boundary
    const int RagdollMs = 1200;
    const int RagdollGapMs = 2000;
    const int LogGapMs = 2000;                     // a held accelerator pushes every few ticks

    const float TestWallAhead = 25f;
    const float TestWallWidth = 12f;
    const int MaxTestWalls = 12;

    sealed class Site(Wall wall, bool test)
    {
        public readonly Wall Wall = wall;
        public readonly WallSegment Segment = ForceField.Segment(wall);
        public readonly WallTrack Track = new();
        public readonly bool Test = test;
        public readonly List<ParticleEffect> Particles = [];
        public Blip Blip;
        public int NextParticleTry;
    }

    static readonly List<Site> sites = [];
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
    static int nextRagdoll, nextPushLog;
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
    }

    static void Remove(Site site)
    {
        site.Blip?.Delete();
        site.Blip = null;
        StopParticles(site);
    }

    static void StopParticles(Site site)
    {
        foreach (var p in site.Particles) p?.Delete();
        site.Particles.Clear();
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

    /// <summary>Call every tick in every phase: walls are only up during the escape, and test walls at any time.</summary>
    public static void Tick()
    {
        if (sites.Count == 0) return;

        var ped = Game.Player.Character;
        if (!ped.Exists() || ped.IsDead)
        {
            Clear();
            return;
        }

        bool inVehicle = ped.IsInVehicle();
        Entity mover = inVehicle ? ped.CurrentVehicle : ped;
        var pos = mover.Position;
        var vel = mover.Velocity;
        var cam = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);

        foreach (var site in sites)
        {
            float dist = pos.DistanceTo2D(new Vector3(site.Segment.CentreX, site.Segment.CentreY, 0f));
            UpdateBlip(site, dist);
            if (dist > ForceField.CheckRadius)
            {
                site.Track.Forget();
                if (dist > ForceField.CheckRadius + RemoveMargin) StopParticles(site);
                continue;
            }

            var result = ForceField.Step(site.Segment, site.Track, inVehicle ? ForceField.Vehicle : ForceField.OnFoot, pos.X, pos.Y, pos.Z, vel.X, vel.Y);
            if (result.Kind != PushKind.None) Apply(result, ped, mover, inVehicle, ref pos, ref vel);

            if (table != null)
            {
                var middle = Draw(site, cam);
                UpdateParticles(site, middle);
            }
        }
    }

    static void Apply(in PushResult r, Ped ped, Entity mover, bool inVehicle, ref Vector3 pos, ref Vector3 vel)
    {
        float speed = vel.Length();
        if (Game.GameTime >= nextPushLog)
        {
            nextPushLog = Game.GameTime + LogGapMs;
            Log.Write($"forcefield: {(r.Kind == PushKind.PutBack ? "put back" : "pushed")} {(inVehicle ? "vehicle" : "on foot")} at {speed:0.0} m/s");
        }

        // A ped's animation is likely to override its velocity, so it goes limp first and takes the push that way.
        if (!inVehicle && Game.GameTime >= nextRagdoll)
        {
            nextRagdoll = Game.GameTime + RagdollGapMs;
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

    readonly struct Layer(float offset, float height, float alpha, float extraThickness)
    {
        public readonly float Offset = offset, Height = height, Alpha = alpha, ExtraThickness = extraThickness;
    }

    // One grid corner: where it is, and its colour (0 to 255) and alpha with the light applied.
    struct Corner
    {
        public float X, Y, Z, R, G, B;
        public int A;
    }

    static readonly Corner[,] corners = new Corner[ForceField.Cols + 1, ForceField.Rows + 1];

    // Two layers of Cols x Rows cells. Colour (for the view angle and film thickness), haze and the QPAM
    // light are worked out at every grid corner and blended across each triangle by the three-colour
    // native, so the wall has no facets. Without the plain texture, each triangle is drawn flat in its
    // corners' average colour, without the light. DRAW_POLY-style triangles show one side only, so each
    // is drawn in both windings. Returns the colour at the bottom middle of the wall.
    static Color Draw(Site site, Vector3 cam)
    {
        var s = site.Segment;
        bool smooth = PlainTextureReady() && light != null;
        double seconds = Game.GameTime / 1000.0;
        int frame = ForceField.LightFrame(Game.GameTime);
        float rise = (float)(ForceField.LightRise * seconds % ForceField.LightTileMetres);
        float width = s.Width;
        // The front layer goes on the viewer's side of the wall.
        float side = (cam.X - s.CentreX) * s.NormalX + (cam.Y - s.CentreY) * s.NormalY >= 0 ? 1f : -1f;
        Color middle = default;
        var layers = new[]
        {
            new Layer(0f, ForceField.WallHeight, 1f, 0f),
            new Layer(side * ForceField.FrontOffset, ForceField.FrontHeight, ForceField.FrontAlpha, ForceField.FrontThickness),
        };
        foreach (var layer in layers)
        {
            float ox = s.NormalX * layer.Offset, oy = s.NormalY * layer.Offset;
            for (int i = 0; i <= ForceField.Cols; i++)
                for (int j = 0; j <= ForceField.Rows; j++)
                {
                    float f = (float)i / ForceField.Cols, z = layer.Height * j / ForceField.Rows, along = (f - 0.5f) * width;
                    float px = s.PointX(f) + ox, py = s.PointY(f) + oy;
                    float cos = ForceField.ViewCos(s, px, py, s.Z + z, cam.X, cam.Y, cam.Z);
                    var (cr, cg, cb) = table.Colour(cos, ForceField.ThicknessAt(seconds, along, z) + layer.ExtraThickness);
                    int alpha = (int)Math.Round(ForceField.BaseAlpha * layer.Alpha * ForceField.Haze(along, z, layer.Height, width));
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
                    if (layer.Offset == 0f && i == ForceField.Cols / 2 && j == 0)
                        middle = Color.FromArgb(Math.Min(255, alpha), Byte(cr * 255), Byte(cg * 255), Byte(cb * 255));
                }

            for (int i = 0; i < ForceField.Cols; i++)
                for (int j = 0; j < ForceField.Rows; j++)
                {
                    // lower-right (i,j)(i+1,j)(i+1,j+1), then upper-left (i,j)(i+1,j+1)(i,j+1)
                    Triangle(corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], smooth);
                    Triangle(corners[i, j], corners[i + 1, j + 1], corners[i, j + 1], smooth);
                }
        }
        return middle;
    }

    static void Triangle(in Corner a, in Corner b, in Corner c, bool smooth)
    {
        if (a.A < 2 && b.A < 2 && c.A < 2) return;
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
            site.Blip.Sprite = BlipSprite.Standard;
            site.Blip.Color = BlipColor.Blue;
            site.Blip.Scale = 0.7f;
            site.Blip.IsShortRange = true;
            site.Blip.Name = "Sealed road";
        }
        else if (site.Blip != null && dist > ForceField.BlipRadius + RemoveMargin)
        {
            site.Blip.Delete();
            site.Blip = null;
        }
    }

    static void UpdateParticles(Site site, Color colour)
    {
        if (site.Particles.Count == 0 && Game.GameTime >= site.NextParticleTry) StartParticles(site);
        foreach (var p in site.Particles)
            if (p != null) p.Color = colour;
    }

    static void StartParticles(Site site)
    {
        site.NextParticleTry = Game.GameTime + ParticleRetryMs;
        asset.Request();
        if (!asset.IsLoaded) return;   // the next try picks it up

        var s = site.Segment;
        for (int i = 0; i < ParticlesPerWall; i++)
        {
            float f = (i + 0.5f) / ParticlesPerWall;
            var effect = World.CreateParticleEffect(asset, ParticleName, new Vector3(s.PointX(f), s.PointY(f), s.Z + ParticleLift), Vector3.Zero, ParticleScale);
            if (effect != null) site.Particles.Add(effect);
            else if (!particleFailureLogged)
            {
                particleFailureLogged = true;
                Log.Write($"forcefield: could not start particle {ParticleAsset}/{ParticleName}");
            }
        }
    }
}
