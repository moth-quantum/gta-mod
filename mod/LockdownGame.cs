// Escape lockdown, the game side: draws what Lockdown.cs decided. Calls natives, so script thread only.
// Nothing here spawns an entity, so there is nothing to clean up when the mission ends.
//
// TODO(Alejandro): the force field that pushes the car back, minimap markers for nearby sealed
// roads and particles attach at DrawWalls, using the same sealed wall sites.

using System;
using System.Drawing;
using System.IO;
using GTA;
using GTA.Math;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QDay;

public static class LockdownGame
{
    static readonly Color WallColor = Color.FromArgb(130, 60, 140, 255);
    const float MarkerHeight = 80f;       // tall enough to spot from down the road

    /// <summary>The embedded district file, or null with the reason logged, so a bad file can't stop the script loading.</summary>
    public static DistrictGraph LoadDistricts()
    {
        try
        {
            using var s = typeof(LockdownGame).Assembly.GetManifestResourceStream("recorded.lockdown_districts.json")
                          ?? throw new FileNotFoundException("no embedded lockdown_districts.json");
            using StreamReader r = new(s);
            var g = DistrictGraph.Parse(r.ReadToEnd());
            Log.Write($"lockdown: {g.Count} districts, {g.Links.Count} links, {g.Stops.Count} stops");
            return g;
        }
        catch (Exception e)
        {
            Log.Write($"lockdown: district file refused: {e.Message}");
            return null;
        }
    }

    /// <summary>The centre of the last stop, the temporary end of the escape.</summary>
    public static Vector3 LastStop(DistrictGraph g)
    {
        var c = g.Centres[g.Stops[g.Stops.Count - 1]];   // no ^index: net48 has no System.Index
        return new Vector3(c[0], c[1], c[2]);
    }

    /// <summary>A debug marker at every sealed wall site. Call every frame while the escape runs.</summary>
    public static void DrawWalls(DistrictGraph g, LockdownLayout layout)
    {
        for (int i = 0; i < g.Links.Count; i++)
        {
            if (!layout.Sealed[i]) continue;
            foreach (var w in g.Links[i].Walls)
                World.DrawMarker(MarkerType.VerticalCylinder, new Vector3(w.X, w.Y, w.Z), Vector3.Zero, Vector3.Zero,
                                 new Vector3(w.Width, w.Width, MarkerHeight), WallColor);
        }
    }

    public static void DrawHud(LockdownLayout layout)
    {
        var lines = Lockdown.HudLines(layout);
        new ContainerElement(new PointF(920, 236), new SizeF(340, 8 + 20 * lines.Count), Color.FromArgb(160, 0, 0, 0)).Draw();
        for (int i = 0; i < lines.Count; i++)
            new TextElement(lines[i], new PointF(930, 240 + 20 * i), 0.34f, Color.LightBlue, Font.ChaletLondon, Alignment.Left).Draw();
    }
}
