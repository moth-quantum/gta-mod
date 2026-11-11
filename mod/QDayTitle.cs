// The animated titles in Atlas Type's grid font: "qday activated" when Q-Day starts and "you survived qday"
// at the ending. Each flickers through its export's seeds and lands on the one picked by the IBM run's random
// numbers. Both share this drawing, the timeline in QDayTitleTimeline.cs and the clean-up; what differs is
// the file, the hold length, the backdrop and the line under the title. Calls natives, so script thread
// only. It's an overlay: the player keeps control throughout.
//
// Horizontal and vertical strokes are DRAW_RECT. I couldn't confirm a plain white texture in the game files,
// so diagonals are small DRAW_RECT squares stepped along the stroke, which needs no texture.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GTA;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;
using Screen = GTA.UI.Screen;

namespace QDay;

public static class QDayTitle
{
    const float LineScale = 0.42f;
    const float LineGap = 18f;                           // canvas pixels under the glyph box
    const double StampSpacing = 0.5;                     // of a stamp's side, along a diagonal
    const string StingSound = "TextHit", StingSet = "WastedSounds";   // listed in the game's sound names; check in game
    const int GfxOrderAfterFade = 7, GfxOrderAfterHud = 4;            // eGfxDrawOrder, for SET_SCRIPT_GFX_DRAW_ORDER

    /// <summary>What makes one title different from another.</summary>
    sealed class Title(string name, string resource, string fallbackCard, TitleTiming timing, int backdropAlpha, bool overFade)
    {
        public readonly string Name = name;                  // log prefix
        public readonly string Resource = resource;          // embedded export
        public readonly string FallbackCard = fallbackCard;  // what shows if the font won't load
        public readonly TitleTiming Timing = timing;
        public readonly int BackdropAlpha = backdropAlpha;
        public readonly bool OverFade = overFade;            // drawn after the screen fade, for a title over a faded-out screen
    }

    static readonly Title QDayTitleSpec = new("qday title", "recorded.qday_title_gridfont.json", "the Q-DAY card", TitleTiming.QDay, 140, false);   // about 55%
    static readonly Title EndingTitleSpec = new("ending title", "recorded.ending_title_gridfont.json", "the YOU SURVIVED QDAY card", TitleTiming.Ending, 255, true);

    static readonly Dictionary<string, GridFont> fonts = [];
    static readonly HashSet<string> refused = [];
    static readonly Random rng = new();
    static Title title;
    static GridFont font;
    static string line;
    static int startIndex, landingIndex, startedAt;
    static bool landed;
    static int? stingId;

    public static bool IsActive { get; private set; }

    /// <summary>
    /// Starts the Q-Day title. atlasValue is the first of the Q-Day random numbers, or null if the result had none.
    /// False, with the reason logged, if the font couldn't be loaded, so the caller's Q-DAY card shows instead.
    /// </summary>
    public static bool Start(int? atlasValue, double s) =>
        Start(QDayTitleSpec, atlasValue, double.IsNaN(s) ? "every key on earth is public" : $"every key on earth is public    Bell S = {s:0.00} > 2");

    /// <summary>
    /// Starts the ending title over the faded-out screen, with the credit under it. atlasValue is the next draw
    /// from the riot's Atlas numbers, or null if there were none. False, logged, if the font couldn't be loaded.
    /// </summary>
    public static bool StartEnding(int? atlasValue, string credit) => Start(EndingTitleSpec, atlasValue, credit);

    static bool Start(Title t, int? atlasValue, string underLine)
    {
        Stop();
        if (!LoadFont(t)) return false;
        title = t;
        font = fonts[t.Resource];
        line = underLine;
        int count = font.SeedCount;
        startIndex = rng.Next(count);
        if (atlasValue.HasValue) landingIndex = QDayTitleTimeline.LandingIndex(atlasValue.Value, count);
        else
        {
            landingIndex = rng.Next(count);
            Log.Write($"{t.Name}: the Q-Day result has no random numbers, landing chosen by System.Random");
        }
        landed = false;
        startedAt = Game.GameTime;
        IsActive = true;
        Log.Write($"{t.Name}: start index {startIndex}, landing index {landingIndex}, landing seed {font.SeedNumbers[landingIndex]}{(atlasValue.HasValue ? $" from Q-Day number {atlasValue.Value}" : "")}");
        return true;
    }

    /// <summary>
    /// Stops drawing and releases what it holds. Safe to call at any time. Cut short over a faded-out screen, it
    /// fades the screen back in, so an abort or a reload mid-ending doesn't leave the player in the dark.
    /// </summary>
    public static void Stop() => End(cutShort: true);

    static void End(bool cutShort)
    {
        if (stingId.HasValue) Audio.ReleaseSound(stingId.Value);
        stingId = null;
        if (!IsActive) return;
        IsActive = false;
        if (cutShort && title.OverFade && (Screen.IsFadedOut || Screen.IsFadingOut)) Screen.FadeIn(500);
        Log.Write($"{title.Name}: {(cutShort ? "stopped" : "finished")}");
    }

    static bool LoadFont(Title t)
    {
        if (fonts.ContainsKey(t.Resource)) return true;
        if (refused.Contains(t.Resource)) return false;   // refused once already, and logged then
        try
        {
            using var s = typeof(QDayTitle).Assembly.GetManifestResourceStream(t.Resource)
                          ?? throw new FileNotFoundException($"no embedded {t.Resource.Substring("recorded.".Length)}");
            using StreamReader r = new(s);
            var f = GridFont.Parse(r.ReadToEnd());
            fonts[t.Resource] = f;
            Log.Write($"{t.Name}: font loaded, \"{f.Text}\", {f.SeedCount} seeds {f.SeedNumbers[0]} to {f.SeedNumbers[f.SeedCount - 1]}");
            return true;
        }
        catch (Exception e)
        {
            refused.Add(t.Resource);
            Log.Write($"{t.Name}: font refused, using {t.FallbackCard} instead: {e.Message}");
            return false;
        }
    }

    /// <summary>Call every tick in every phase.</summary>
    public static void Tick()
    {
        if (!IsActive) return;
        var ped = Game.Player.Character;
        if (!ped.Exists() || ped.IsDead)
        {
            Stop();
            return;
        }

        int ms = Game.GameTime - startedAt;
        if (QDayTitleTimeline.Finished(ms, title.Timing))
        {
            End(cutShort: false);
            return;
        }
        if (!landed && QDayTitleTimeline.Landed(ms))
        {
            landed = true;
            stingId = Audio.PlaySoundFrontend(StingSound, StingSet);
            Log.Write($"{title.Name}: landed on seed {font.SeedNumbers[landingIndex]}");
        }

        if (title.OverFade) Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, GfxOrderAfterFade);
        double alpha = QDayTitleTimeline.Alpha(ms, title.Timing);
        int a = (int)Math.Round(255 * alpha);
        Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1f, 1f, 0, 0, 0, (int)Math.Round(title.BackdropAlpha * alpha), false);

        double aspect = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
        if (aspect < 0.5 || aspect > 5) aspect = 16.0 / 9;   // a bad reading shouldn't throw the layout off screen
        int seed = QDayTitleTimeline.SeedIndex(ms, startIndex, landingIndex, font.SeedCount);
        foreach (var st in TitleLayout.Strokes(font, seed, aspect))
        {
            if (st.Horizontal) Rect(st.CX, st.CY, st.Length, st.Thickness, aspect, a);
            else if (st.Vertical) Rect(st.CX, st.CY, st.Thickness, st.Length, aspect, a);
            else Stamp(st, aspect, a);
        }

        if (QDayTitleTimeline.ShowsLine(ms, title.Timing))
        {
            var bottom = TitleLayout.Box(font, aspect).bottom;
            new TextElement(line, new PointF(640f, (float)(bottom * 720) + LineGap), LineScale, Color.FromArgb(a, 255, 255, 255), Font.ChaletLondon, Alignment.Center).Draw();
        }
        if (title.OverFade) Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, GfxOrderAfterHud);
    }

    static void Rect(double cx, double cy, double w, double h, double aspect, int a) =>
        Function.Call(Hash.DRAW_RECT, (float)(cx / aspect), (float)cy, (float)(w / aspect), (float)h, 255, 255, 255, a, false);

    // A diagonal as squares along it, each sized so its width across the stroke matches the stroke's thickness.
    static void Stamp(in TitleStroke st, double aspect, int a)
    {
        double rad = st.AngleDegrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double side = st.Thickness / (Math.Abs(cos) + Math.Abs(sin));
        double run = st.Length - side;   // the stamps' centres stay inside the stroke's ends
        int n = Math.Max(1, (int)Math.Ceiling(run / (side * StampSpacing)));
        for (int k = 0; k <= n; k++)
        {
            double f = (double)k / n - 0.5;
            Rect(st.CX + cos * run * f, st.CY + sin * run * f, side, side, aspect, a);
        }
    }
}
