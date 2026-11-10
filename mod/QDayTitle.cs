// The Q-Day title: "qday activated" in Atlas Type's grid font, flickering through the export's seeds and
// landing on the one the IBM run's random numbers chose. Timeline, font and layout are in QDayTitleTimeline.cs.
// Calls natives, so script thread only. It's an overlay: the player keeps control throughout.
//
// Horizontal and vertical strokes are DRAW_RECT. I couldn't confirm a plain white texture in the game files,
// so diagonals default to small DRAW_RECT squares stepped along the stroke, which needs no texture. Setting
// DiagonalsAsSprites draws them as one rotated sprite each instead, with the texture checked when it loads.

using System;
using System.Drawing;
using System.IO;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QDay;

public static class QDayTitle
{
    static readonly bool DiagonalsAsSprites = false;    // see the note at the top; compare in game
    const string SpriteDict = "commonmenu", SpriteName = "gradient_nav";   // candidate only, unconfirmed as plain white
    const int BackdropAlpha = 140;                       // about 55%
    const float LineScale = 0.42f;
    const float LineGap = 18f;                           // canvas pixels under the glyph box
    const double StampSpacing = 0.5;                     // of a stamp's side, along a diagonal
    const string StingSound = "TextHit", StingSet = "WastedSounds";   // listed in the game's sound names; check in game

    static GridFont font;
    static bool loadTried;
    static readonly Random rng = new();
    static int startIndex, landingIndex, startedAt;
    static double bellS;
    static bool landed, spriteChecked, spriteOk;
    static int? stingId;

    public static bool IsActive { get; private set; }

    /// <summary>
    /// Starts the title. atlasValue is the first of the Q-Day random numbers, or null if the result had none.
    /// False, with the reason logged, if the font couldn't be loaded, so the caller's Q-DAY card shows instead.
    /// </summary>
    public static bool Start(int? atlasValue, double s)
    {
        Stop();
        if (!LoadFont()) return false;
        int count = font.SeedCount;
        startIndex = rng.Next(count);
        if (atlasValue.HasValue) landingIndex = QDayTitleTimeline.LandingIndex(atlasValue.Value, count);
        else
        {
            landingIndex = rng.Next(count);
            Log.Write("qday title: the Q-Day result has no random numbers, landing chosen by System.Random");
        }
        bellS = s;
        landed = false;
        startedAt = Game.GameTime;
        IsActive = true;
        if (DiagonalsAsSprites) Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, SpriteDict, false);
        Log.Write($"qday title: start index {startIndex}, landing index {landingIndex}, landing seed {font.SeedNumbers[landingIndex]}{(atlasValue.HasValue ? $" from Q-Day number {atlasValue.Value}" : "")}");
        return true;
    }

    /// <summary>Stops drawing and releases what it holds. Safe to call at any time.</summary>
    public static void Stop()
    {
        if (stingId.HasValue) Audio.ReleaseSound(stingId.Value);
        stingId = null;
        if (!IsActive) return;
        IsActive = false;
        if (DiagonalsAsSprites) Function.Call(Hash.SET_STREAMED_TEXTURE_DICT_AS_NO_LONGER_NEEDED, SpriteDict);
        Log.Write("qday title: stopped");
    }

    static bool LoadFont()
    {
        if (font != null) return true;
        if (loadTried) return false;   // refused once already, and logged then
        loadTried = true;
        try
        {
            using var s = typeof(QDayTitle).Assembly.GetManifestResourceStream("recorded.qday_title_gridfont.json")
                          ?? throw new FileNotFoundException("no embedded qday_title_gridfont.json");
            using StreamReader r = new(s);
            font = GridFont.Parse(r.ReadToEnd());
            Log.Write($"qday title: font loaded, \"{font.Text}\", {font.SeedCount} seeds {font.SeedNumbers[0]} to {font.SeedNumbers[font.SeedCount - 1]}");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"qday title: font refused, using the Q-DAY card instead: {e.Message}");
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
        if (QDayTitleTimeline.Finished(ms))
        {
            Stop();
            return;
        }
        if (!landed && QDayTitleTimeline.Landed(ms))
        {
            landed = true;
            stingId = Audio.PlaySoundFrontend(StingSound, StingSet);
            Log.Write($"qday title: landed on seed {font.SeedNumbers[landingIndex]}");
        }

        double alpha = QDayTitleTimeline.Alpha(ms);
        int a = (int)Math.Round(255 * alpha);
        Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1f, 1f, 0, 0, 0, (int)Math.Round(BackdropAlpha * alpha), false);

        double aspect = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
        if (aspect < 0.5 || aspect > 5) aspect = 16.0 / 9;   // a bad reading shouldn't throw the layout off screen
        bool sprites = DiagonalsAsSprites && SpriteReady();
        int seed = QDayTitleTimeline.SeedIndex(ms, startIndex, landingIndex, font.SeedCount);
        foreach (var st in TitleLayout.Strokes(font, seed, aspect))
        {
            if (st.Horizontal) Rect(st.CX, st.CY, st.Length, st.Thickness, aspect, a);
            else if (st.Vertical) Rect(st.CX, st.CY, st.Thickness, st.Length, aspect, a);
            else if (sprites)
                Function.Call(Hash.DRAW_SPRITE, SpriteDict, SpriteName, (float)(st.CX / aspect), (float)st.CY, (float)(st.Length / aspect), (float)st.Thickness, (float)st.AngleDegrees, 255, 255, 255, a, false, 0);
            else Stamp(st, aspect, a);
        }

        if (QDayTitleTimeline.ShowsLine(ms))
        {
            var bottom = TitleLayout.Box(font, aspect).bottom;
            string line = double.IsNaN(bellS) ? "every key on earth is public" : $"every key on earth is public    Bell S = {bellS:0.00} > 2";
            new TextElement(line, new PointF(640f, (float)(bottom * 720) + LineGap), LineScale, Color.FromArgb(a, 255, 255, 255), Font.ChaletLondon, Alignment.Center).Draw();
        }
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

    static bool SpriteReady()
    {
        if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, SpriteDict)) return false;   // stamps until it loads
        if (!spriteChecked)
        {
            spriteChecked = true;
            var size = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, SpriteDict, SpriteName);
            spriteOk = size.X > 0 && size.Y > 0;
            Log.Write($"qday title: sprite {SpriteDict}/{SpriteName} {(spriteOk ? $"is {size.X} x {size.Y}" : "not found, using stamps")}");
        }
        return spriteOk;
    }
}
