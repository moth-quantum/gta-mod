// Debug experiment: switching off the road past an F4 test wall, the game side, using the logic in
// RoadTest.cs. Only F3 with Debug = true starts it. Calls natives, so script thread only.
//
// Nothing confirms these changes reset by themselves, so Clear has to run on every way out. ForceFieldGame.Clear
// calls it, and that already runs on F10, death, mission end and script reload.
//
// GET_VEHICLE_NODE_POSITION crashes the game on a bad node ID and the validity native only checks for
// zero, so positions are only ever read for an ID the game has just returned. The switched-off native
// may be just as unforgiving, so stored IDs aren't read once the player is far enough for them to unload.

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace QDay;

public static class RoadTestGame
{
    const int SettleMs = 1500;              // after a switch, before reading the nodes
    const int RouteMs = 2000;               // after setting a route, before reading its length
    const int NodesPerPoint = 12;           // nearest nodes taken round the wall and round the stretch
    const int IncludeSwitchedOff = 1;       // GCNF_INCLUDE_SWITCHED_OFF_NODES: without it, a switched-off node can't be found
    const float NodeZWeight = 3f;           // the value commonly passed for zMeasureMult
    const float ReadRange = 200f;           // metres from the wall beyond which stored node IDs aren't read
    const float OwnRouteAhead = 250f;       // metres past the wall for a route when no mission route exists
    const bool BackToOriginalFlag = true;   // the DB says always 1

    sealed class Node(int id, Vector3 position, bool firstReading)
    {
        public readonly int Id = id;
        public readonly Vector3 Position = position;
        public readonly bool First = firstReading;
    }

    static readonly RoadTestRun run = new(SettleMs, RouteMs);
    static readonly List<Node> nodes = [];
    static WallSegment wall;
    static RoadStretch applied;
    static Blip routeBlip, ownBlip;

    /// <summary>F3 next variant, Shift+F3 stop and undo, Ctrl+F3 read the nodes round the player and change nothing.</summary>
    public static void Key(bool shift, bool control, Blip missionRoute)
    {
        if (control) { ReadAroundPlayer(); return; }
        if (shift)
        {
            bool wasActive = run.Active;
            var mission = routeBlip;   // Clear forgets it
            Clear();
            if (!wasActive) Log.Write("DEBUG road test: nothing to stop");
            else if (mission != null && mission.Exists())
            {
                mission.ShowRoute = false;
                mission.ShowRoute = true;
            }
            return;
        }

        if (!run.Active)
        {
            if (!ForceFieldGame.LastTestWall(out wall))
            {
                Log.Write("DEBUG road test: drop a wall with F4 first");
                Screen.ShowSubtitle("~y~road test: drop a wall with F4 first~s~", 2500);
                return;
            }
            routeBlip = missionRoute != null && missionRoute.Exists() ? missionRoute : null;
        }
        Do(run.Press(Game.GameTime));
    }

    public static void Tick()
    {
        if (!run.Active) return;
        Do(run.Tick(Game.GameTime));
        if (run.Applied) DrawArea();
    }

    /// <summary>Switches the road back if it's off and removes the test's own blip. Safe to call at any time.</summary>
    public static void Clear()
    {
        if (run.Stop())
        {
            Undo();
            Log.Write($"road test: road switched back by clean-up, {Current} {Reading()}");
        }
        else if (nodes.Count > 0) Log.Write("road test: stopped with the road already back on");
        ownBlip?.Delete();
        ownBlip = null;
        routeBlip = null;
        nodes.Clear();
    }

    static string Current => run.Current.Label;

    static void Do(RoadAction action)
    {
        switch (action)
        {
            case RoadAction.Busy:
                Log.Write("DEBUG road test: still measuring, ignored F3");
                break;
            case RoadAction.Prepare:
                Prepare();
                break;
            case RoadAction.Apply:
                Apply();
                break;
            case RoadAction.ReadAfterApply:
                Log.Write($"road test: {Current} switched off: {Reading()}; {Route("route before setting it again")}");
                SetRouteOff();
                break;
            case RoadAction.Undo:
                Undo();
                break;
            case RoadAction.ReadAfterUndo:
                Log.Write($"road test: {Current} switched back: {Reading()}; {Route("route before setting it again")}");
                SetRouteOff();
                break;
            case RoadAction.RouteOn:
                if (RouteBlip is { } blip) blip.ShowRoute = true;
                break;
            case RoadAction.ReadRoute:
                Log.Write($"road test: {Current} {Route("route after setting it again")}");
                if (run.Idle) Screen.ShowSubtitle($"~y~{Current}~s~. F3 next, Shift+F3 stop", 5000);
                break;
            case RoadAction.Finish:
                Log.Write("road test: every variant tried and switched back");
                Screen.ShowSubtitle("~y~road test finished~s~", 3000);
                Clear();
                break;
        }
    }

    static Blip RouteBlip
    {
        get
        {
            if (ownBlip != null && ownBlip.Exists()) return ownBlip;
            return routeBlip != null && routeBlip.Exists() ? routeBlip : null;
        }
    }

    static void SetRouteOff()
    {
        if (RouteBlip is { } blip) blip.ShowRoute = false;
    }

    static void Prepare()
    {
        var far = RoadTest.Stretch(wall, RoadTest.Lengths.Max());
        var points = new[]
        {
            new Vector3(wall.CentreX, wall.CentreY, wall.Z),
            new Vector3((far.OriginX + far.ExtentX) / 2, (far.OriginY + far.ExtentY) / 2, wall.Z),
        };
        nodes.Clear();
        HashSet<int> seen = [];
        foreach (var point in points)
            foreach (int id in NodesAround(point))
                if (seen.Add(id)) nodes.Add(new(id, NodePosition(id), IsOff(id)));

        Log.Write($"road test: wall at {wall.CentreX:0.0}, {wall.CentreY:0.0}, {wall.Z:0.0} width {wall.Width:0.0}, area starts {RoadTest.Gap:0} m past it, {nodes.Count} nodes near it, using {RoadTest.Variants.Length} variants");
        foreach (var n in nodes)
            Log.Write($"road test: node {n.Id} at {n.Position.X:0.0}, {n.Position.Y:0.0}, {n.Position.Z:0.0}: {far.Along(n.Position.X, n.Position.Y):0.0} m along, {far.Across(n.Position.X, n.Position.Y):0.0} m across, switched off {n.First}");

        if (routeBlip != null) Log.Write("road test: using the mission's route");
        else
        {
            var ahead = new Vector3(wall.CentreX + wall.NormalX * OwnRouteAhead, wall.CentreY + wall.NormalY * OwnRouteAhead, wall.Z);
            var target = World.GetNextPositionOnStreet(ahead);
            if (target == Vector3.Zero) target = ahead;   // no node found, as can happen if that road isn't loaded
            ownBlip = World.CreateBlip(target);
            ownBlip.Color = BlipColor.Green;
            ownBlip.Name = "Road test";
            ownBlip.ShowRoute = true;
            Log.Write($"road test: route set to {target.X:0.0}, {target.Y:0.0}, {target.Z:0.0}, past the wall");
        }
    }

    static void Apply()
    {
        var v = run.Current;
        applied = RoadTest.Stretch(wall, v.Length);
        // Logged before the call, so if it crashes the game the log says which one did it.
        Log.Write($"road test: {v.Label} about to switch off: {Reading()}; {Route("route")}");
        Function.Call(Hash.SET_ROADS_IN_ANGLED_AREA, applied.OriginX, applied.OriginY, applied.OriginZ, applied.ExtentX, applied.ExtentY, applied.ExtentZ, applied.Width, v.Flag1, v.Flag2, v.Flag3);
    }

    static void Undo() =>
        Function.Call(Hash.SET_ROADS_BACK_TO_ORIGINAL_IN_ANGLED_AREA, applied.OriginX, applied.OriginY, applied.OriginZ, applied.ExtentX, applied.ExtentY, applied.ExtentZ, applied.Width, BackToOriginalFlag);

    static string Reading()
    {
        var player = Game.Player.Character.Position;
        if (player.DistanceTo2D(new Vector3(wall.CentreX, wall.CentreY, 0f)) > ReadRange)
            return $"nodes not read, player over {ReadRange:0} m from the wall";

        bool[] first = nodes.Select(n => n.First).ToArray();
        bool[] now = nodes.Select(n => IsOff(n.Id)).ToArray();
        // The latest area, or the first variant's if none has been applied.
        var area = applied.Length > 0 ? applied : RoadTest.Stretch(wall, RoadTest.Lengths[0]);
        bool[] inside = nodes.Select(n => area.Contains(n.Position.X, n.Position.Y, n.Position.Z)).ToArray();
        var reading = RoadTest.Compare(first, now, inside);
        var changed = nodes.Where((n, i) => first[i] != now[i]).Select(n => n.Id);
        return reading.SameAsFirst ? $"{reading.Text}, same as first reading" : $"{reading.Text}, changed since first reading: {string.Join(" ", changed)}";
    }

    static string Route(string what)
    {
        return $"{what}: found {Function.Call<bool>(Hash.GET_GPS_BLIP_ROUTE_FOUND)}, length {Function.Call<int>(Hash.GET_GPS_BLIP_ROUTE_LENGTH)}";
    }

    static void ReadAroundPlayer()
    {
        var player = Game.Player.Character.Position;
        Log.Write($"DEBUG road test: read at {player.X:0.0}, {player.Y:0.0}, {player.Z:0.0}");
        foreach (int id in NodesAround(player))
        {
            var p = NodePosition(id);
            Log.Write($"road test: node {id} at {p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}: switched off {IsOff(id)}");
        }
    }

    static IEnumerable<int> NodesAround(Vector3 point)
    {
        for (int nth = 0; nth < NodesPerPoint; nth++)
        {
            int id = Function.Call<int>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_ID, point.X, point.Y, point.Z, nth, IncludeSwitchedOff, NodeZWeight, 0f);
            if (id != 0 && id != -1) yield return id;
        }
    }

    static Vector3 NodePosition(int id)
    {
        var output = new OutputArgument();
        Function.Call(Hash.GET_VEHICLE_NODE_POSITION, id, output);
        return output.GetResult<Vector3>();
    }

    static bool IsOff(int id) => Function.Call<bool>(Hash.GET_VEHICLE_NODE_IS_SWITCHED_OFF, id);

    // Red outline at road height, so it's clear where the area is.
    static void DrawArea()
    {
        var (ax, ay) = applied.Corner(-1, false);
        var (bx, by) = applied.Corner(1, false);
        var (cx, cy) = applied.Corner(1, true);
        var (dx, dy) = applied.Corner(-1, true);
        float z = wall.Z + 0.5f;
        Line(ax, ay, bx, by, z);
        Line(bx, by, cx, cy, z);
        Line(cx, cy, dx, dy, z);
        Line(dx, dy, ax, ay, z);
    }

    static void Line(float x1, float y1, float x2, float y2, float z) =>
        Function.Call(Hash.DRAW_LINE, x1, y1, z, x2, y2, z, Color.Red.R, Color.Red.G, Color.Red.B, 255);
}
