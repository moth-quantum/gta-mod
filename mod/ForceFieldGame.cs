// Force field on sealed roads, the game side: draws, pushes, adds particles and blips, using the logic
// in ForceField.cs. Calls natives, so script thread only.
//
// The wall colour comes from R_lut only, by way of EntanglementTable. Nothing here picks a colour.
// Blips and looped particles outlive the script, so Clear has to run on every way out.
//
// Unverified until seen in game: the particle name and whether it takes a tint, DRAW_POLY on rotated
// quads, and whether velocity holds a ped. docs/forcefield-test.md covers each.

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
        float thickness = ForceField.Thickness(Game.GameTime / 1000.0);

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
                var middle = Draw(site, cam, thickness);
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

    // Eight strips across the wall, each with the colour for its own view angle. DRAW_POLY shows one
    // side only, so each strip is drawn in both windings, 32 triangles a wall. Returns the middle strip's colour.
    static Color Draw(Site site, Vector3 cam, float thickness)
    {
        var s = site.Segment;
        float bottom = s.Z, top = s.Z + ForceField.WallHeight, midZ = (bottom + top) / 2;
        Color middle = default;
        for (int i = 0; i < ForceField.Strips; i++)
        {
            float f0 = (float)i / ForceField.Strips, f1 = (float)(i + 1) / ForceField.Strips, fm = (f0 + f1) / 2;
            float cos = ForceField.ViewCos(s, s.PointX(fm), s.PointY(fm), midZ, cam.X, cam.Y, cam.Z);
            var (cr, cg, cb) = table.Colour(cos, thickness);
            int r = (int)Math.Round(cr * 255), g = (int)Math.Round(cg * 255), b = (int)Math.Round(cb * 255);
            if (i == ForceField.Strips / 2) middle = Color.FromArgb(ForceField.WallAlpha, r, g, b);

            float x0 = s.PointX(f0), y0 = s.PointY(f0), x1 = s.PointX(f1), y1 = s.PointY(f1);
            Triangle(x0, y0, bottom, x1, y1, bottom, x1, y1, top, r, g, b);
            Triangle(x0, y0, bottom, x1, y1, top, x0, y0, top, r, g, b);
            Triangle(x1, y1, top, x1, y1, bottom, x0, y0, bottom, r, g, b);
            Triangle(x0, y0, top, x1, y1, top, x0, y0, bottom, r, g, b);
        }
        return middle;
    }

    static void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, int r, int g, int b) =>
        Function.Call(Hash.DRAW_POLY, x1, y1, z1, x2, y2, z2, x3, y3, z3, r, g, b, ForceField.WallAlpha);

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
