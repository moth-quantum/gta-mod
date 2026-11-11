using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using QDay;

/// <summary>The force field's maths only. Whether any of it looks or feels right needs the game: docs/forcefield-test.md.</summary>
static class ForceFieldChecks
{
    static Action<bool, string> Check;

    static bool Near(double a, double b, double tolerance = 1e-3) => Math.Abs(a - b) <= tolerance;

    public static void Run(DistrictGraph g, string districtJson, Action<bool, string> check)
    {
        Check = check;
        Segments(g, districtJson);
        Sides();
        Pushes();
        Crossings();
        FullSpeed();
        Table();
        Bake();
        RoadTestChecks();
    }

    // ---- the road switching experiment ------------------------------------------------
    static void RoadTestChecks()
    {
        // A wall on a road running north: the area starts past it and carries on north.
        var wall = ForceField.Segment(At(100, 200, 30, 0));
        var a = RoadTest.Stretch(wall, 20);
        Check(Near(a.OriginX, 100) && Near(a.OriginY, 205) && Near(a.ExtentX, 100) && Near(a.ExtentY, 225), "the area runs from 5 m past the wall for 20 m, the way the wall's normal points");
        Check(Near(a.Width, 12), "the area is as wide as the wall");
        Check(a.OriginZ < 30 && a.ExtentZ > 30, "the area's height band straddles the road");
        Check(Near(a.Length, 20) && Near(RoadTest.Stretch(wall, 50).Length, 50), "the lengths are as asked for");

        Check(a.Contains(100, 215, 30), "a node on the road past the wall is in the area");
        Check(a.Contains(105, 210, 30) && !a.Contains(107, 210, 30), "the area's edges across the road are at half its width");
        Check(!a.Contains(100, 200, 30), "the wall itself is outside the area");
        Check(!a.Contains(100, 204, 30), "the gap past the wall is outside the area");
        Check(!a.Contains(100, 195, 30), "the player's side of the wall is outside the area");
        Check(!a.Contains(100, 226, 30), "beyond the far edge is outside the area");
        Check(!a.Contains(100, 215, 45), "a road high overhead is outside the area");
        Check(!a.Contains(100, 215, 15), "a road far below is outside the area");

        var (ex, ey) = ForceField.Forward(90);
        var west = RoadTest.Stretch(ForceField.Segment(At(100, 200, 30, 90)), 50);
        Check(Near(west.OriginX, 100 + ex * 5) && Near(west.OriginY, 200 + ey * 5) && Near(west.ExtentX, 100 + ex * 55) && Near(west.ExtentY, 200 + ey * 55), "a road running west gets its area further west");
        foreach (float heading in new float[] { 0, 37, 90, 145, 180, 233, 270, 311 })
        {
            var s = ForceField.Segment(At(-50, 80, 10, heading));
            var area = RoadTest.Stretch(s, 50);
            bool wallOut = true;
            for (int i = 0; i <= 8; i++) wallOut &= !area.Contains(s.PointX(i / 8f), s.PointY(i / 8f), s.Z);
            Check(wallOut, $"no part of a wall at heading {heading} is in its area");
            var (cx, cy) = area.Corner(-1, false);
            var (dx, dy) = area.Corner(1, true);
            Check(Near(area.Along(cx, cy), 0) && Near(Math.Abs(area.Across(cx, cy)), 6) && Near(area.Along(dx, dy), 50) && Near(Math.Abs(area.Across(dx, dy)), 6), $"the outline drawn at heading {heading} matches the area");
        }

        var v = RoadTest.Variants;
        Check(v.Length == 16, "16 variants: eight flag combinations at two lengths");
        Check(v.Select(x => (x.Length, x.Flag1, x.Flag2, x.Flag3)).Distinct().Count() == 16, "no variant repeats");
        Check(v.Select((x, i) => x.Number == i + 1 && x.Count == 16).All(ok => ok), "variants are numbered in order");
        Check(v.Take(8).All(x => !x.Flag3) && v.Skip(8).All(x => x.Flag3), "the network flag stays false until the rest are done");
        Check(v[3].Label == "variant 4/16 length 50 m flags false,true,false", "the label says which variant was used");

        var inside = new[] { true, true, false, false };
        var r = RoadTest.Compare([false, true, false, true], [true, true, false, true], inside);
        Check(r.InsideTurnedOff == 1 && r.InsideOff == 2 && r.Inside == 2 && !r.SameAsFirst, "a node turning off in the area is counted");
        r = RoadTest.Compare([false, false, false, true], [false, false, true, false], inside);
        Check(r.OutsideTurnedOff == 1 && r.OutsideTurnedOn == 1 && r.InsideTurnedOff == 0, "a change outside the area is counted apart, in both directions");
        Check(RoadTest.Compare([true, false, true, false], [true, false, true, false], inside).SameAsFirst, "a node that was already off, like an alley, isn't a change");

        Sequence();
    }

    // Plays the run with a clock. Applied is what decides whether a way out has to undo the road.
    static void Sequence()
    {
        const int settle = 1500, route = 2000;
        RoadTestRun run = new(settle, route);
        int now = 1000;
        List<RoadAction> seen = [];
        void RunUntilIdle(RoadTestRun r)
        {
            for (int i = 0; i < 200 && (r.Active && !r.Idle); i++)
            {
                now += 100;
                var a = r.Tick(now);
                if (a != RoadAction.None) seen.Add(a);
            }
        }

        Check(run.Tick(now) == RoadAction.None && !run.Applied && !run.Active, "nothing happens before the first press");
        Check(!run.Stop(), "stopping with nothing on needs no undo");
        Check(run.Press(now) == RoadAction.Prepare && !run.Applied, "the first press only finds the nodes and sets a route");
        Check(run.Press(now) == RoadAction.Busy, "a press while measuring is ignored");
        Check(!run.Stop(), "stopping before anything was switched off needs no undo");

        run.Press(now);
        seen.Clear();
        RunUntilIdle(run);
        Check(seen.SequenceEqual([RoadAction.Apply, RoadAction.ReadAfterApply, RoadAction.RouteOn, RoadAction.ReadRoute]), "a variant is applied, read as it stands, then the route is set again and read");
        Check(run.Applied && run.Idle && run.Current.Number == 1, "the first variant stays on until the next press");

        List<int> used = [run.Current.Number];
        bool everyOneUndone = true;
        while (true)
        {
            seen.Clear();
            var press = run.Press(now);
            everyOneUndone &= press == RoadAction.Undo && !run.Applied;
            RunUntilIdle(run);
            if (!run.Active) break;
            used.Add(run.Current.Number);
            everyOneUndone &= seen.Take(5).SequenceEqual([RoadAction.ReadAfterUndo, RoadAction.RouteOn, RoadAction.ReadRoute, RoadAction.Apply, RoadAction.ReadAfterApply]);
        }
        Check(used.SequenceEqual(Enumerable.Range(1, 16)), "pressing on tries each variant once, in order");
        Check(everyOneUndone, "each variant is undone and read before the next is applied");
        Check(seen.SequenceEqual([RoadAction.ReadAfterUndo, RoadAction.RouteOn, RoadAction.ReadRoute, RoadAction.Finish]) && !run.Applied, "the run finishes with the road back on");

        // Every moment a way out could come: exactly those with a variant on have to undo.
        bool undoRight = true;
        for (int steps = 0; steps < 60; steps++)
        {
            RoadTestRun r = new(settle, route);
            int t = 0;
            r.Press(t);
            bool on = false;
            for (int i = 0; i < steps; i++)
            {
                t += 500;
                var a = r.Tick(t);
                if (a == RoadAction.Apply) on = true;
                if (r.Idle)
                {
                    if (r.Press(t) == RoadAction.Undo) on = false;
                }
                else if (a == RoadAction.Undo) on = false;
            }
            undoRight &= r.Stop() == on && !r.Applied && !r.Active;
        }
        Check(undoRight, "whenever a way out comes, it undoes the road exactly when a variant is on");

        run = new(settle, route);
        run.Press(0);
        while (!run.Idle) { now += 100; run.Tick(now); }
        Check(run.Stop() && !run.Stop(), "undoing happens once, so clean-up running twice is safe");
        run.Press(now);
        Check(!run.Applied && run.Active, "a run can start again after being stopped");
    }

    static Wall At(float x, float y, float z, float heading, float width = 12f) => new() { X = x, Y = y, Z = z, Heading = heading, Width = width };

    // ---- a wall record as a line -------------------------------------------------------
    static void Segments(DistrictGraph g, string districtJson)
    {
        var (fx, fy) = ForceField.Forward(0);
        Check(Near(fx, 0) && Near(fy, 1), "heading 0 points to +Y");
        (fx, fy) = ForceField.Forward(90);
        Check(Near(fx, -1) && Near(fy, 0), "heading 90 points to -X, as GTA's turn counter-clockwise");

        // A road running north at heading 0: the wall lies east to west, across it.
        var s = ForceField.Segment(At(100, 200, 30, 0));
        Check(Near(s.AX, 94) && Near(s.BX, 106) && Near(s.AY, 200) && Near(s.BY, 200), "heading 0 puts the wall across a north-south road");
        Check(Near(s.NormalX, 0) && Near(s.NormalY, 1), "the wall's normal runs along the road");
        Check(Near(s.Z, 30), "the wall stands on the record's height");

        // A road running west at heading 90: the wall lies north to south.
        s = ForceField.Segment(At(0, 0, 0, 90));
        Check(Near(s.AX, 0) && Near(s.BX, 0) && Near(Math.Abs(s.AY - s.BY), 12), "heading 90 puts the wall across an east-west road");

        foreach (var heading in Enumerable.Range(0, 72).Select(i => i * 5f))
        {
            s = ForceField.Segment(At(50, -70, 10, heading, 17));
            Check(Near(s.Width, 17), "the wall is as wide as its record at any heading");
            Check(Near(s.CentreX, 50) && Near(s.CentreY, -70), "the wall is centred on its record at any heading");
            (fx, fy) = ForceField.Forward(heading);
            Check(Near((s.BX - s.AX) * fx + (s.BY - s.AY) * fy, 0, 1e-2), "the wall is square to the road at any heading");
        }

        // Placeholder only: real headings follow real roads, not the line between district centres.
        if (JsonNode.Parse(districtJson)["placeholder"] != null)
            foreach (var l in g.Links)
                foreach (var w in l.Walls)
                {
                    var (x, y) = ForceField.Forward(w.Heading);
                    double dx = g.Centres[l.B][0] - g.Centres[l.A][0], dy = g.Centres[l.B][1] - g.Centres[l.A][1];
                    double cos = (x * dx + y * dy) / Math.Sqrt(dx * dx + dy * dy);
                    Check(cos > Math.Cos(Math.PI / 180), $"placeholder wall on link {l.A}-{l.B} has its heading along the link");
                }
    }

    static void Sides()
    {
        var s = ForceField.Segment(At(0, 0, 0, 0));
        Check(ForceField.SideOf(s, 0, 5) == 1 && ForceField.SideOf(s, 0, -5) == -1, "side of a wall");
        Check(ForceField.SideOf(s, 500, 5) == 1, "the side is of the wall's whole line, not just its width");
        Check(ForceField.InBand(s, 0) && ForceField.InBand(s, ForceField.WallHeight) && ForceField.InBand(s, -ForceField.BandBelow), "the band covers the road and the wall's height");
        Check(!ForceField.InBand(s, ForceField.WallHeight + 1) && !ForceField.InBand(s, -ForceField.BandBelow - 1), "a road above or below is outside the band");
    }

    // ---- pushing ---------------------------------------------------------------------------
    static PushResult Step(WallSegment s, WallTrack t, Mover m, float x, float y, float vx, float vy, float z = 1) => ForceField.Step(s, t, m, x, y, z, vx, vy);

    static void Pushes()
    {
        var car = ForceField.Vehicle;
        var s = ForceField.Segment(At(0, 0, 0, 0));
        WallTrack t = new();

        Check(Step(s, t, car, 0, -100, 0, 20).Kind == PushKind.None, "the first tick only learns the side");
        Check(t.Known && t.Side == -1, "the side comes from the approach");
        Check(Step(s, t, car, 0, -20, 0, 20).Kind == PushKind.None, "no push out of range");

        var near = Step(s, t, car, 3, -4, 5, 20);
        Check(near.Kind == PushKind.Push, "moving towards the wall within range is a push");
        Check(Near(near.Vx, 5), "a push keeps sideways speed");
        Check(Near(near.Vy, -20 * ForceField.Restitution), "a push reverses the approach speed");

        var slow = Step(s, t, car, 0, -4, 0, 1);
        Check(slow.Kind == PushKind.Push && Near(slow.Vy, -car.MinPushSpeed), "a slow approach is still pushed at the minimum speed");

        Check(Step(s, t, car, 0, -4, 0, -5).Kind == PushKind.None, "moving away isn't pushed");
        Check(Step(s, t, car, 0, -4, 30, 0).Kind == PushKind.None, "driving alongside isn't pushed");
        Check(Step(s, t, car, 0, -4, 0, 0).Kind == PushKind.None, "standing still isn't pushed");
        Check(Step(s, t, car, 30, -4, 0, 20).Kind == PushKind.None, "past the end of the wall isn't pushed");

        // The other way round, so the sign can't be baked in.
        WallTrack north = new();
        Step(s, north, car, 0, 50, 0, -20);
        var fromNorth = Step(s, north, car, 0, 4, 0, -20);
        Check(north.Side == 1 && fromNorth.Kind == PushKind.Push && fromNorth.Vy > 0, "the same from the other side");

        var foot = ForceField.OnFoot;
        WallTrack walker = new();
        Step(s, walker, foot, 0, -20, 0, 3);
        Check(Step(s, walker, foot, 0, -3, 0, 3).Kind == PushKind.None, "on foot the range is shorter than a car's");
        Check(Step(s, walker, foot, 0, -1, 0, 3).Kind == PushKind.Push, "on foot, close to the wall, is a push");

        // A road on another level isn't blocked, and coming back into the band relearns the side.
        WallTrack bridge = new();
        Step(s, bridge, car, 0, -10, 0, 20);
        Check(Step(s, bridge, car, 0, -4, 0, 20, 8).Kind == PushKind.None && !bridge.Known, "above the band there's no push, and the side is forgotten");
        Check(Step(s, bridge, car, 0, 10, 0, 20, 8).Kind == PushKind.None, "crossing above the band isn't a crossing");
    }

    static void Crossings()
    {
        var car = ForceField.Vehicle;
        var s = ForceField.Segment(At(0, 0, 0, 0));

        WallTrack t = new();
        Step(s, t, car, 2, -3, 10, 90);
        var r = Step(s, t, car, 3, 6, 10, 90);
        Check(r.Kind == PushKind.PutBack, "going through between ticks is caught");
        Check(Near(r.Y, -car.Clearance), "it's put back on the side it came from");
        Check(Near(r.X, 2 + 3.0 / 9), "at the point it met the wall");
        Check(Near(r.Vx, 10), "a reflection keeps sideways speed");
        Check(Near(r.Vy, -90 * ForceField.Restitution), "a reflection reverses the approach");
        Check(t.Side == -1, "the side doesn't change when put back");

        // At an angle, so the put-back point has to use the wall's own axes.
        var angled = ForceField.Segment(At(100, 50, 0, 37));
        var (fx, fy) = ForceField.Forward(37);
        WallTrack ta = new();
        Step(angled, ta, car, 100 - fx * 3, 50 - fy * 3, fx * 80, fy * 80);
        var ra = Step(angled, ta, car, 100 + fx * 3, 50 + fy * 3, fx * 80, fy * 80);
        Check(ra.Kind == PushKind.PutBack && Near(ra.X, 100 - fx * car.Clearance) && Near(ra.Y, 50 - fy * car.Clearance), "an angled wall puts back along its own normal");
        Check(Near(ra.Vx, -fx * 80 * ForceField.Restitution, 1e-2) && Near(ra.Vy, -fy * 80 * ForceField.Restitution, 1e-2), "an angled wall reflects along its own normal");

        // Round the end: allowed, and the side follows.
        WallTrack round = new();
        Step(s, round, car, 20, -3, 0, 40);
        Check(Step(s, round, car, 20, 3, 0, 40).Kind == PushKind.None && round.Side == 1, "going round the end is allowed and changes the side");
        Check(Step(s, round, car, 0, 10, 0, -40).Kind == PushKind.None && round.Side == 1, "the wall then holds from the new side");

        // A teleport isn't a crossing.
        WallTrack jump = new();
        Step(s, jump, car, 0, -30, 0, 0);
        Check(Step(s, jump, car, 0, 30, 0, 0).Kind == PushKind.None && jump.Side == 1, "a jump of over 50 m relearns the side");

        // Out of range forgets, so coming back the other way isn't a crossing.
        WallTrack away = new();
        Step(s, away, car, 0, -20, 0, 0);
        away.Forget();
        Check(Step(s, away, car, 0, 20, 0, 0).Kind == PushKind.None && away.Side == 1, "a forgotten side is learnt again");
    }

    /// <summary>Random drives at the wall, up to 120 m/s and down to 10 ticks a second. Nothing may end up on the far side.</summary>
    static void FullSpeed()
    {
        Random rng = new(31);
        int through = 0, putBacks = 0, runs = 20000;
        for (int run = 0; run < runs; run++)
        {
            bool onFoot = rng.Next(4) == 0;
            var m = onFoot ? ForceField.OnFoot : ForceField.Vehicle;
            float heading = (float)(rng.NextDouble() * 360), width = 6 + (float)rng.NextDouble() * 20;
            var s = ForceField.Segment(At((float)rng.NextDouble() * 1000, (float)rng.NextDouble() * 1000, 0, heading, width));
            var (nx, ny) = (s.NormalX, s.NormalY);
            float side = rng.Next(2) == 0 ? 1 : -1;
            float offset = ((float)rng.NextDouble() - 0.5f) * width * 0.9f;   // inside the span
            float x = s.CentreX + ny * offset + nx * side * 60, y = s.CentreY - nx * offset + ny * side * 60;
            float speed = onFoot ? 1 + (float)rng.NextDouble() * 10 : 1 + (float)rng.NextDouble() * 119;
            float slant = ((float)rng.NextDouble() - 0.5f) * 0.3f;
            float dt = 1f / (10 + rng.Next(110));
            WallTrack t = new();
            for (int tick = 0; tick < 600; tick++)
            {
                // The player keeps driving at the wall after each bounce; the worst case for it.
                float vx = -nx * side * speed + ny * slant * speed, vy = -ny * side * speed - nx * slant * speed;
                float lastX = x, lastY = y;
                x += vx * dt; y += vy * dt;
                var r = ForceField.Step(s, t, m, x, y, 1, vx, vy);
                if (r.Kind == PushKind.PutBack) { x = r.X; y = r.Y; putBacks++; }
                float along = (x - s.CentreX) * ny - (y - s.CentreY) * nx;
                if (Math.Abs(along) > width / 2 + m.EndMargin) break;   // drifted round the end, which is allowed
                if (ForceField.SideOf(s, x, y) == (int)side) continue;

                // On the far side: worked out here rather than from the track, which may be what's wrong.
                float d0 = (lastX - s.CentreX) * nx + (lastY - s.CentreY) * ny, d1 = (x - s.CentreX) * nx + (y - s.CentreY) * ny;
                float a0 = (lastX - s.CentreX) * ny - (lastY - s.CentreY) * nx;
                if (Math.Abs(a0 + (along - a0) * (d0 / (d0 - d1))) <= width / 2) through++;
                break;
            }
        }
        Check(through == 0, $"random drives at the wall: {through} of {runs} got through");
        Check(putBacks > 0, "the random drives did reach the crossing check");
        Console.WriteLine($"force field: {runs} random drives, {through} got through, {putBacks} put backs");
    }

    // ---- colour ------------------------------------------------------------------------------
    /// <summary>A table written by hand in the bake's layout, for the checks that need known values.</summary>
    static byte[] Encode(int w, int h, ushort[] r, ushort[] t, double scale = 1)
    {
        var head = Encoding.UTF8.GetBytes($"{{\"engine\":\"e\",\"job_id\":\"j\",\"params\":{{\"style\":\"s\"}},\"width\":{w},\"height\":{h},\"scale\":{scale}}}");
        using MemoryStream m = new();
        using BinaryWriter b = new(m);
        b.Write(Encoding.ASCII.GetBytes("QLUT"));
        b.Write(head.Length);
        b.Write(head);
        foreach (var v in r) b.Write(v);
        foreach (var v in t) b.Write(v);
        b.Flush();
        return m.ToArray();
    }

    static bool Refused(byte[] data)
    {
        try { EntanglementTable.Parse(data); return false; }
        catch (Exception e) when (e is InvalidDataException or NullReferenceException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException) { return true; }
    }

    static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ArgumentException) { return true; }
    }

    static void Table()
    {
        // One value everywhere reads back as itself, at any angle and thickness.
        var flat = Enumerable.Repeat((ushort)32768, 16).ToArray();
        var zero = new ushort[16];
        var t = EntanglementTable.Parse(Encode(4, 4, flat, zero));
        var c = t.Colour(0.6, 480);
        Check(Near(c.r, 0.5) && Near(c.g, 0.5) && Near(c.b, 0.5), "a flat table reads back as itself");

        var halved = EntanglementTable.Parse(Encode(4, 4, flat, zero, 0.25)).Colour(0.6, 480);
        Check(Near(halved.r, 1), "colour is divided by the scale and held at 1");

        // Rows hold different values: head-on reads the top row and edge-on the bottom row, whatever the phase.
        var rows = new ushort[16];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) rows[y * 4 + x] = (ushort)(y == 0 ? 6553 : y == 3 ? 65535 : 32768);
        t = EntanglementTable.Parse(Encode(4, 4, rows, zero));
        Check(Near(t.Colour(1, 500).r, 0.1), "head-on reads row 0");
        Check(Near(t.Colour(0, 500).r, 1), "edge-on reads the last row");
        Check(Near(t.Colour(-1, 500).r, 1) && Near(t.Colour(2, 500).r, 0.1), "a cos out of range is clamped");

        // Columns hold different values. Worked by hand: head-on at 500 nm red, the phase is
        // (-4pi x 500 / 650 mod 2pi) / 2pi = 0.4615, which on a 4-wide table sits between cells 1 and 2 at 0.346.
        var columns = new ushort[16];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) columns[y * 4 + x] = (ushort)(x == 1 ? 65535 : 0);
        t = EntanglementTable.Parse(Encode(4, 4, columns, zero));
        double phase = (-2 * 500.0 / 650) - Math.Floor(-2 * 500.0 / 650);
        Check(Near(phase, 0.4615, 1e-3), "the worked phase is right");
        Check(Near(t.Colour(1, 500).r, 1 - (phase * 4 - 1.5), 1e-3), "phase picks the column, bilinear");

        // Wraps: the phase just below 1 blends the last column with the first.
        var wrap = new ushort[16];
        for (int y = 0; y < 4; y++) wrap[y * 4] = 65535;
        t = EntanglementTable.Parse(Encode(4, 4, wrap, zero));
        // At green (530 nm) head-on and this thickness the phase is 0, so it sits between the last and first columns.
        Check(Near(t.Colour(1, 530).g, 0.5, 1e-3), "phase wraps round");
        // And from the top: a thickness of 13.25 nm puts green at phase 0.95, 0.3 of the way from the last cell to the first.
        Check(Near(t.Colour(1, 13.25).g, 0.3, 1e-3), "phase wraps round from the last column");

        var good = Encode(4, 4, flat, zero);
        Check(!Refused(good), "a good table is accepted");
        Check(Refused(good.Take(good.Length - 1).ToArray()), "a short table is refused");
        Check(Refused([.. good, 0]), "a long table is refused");
        Check(Refused(Encoding.ASCII.GetBytes("nope")), "something else is refused");
        byte[] magic = [.. good];
        magic[0] = 0;
        Check(Refused(magic), "a wrong magic number is refused");

        Check(Enumerable.Range(0, 2000).All(i => ForceField.Thickness(i * 0.05) is >= 450f and <= 550f), "the shimmer stays within 50 nm of the shader's default");
        Check(ForceField.Thickness(0) != ForceField.Thickness(2), "the shimmer moves");

        // The 11 Nov look: swirl across the wall, haze that fades up and at the ends, light texture frames.
        var swirl = Enumerable.Range(0, 4000).Select(i => ForceField.ThicknessAt(i * 0.05, (i % 69) / 2f - 17, (i % 13) / 2f)).ToArray();
        Check(swirl.All(t => t is >= 395f and <= 605f), "the swirl stays within 105 nm of the shader's default");
        Check(ForceField.ThicknessAt(1, -10, 1) != ForceField.ThicknessAt(1, 10, 1), "the swirl varies across the wall");
        Check(Near(ForceField.Haze(0, 0, 6, 34), 1), "the haze is full at the road in the middle");
        Check(Near(ForceField.Haze(0, 6, 6, 34), 0), "the haze is gone at the top");
        Check(Near(ForceField.Haze(17, 0, 6, 34), 0) && Near(ForceField.Haze(-17, 0, 6, 34), 0), "the haze is gone at both ends");
        Check(ForceField.Haze(0, 1, 6, 34) > ForceField.Haze(0, 3, 6, 34), "the haze thins going up");
        Check(Enumerable.Range(0, 100).All(i => ForceField.LightFrame(i * 97) is >= 0 and < ForceField.LightFrames), "the light frame is always a real frame");
        Check(ForceField.LightFrame(0) != ForceField.LightFrame(ForceField.LightFrameMs), "the light frame changes every step");
        Check(ForceField.LightFrame(int.MaxValue) is >= 0 and < ForceField.LightFrames, "the light frame survives game time wrapping");

        // The light field: averaging, wrap-around and the colour and alpha it gives.
        var dark = new byte[16]; var bright = Enumerable.Repeat((byte)255, 16).ToArray();
        var half = new byte[16]; for (int i = 0; i < 16; i++) half[i] = (byte)(i % 4 < 2 ? 255 : 0);   // left half lit
        var lf = new LightField([dark, bright, half], 4, 2);
        Check(lf.Frames == 3, "three light frames");
        Check(Near(lf.Sample(0, 0.3, 0.7), 0) && Near(lf.Sample(1, 0.3, 0.7), 1), "a dark frame is 0 and a lit one 1");
        Check(Near(lf.Sample(1, 0.25, 0.25), lf.Sample(1, 5.25, -2.75)), "the light wraps round in both directions");
        Check(Near(lf.Sample(2, 0.25, 0.5), 1) && Near(lf.Sample(2, 0.75, 0.5), 0), "averaged squares keep the lit half lit");
        Check(Near(lf.Sample(4, 0.3, 0.7), lf.Sample(1, 0.3, 0.7)), "the frame number wraps too");
        Check(Throws(() => new LightField([new byte[15]], 4, 2)), "a light frame of the wrong size is refused");
        Check(Near(ForceField.Lit(0.5, 0), 0.5) && ForceField.Lit(0.2, 0.5f) > 0.2 && ForceField.Lit(0.5, 1) == 1, "light brightens a colour and caps it at 1");
        Check(ForceField.LitAlpha(170, 0) < ForceField.LitAlpha(170, 1) && ForceField.LitAlpha(255, 1) == 255, "light thickens the haze, capped at 255");

        var s = ForceField.Segment(At(0, 0, 0, 0));
        Check(Near(ForceField.ViewCos(s, 0, 0, 3, 0, -50, 3), 1), "looking square at a wall is head-on");
        Check(Near(ForceField.ViewCos(s, 0, 0, 3, 0, 50, 3), 1), "the same from behind, as both sides are drawn");
        Check(Near(ForceField.ViewCos(s, 0, 0, 3, 50, 0, 3), 0), "looking along a wall is edge-on");
        Check(Near(ForceField.ViewCos(s, 0, 0, 3, 50, 50, 3), Math.Cos(Math.PI / 4)), "45 degrees");
    }

    static void Bake()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("recorded.entanglement_lut.bin");
        Check(stream != null, "the baked table is embedded");
        if (stream == null) return;
        using MemoryStream m = new();
        stream.CopyTo(m);
        var t = EntanglementTable.Parse(m.ToArray());

        Check(t.JobId == "576f2916-8968-4404-bfe4-97ccdd96141a", "the table names its job");
        Check(t.Engine == "entanglement-shader-v1", "the table names its engine");
        Check(t.Params.Contains("\"style\":\"peaked\"") && t.Params.Contains("\"interaction\":5"), "the table holds the peaked settings");
        Check(t.LogLine.Contains(t.JobId) && t.LogLine.Contains(t.Params) && t.LogLine.Contains("R_lut only"), "the log line has the job, the params and where the colour comes from");
        Check(t.Scale > 0 && t.Scale <= 1, "the scale is sensible for a table capped at 1");

        // The bake's own Python lookup and this C# one were written separately, so they should agree.
        Check(t.Samples is { Count: > 0 }, "the bake left worked samples");
        foreach (var sample in t.Samples)
        {
            var c = t.Colour((double)sample["cos"], (double)sample["thickness"]);
            Check(Near(c.r, (double)sample["rgb"][0], 1e-4) && Near(c.g, (double)sample["rgb"][1], 1e-4) && Near(c.b, (double)sample["rgb"][2], 1e-4),
                  $"colour at cos {sample["cos"]}, {sample["thickness"]} nm matches the bake: {c}");
        }

        // What the swatch shows: colour head-on, nearly white edge-on, changing with angle.
        var grazing = t.Colour(0, 500);
        Check(grazing.r > 0.95 && grazing.g > 0.95 && grazing.b > 0.95, "edge-on is nearly white");
        List<(double r, double g, double b)> byAngle = [.. Enumerable.Range(0, 7).Select(i => t.Colour(Math.Cos(i * 10 * Math.PI / 180), 500))];
        Check(byAngle.Distinct().Count() == byAngle.Count, "colour changes with angle");
        Check(byAngle.Any(c => Math.Max(c.r, Math.Max(c.g, c.b)) - Math.Min(c.r, Math.Min(c.g, c.b)) > 0.2), "some angles have real colour");
        Check(!t.Colour(0.8, 450).Equals(t.Colour(0.8, 550)), "colour changes with thickness, which is the shimmer");
    }
}
