// Escape lockdown, the game side: draws what Lockdown.cs decided. Calls natives, so script thread only.
// Nothing here spawns an entity, so there is nothing to clean up when the mission ends.
//
// TODO(Alejandro): the force field that pushes the car back, minimap markers for nearby sealed
// roads and particles attach at DrawWalls, using the same sealed wall sites.

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

    public static DistrictGraph LoadDistricts()
    {
        using var s = typeof(LockdownGame).Assembly.GetManifestResourceStream("recorded.lockdown_districts.json")
                      ?? throw new FileNotFoundException("no embedded lockdown_districts.json");
        using StreamReader r = new(s);
        return DistrictGraph.Parse(r.ReadToEnd());
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
            new TextElement(lines[i], new PointF(930, 240 + 20 * i), 0.34f, lines[i] == "BREACH" ? Color.OrangeRed : Color.LightBlue, Font.ChaletLondon, Alignment.Left).Draw();
    }
}
