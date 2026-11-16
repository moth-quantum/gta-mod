// Debug experiment: switching off the road past an F4 test wall, the logic. No ScriptHookVDotNet types
// here, so tests/LockdownCheck can run it without the game. mod/RoadTestGame.cs makes the native calls.
//
// The area never covers the wall itself. It starts a few metres past the wall and runs away from it,
// so it shouldn't cut open roads through a junction at the wall. Nothing in the mission uses this yet,
// and nothing touches traffic. docs/forcefield-test.md section G is how it's tested.

using System;
using System.Collections.Generic;
using System.Linq;

namespace QDay;

/// <summary>One thing to try: how long the stretch is, and the three undocumented BOOLs of SET_ROADS_IN_ANGLED_AREA.</summary>
public readonly struct RoadVariant(int number, int count, float length, bool flag1, bool flag2, bool flag3)
{
    public readonly int Number = number, Count = count;
    public readonly float Length = length;
    public readonly bool Flag1 = flag1, Flag2 = flag2, Flag3 = flag3;

    public string Label => $"variant {Number}/{Count} length {Length:0} m flags {Flag1.ToString().ToLowerInvariant()},{Flag2.ToString().ToLowerInvariant()},{Flag3.ToString().ToLowerInvariant()}";
}

/// <summary>An angled area in the native's terms: the mid-points of two opposite edges, and the width between the other two.</summary>
public readonly struct RoadStretch(float originX, float originY, float originZ, float extentX, float extentY, float extentZ, float width)
{
    public readonly float OriginX = originX, OriginY = originY, OriginZ = originZ;
    public readonly float ExtentX = extentX, ExtentY = extentY, ExtentZ = extentZ;
    public readonly float Width = width;

    public float Length => (float)Math.Sqrt((ExtentX - OriginX) * (ExtentX - OriginX) + (ExtentY - OriginY) * (ExtentY - OriginY));

    /// <summary>Metres from the near edge, along the road.</summary>
    public float Along(float x, float y) => ((x - OriginX) * (ExtentX - OriginX) + (y - OriginY) * (ExtentY - OriginY)) / Length;

    /// <summary>Metres from the middle of the area, across the road.</summary>
    public float Across(float x, float y) => ((x - OriginX) * (ExtentY - OriginY) - (y - OriginY) * (ExtentX - OriginX)) / Length;

    public bool Contains(float x, float y, float z)
    {
        float along = Along(x, y);
        return along >= 0 && along <= Length && Math.Abs(Across(x, y)) <= Width / 2 && z >= OriginZ && z <= ExtentZ;
    }

    /// <summary>A corner of the area at the height given: side is -1 or +1 across the road, far picks the far edge.</summary>
    public (float x, float y) Corner(int side, bool far)
    {
        float dx = (ExtentX - OriginX) / Length, dy = (ExtentY - OriginY) / Length;
        float x = far ? ExtentX : OriginX, y = far ? ExtentY : OriginY;
        return (x + dy * side * Width / 2, y - dx * side * Width / 2);
    }
}

/// <summary>How a reading of the nodes differs from the first one taken.</summary>
public readonly struct RoadNodeReading(int inside, int insideOff, int insideTurnedOff, int insideTurnedOn, int outside, int outsideOff, int outsideTurnedOff, int outsideTurnedOn)
{
    public readonly int Inside = inside, InsideOff = insideOff, InsideTurnedOff = insideTurnedOff, InsideTurnedOn = insideTurnedOn;
    public readonly int Outside = outside, OutsideOff = outsideOff, OutsideTurnedOff = outsideTurnedOff, OutsideTurnedOn = outsideTurnedOn;

    public bool SameAsFirst => InsideTurnedOff + InsideTurnedOn + OutsideTurnedOff + OutsideTurnedOn == 0;

    public string Text =>
        $"in area {InsideOff} of {Inside} off ({InsideTurnedOff} turned off, {InsideTurnedOn} turned on); " +
        $"outside {OutsideOff} of {Outside} off ({OutsideTurnedOff} turned off, {OutsideTurnedOn} turned on)";
}

public static class RoadTest
{
    public const float Gap = 5f;            // metres from the wall to the near edge of the area
    public const float BandBelow = 10f;     // metres under the wall's height still in the area, for a road going downhill
    public const float BandAbove = 10f;     // and above it, for one going uphill. A road crossing within this is caught too
    public static readonly float[] Lengths = [20f, 50f];

    /// <summary>
    /// Every combination at each length. The DB doesn't define the flags, so no combination can be ruled
    /// out, but it does tie the third to network events, so those with it true come last: if one of them
    /// goes wrong, the rest have already been logged.
    /// </summary>
    public static readonly RoadVariant[] Variants = MakeVariants();

    static RoadVariant[] MakeVariants()
    {
        List<RoadVariant> all = [];
        int count = 2 * 2 * 2 * Lengths.Length;
        foreach (bool third in new[] { false, true })
            foreach (bool first in new[] { false, true })
                foreach (bool second in new[] { false, true })
                    foreach (float length in Lengths)
                        all.Add(new(all.Count + 1, count, length, first, second, third));
        return all.ToArray();
    }

    /// <summary>The stretch to switch off: past the wall, the way its normal points, as wide as the wall. An F4 wall's normal points away from the player who dropped it.</summary>
    public static RoadStretch Stretch(in WallSegment wall, float length) => new(
        wall.CentreX + wall.NormalX * Gap, wall.CentreY + wall.NormalY * Gap, wall.Z - BandBelow,
        wall.CentreX + wall.NormalX * (Gap + length), wall.CentreY + wall.NormalY * (Gap + length), wall.Z + BandAbove,
        wall.Width);

    public static RoadNodeReading Compare(bool[] first, bool[] now, bool[] inside)
    {
        if (first.Length != now.Length || first.Length != inside.Length) throw new ArgumentException("readings differ in length");
        int inCount = 0, inOff = 0, inToOff = 0, inToOn = 0, outCount = 0, outOff = 0, outToOff = 0, outToOn = 0;
        for (int i = 0; i < first.Length; i++)
        {
            bool off = now[i], changedOff = off && !first[i], changedOn = !off && first[i];
            if (inside[i])
            {
                inCount++;
                if (off) inOff++;
                if (changedOff) inToOff++;
                if (changedOn) inToOn++;
            }
            else
            {
                outCount++;
                if (off) outOff++;
                if (changedOff) outToOff++;
                if (changedOn) outToOn++;
            }
        }
        return new(inCount, inOff, inToOff, inToOn, outCount, outOff, outToOff, outToOn);
    }
}

public enum RoadAction
{
    None,
    Busy,              // pressed mid-measurement, so nothing happens
    Prepare,           // find the nodes and set a route
    Apply,             // read the nodes, then switch the road off
    ReadAfterApply,    // read the nodes and the route as it stands, then turn the route off
    Undo,              // switch the road back
    ReadAfterUndo,     // the same reading after switching back
    RouteOn,           // turn the route back on, a tick after turning it off, so the GPS works it out afresh
    ReadRoute,         // read the route after it was set again
    Finish,            // every variant has been tried and undone
}

/// <summary>
/// One pass through the variants. Each press undoes the one that's on, then applies the next, reading the
/// nodes and route along the way. Applied is true from the moment a variant is asked for until its undo,
/// so whatever way out is taken knows whether the road needs switching back.
/// </summary>
public sealed class RoadTestRun(int settleMs, int routeMs)
{
    enum State { Off, Preparing, AfterApply, ApplyRouteOff, ApplyRouteSettling, On, AfterUndo, UndoRouteOff, UndoRouteSettling }

    State state = State.Off;
    int next, due;

    public bool Active => state != State.Off;
    public bool Applied { get; private set; }
    public RoadVariant Current { get; private set; }
    public bool Idle => state == State.On;

    public RoadAction Press(int now)
    {
        if (state == State.Off)
        {
            next = 0;
            return Wait(State.Preparing, now + routeMs, RoadAction.Prepare);
        }
        if (state == State.On)
        {
            Applied = false;
            return Wait(State.AfterUndo, now + settleMs, RoadAction.Undo);
        }
        return RoadAction.Busy;
    }

    public RoadAction Tick(int now)
    {
        if (state == State.Off || state == State.On || now < due) return RoadAction.None;
        switch (state)
        {
            case State.Preparing: return ApplyNext(now);
            case State.AfterApply: return Wait(State.ApplyRouteOff, now, RoadAction.ReadAfterApply);
            case State.ApplyRouteOff: return Wait(State.ApplyRouteSettling, now + routeMs, RoadAction.RouteOn);
            case State.ApplyRouteSettling: return Wait(State.On, now, RoadAction.ReadRoute);
            case State.AfterUndo: return Wait(State.UndoRouteOff, now, RoadAction.ReadAfterUndo);
            case State.UndoRouteOff: return Wait(State.UndoRouteSettling, now + routeMs, RoadAction.RouteOn);
            default: return AfterUndoRoute(now);
        }
    }

    // The route reading after an undo, then on to the next variant on a later tick.
    bool undoRouteRead;
    RoadAction AfterUndoRoute(int now)
    {
        if (!undoRouteRead)
        {
            undoRouteRead = true;
            return RoadAction.ReadRoute;
        }
        undoRouteRead = false;
        if (next < RoadTest.Variants.Length) return ApplyNext(now);
        state = State.Off;
        return RoadAction.Finish;
    }

    RoadAction ApplyNext(int now)
    {
        Current = RoadTest.Variants[next++];
        Applied = true;
        return Wait(State.AfterApply, now + settleMs, RoadAction.Apply);
    }

    RoadAction Wait(State s, int until, RoadAction action)
    {
        state = s;
        due = until;
        return action;
    }

    /// <summary>Ends the run at once. True if the road was left switched off, so the caller has to undo it.</summary>
    public bool Stop()
    {
        bool undo = Applied;
        Applied = false;
        state = State.Off;
        undoRouteRead = false;
        next = 0;
        return undo;
    }
}
