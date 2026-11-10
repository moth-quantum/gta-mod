// The animated titles (Q-Day and the ending), the parts with no game calls: the timeline, the Atlas Type grid
// font and the layout. tests/QDayTitle.Tests runs this file without the game. mod/QDayTitle.cs draws them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace QDay;

/// <summary>When a title's hold ends and when its fade finishes. The flicker and slowdown are the same for every title.</summary>
public readonly struct TitleTiming(int holdEndMs, int endMs)
{
    public readonly int HoldEndMs = holdEndMs, EndMs = endMs;

    public static readonly TitleTiming QDay = new(QDayTitleTimeline.HoldEndMs, QDayTitleTimeline.EndMs);   // 1.5 s hold, 0.5 s fade
    public static readonly TitleTiming Ending = new(7000, 7500);                                            // 4.0 s hold, 0.5 s fade
}

/// <summary>
/// Elapsed milliseconds to what a title shows. 0 to 2 s: a new seed 20 times a second. 2 to 3 s: easing
/// out to about 3 a second. At 3 s it lands on the chosen seed and holds it, then fades. The Q-Day title
/// holds until 4.5 s and fades by 5 s, which is what the overloads without a timing use.
/// </summary>
public static class QDayTitleTimeline
{
    public const int FastEndMs = 2000, SlowEndMs = 3000, HoldEndMs = 4500, EndMs = 5000;
    public const double FastRate = 20, SlowRate = 3;   // changes a second

    /// <summary>How many times the seed has changed by now, before landing.</summary>
    public static int Changes(int ms)
    {
        if (ms <= 0) return 0;
        if (ms < FastEndMs) return (int)(ms * FastRate / 1000);
        int fast = (int)(FastEndMs * FastRate / 1000);
        double u = Math.Min(1, (ms - FastEndMs) / (double)(SlowEndMs - FastEndMs));
        // The rate eases out from FastRate to SlowRate: rate(u) = Slow + (Fast - Slow)(1 - u)^2, over one second.
        double slow = SlowRate * u + (FastRate - SlowRate) * (1 - Math.Pow(1 - u, 3)) / 3;
        return fast + (int)slow;
    }

    public static bool Landed(int ms) => ms >= SlowEndMs;

    /// <summary>The index into the seeds to show now, wrapping round from the start index.</summary>
    public static int SeedIndex(int ms, int startIndex, int landingIndex, int count) =>
        Landed(ms) ? landingIndex : Wrap(startIndex + Changes(ms), count);

    /// <summary>1 until the hold ends, then down to 0 at the end. Applies to the title, the backdrop and the line under it.</summary>
    public static double Alpha(int ms) => Alpha(ms, TitleTiming.QDay);

    public static double Alpha(int ms, TitleTiming timing)
    {
        if (ms < timing.HoldEndMs) return 1;
        if (ms >= timing.EndMs) return 0;
        return 1 - (ms - timing.HoldEndMs) / (double)(timing.EndMs - timing.HoldEndMs);
    }

    /// <summary>The line under the title shows during the hold and the fade only.</summary>
    public static bool ShowsLine(int ms) => ShowsLine(ms, TitleTiming.QDay);

    public static bool ShowsLine(int ms, TitleTiming timing) => ms >= SlowEndMs && ms < timing.EndMs;

    public static bool Finished(int ms) => Finished(ms, TitleTiming.QDay);

    public static bool Finished(int ms, TitleTiming timing) => ms >= timing.EndMs;

    /// <summary>The landing seed from one of the Q-Day random numbers.</summary>
    public static int LandingIndex(int value, int count) => Wrap(value, count);

    public static int Wrap(int i, int count) => (i % count + count) % count;
}

/// <summary>The Atlas Type export: a grid of points, the 56 quanta between them, and per seed one list of quanta per character.</summary>
public sealed class GridFont
{
    public string Text { get; private set; }
    public int Cols { get; private set; }            // grid points across
    public int Rows { get; private set; }            // grid points down, y down from the top row
    public int BaselineRow { get; private set; }
    public int XHeightRow { get; private set; }
    public (int x1, int y1, int x2, int y2)[] Quanta { get; private set; }
    public int[] SeedNumbers { get; private set; }
    public int[][][] Glyphs { get; private set; }    // [seed][character] -> quanta ids

    public int SeedCount => SeedNumbers.Length;

    /// <summary>Reads the export. Anything malformed throws, so a bad file can't draw wrong letters.</summary>
    public static GridFont Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("empty file");
        var grid = root["grid"] ?? throw new InvalidDataException("no grid");
        GridFont f = new()
        {
            Text = root["text"]?.GetValue<string>() ?? throw new InvalidDataException("no text"),
            Cols = grid["cols"].GetValue<int>(),
            Rows = grid["rows"].GetValue<int>(),
            BaselineRow = grid["baseline_row"].GetValue<int>(),
            XHeightRow = grid["x_height_row"].GetValue<int>(),
        };
        if (f.Cols < 2 || f.Rows < 2) throw new InvalidDataException("grid too small");

        var quanta = root["quanta"] as JsonArray ?? throw new InvalidDataException("no quanta");
        f.Quanta = new (int, int, int, int)[quanta.Count];
        foreach (var q in quanta)
        {
            int id = q["id"].GetValue<int>();
            if (id < 0 || id >= quanta.Count) throw new InvalidDataException($"quantum id {id} out of range");
            var a = q["from"].AsArray();
            var b = q["to"].AsArray();
            f.Quanta[id] = (a[0].GetValue<int>(), a[1].GetValue<int>(), b[0].GetValue<int>(), b[1].GetValue<int>());
            foreach (var (x, y) in new[] { (f.Quanta[id].x1, f.Quanta[id].y1), (f.Quanta[id].x2, f.Quanta[id].y2) })
                if (x < 0 || x >= f.Cols || y < 0 || y >= f.Rows) throw new InvalidDataException($"quantum {id} is off the grid");
        }

        var seeds = root["seeds"] as JsonArray ?? throw new InvalidDataException("no seeds");
        if (seeds.Count == 0) throw new InvalidDataException("no seeds");
        f.SeedNumbers = new int[seeds.Count];
        f.Glyphs = new int[seeds.Count][][];
        for (int s = 0; s < seeds.Count; s++)
        {
            f.SeedNumbers[s] = seeds[s]["seed"].GetValue<int>();
            var glyphs = seeds[s]["glyphs"].AsArray();
            if (glyphs.Count != f.Text.Length) throw new InvalidDataException($"seed {f.SeedNumbers[s]} has {glyphs.Count} glyphs for {f.Text.Length} characters");
            f.Glyphs[s] = new int[glyphs.Count][];
            for (int c = 0; c < glyphs.Count; c++)
            {
                var ids = glyphs[c].AsArray();
                f.Glyphs[s][c] = new int[ids.Count];
                for (int k = 0; k < ids.Count; k++)
                {
                    int id = ids[k].GetValue<int>();
                    if (id < 0 || id >= f.Quanta.Length) throw new InvalidDataException($"seed {f.SeedNumbers[s]} uses quantum {id}");
                    f.Glyphs[s][c][k] = id;
                }
            }
        }
        return f;
    }
}

/// <summary>One stroke on screen, in units where the screen is 1 high and aspect wide, y down. Divide x by the aspect ratio for a screen fraction.</summary>
public readonly struct TitleStroke(double cx, double cy, double length, double thickness, double angleDegrees, bool horizontal, bool vertical)
{
    public readonly double CX = cx, CY = cy, Length = length, Thickness = thickness, AngleDegrees = angleDegrees;
    public readonly bool Horizontal = horizontal, Vertical = vertical;
}

/// <summary>Where the phrase sits on screen. Each letter is the grid's width, one cell apart, with three cells for the word space.</summary>
public static class TitleLayout
{
    public const double WidthFraction = 0.70;   // of the screen width
    public const double CentreY = 0.44;          // the glyph box's middle, a little above the screen's
    public const double StrokeWeight = 0.4;      // of a cell, Atlas Type's full weight; tune in game
    public const int LetterGap = 1, WordGap = 3; // in cells

    /// <summary>The cell each character starts at, and the phrase's width in cells. A space starts where it is and takes no width of its own.</summary>
    public static int[] Starts(string text, int letterCells, out int widthCells)
    {
        var starts = new int[text.Length];
        int cursor = 0, gap = 0;
        bool any = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                starts[i] = cursor;
                gap = WordGap;
                continue;
            }
            int start = any ? cursor + gap : 0;
            starts[i] = start;
            cursor = start + letterCells;
            gap = LetterGap;
            any = true;
        }
        widthCells = cursor;
        return starts;
    }

    /// <summary>The size of one cell in screen-height units.</summary>
    public static double Cell(GridFont font, double aspect)
    {
        Starts(font.Text, font.Cols - 1, out int width);
        return WidthFraction * aspect / width;
    }

    /// <summary>The top and bottom of the glyph box, as fractions of the screen height.</summary>
    public static (double top, double bottom) Box(GridFont font, double aspect)
    {
        double c = Cell(font, aspect), h = (font.Rows - 1) * c;
        return (CentreY - h / 2, CentreY + h / 2);
    }

    /// <summary>Every stroke of one seed, each lengthened by half its thickness at both ends so corners close.</summary>
    public static List<TitleStroke> Strokes(GridFont font, int seedIndex, double aspect)
    {
        var starts = Starts(font.Text, font.Cols - 1, out int width);
        double c = WidthFraction * aspect / width;
        double left = (aspect - width * c) / 2, top = Box(font, aspect).top, t = StrokeWeight * c;
        List<TitleStroke> strokes = [];
        var glyphs = font.Glyphs[seedIndex];
        for (int ch = 0; ch < glyphs.Length; ch++)
            foreach (int id in glyphs[ch])
            {
                var (x1, y1, x2, y2) = font.Quanta[id];
                double ax = left + (starts[ch] + x1) * c, ay = top + y1 * c;
                double bx = left + (starts[ch] + x2) * c, by = top + y2 * c;
                double dx = bx - ax, dy = by - ay;
                strokes.Add(new((ax + bx) / 2, (ay + by) / 2, Math.Sqrt(dx * dx + dy * dy) + t, t,
                                Math.Atan2(dy, dx) * 180 / Math.PI, y1 == y2, x1 == x2));
            }
        return strokes;
    }
}
