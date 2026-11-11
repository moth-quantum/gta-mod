// The "Fix the errors" hack, drawn and played. The rules are in HackPuzzle.cs; the terminals that
// open it at the FIB Building and Fort Zancudo are in HackGate.cs. Calls natives, so script thread only.
//
// It changes three things while open, and Close puts all three back: the player (or their vehicle) is
// frozen, the player is invincible if HackInvulnerable is set, and game controls are disabled. The
// controls are only disabled one frame at a time, so they come back as soon as Tick stops doing it.
//
// Not a quantum computation and no Atlas call, so nothing here may say it runs on quantum hardware.

using System;
using System.Drawing;
using GTA;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QDay;

public enum HackLevel { FIB, Zancudo }

public static class Hack
{
    // ---- difficulty ----------------------------------------------------------------
    const int FibRows = 4, FibCols = 4, FibFlips = 2, FibSeconds = 90;                 // 0 = no timer
    const int ZancudoRows = 5, ZancudoCols = 5, ZancudoFlips = 3, ZancudoSeconds = 60;
    const int FibNewErrorSeconds = 0, ZancudoNewErrorSeconds = 15;                     // a qubit flips by itself this often; 0 = never

    /// <summary>Fort Zancudo puts its own wanted level on the player, who can't move while hacking.</summary>
    public const bool HackInvulnerable = true;

    const int InputGuardMs = 250;      // so the press that opened the hack isn't read as a move
    const int SuccessMs = 1500;        // SYSTEM OPEN stays up this long before the hack closes

    // ---- text, exactly as specified --------------------------------------------------
    const string Title = "FIX THE ERRORS";
    const string Instruction = "A red light means two neighbours disagree. Find the flipped qubits and flip them back.";
    const string SuccessText = "SYSTEM OPEN";
    // Placeholders from the 11 Nov visual pass, to be confirmed against docs/script.md.
    const string FibSite = "FIB BUILDING // FLOOR 49 // SECURE TERMINAL";
    const string ZancudoSite = "FORT ZANCUDO // HANGAR 2 // SECURE TERMINAL";
    const string BreachText = "BREACH", LinkText = "LINK OK", NewErrorText = "NEW ERROR";
    const string LitText = "RED LIGHTS", FlipsText = "FLIPS", RescrambleText = "RESCRAMBLE", NoTimerText = "NO TIMER";

    // ---- layout on SHVDN's 1280 x 720 canvas -----------------------------------------
    // Only rectangles, alpha, text and time. Vertical rhythm: a header, then even BigGap spaces between
    // blocks, SmallGap inside a block, Foot at the bottom.
    const float CanvasW = 1280f, CanvasH = 720f;
    const float PanelW = 780f;
    const float Head = 36f, BigGap = 32f, SmallGap = 14f, TitlePx = 34f, InstrPx = 13f, Row = 30f, Foot = 28f;
    const float GridLine = 24f;        // the faint background grid
    const float OpenMs = 220f;         // the panel grows from a line
    const float TextPxPerScale = 40f;  // GTA text scale 1.0 is roughly this many px tall on the 720 canvas; check in game

    static readonly Color Accent = Color.FromArgb(80, 220, 255);
    static readonly Color Red = Color.FromArgb(255, 50, 60);
    static readonly Color Green = Color.FromArgb(90, 240, 140);
    static readonly Color Yellow = Color.FromArgb(255, 205, 0);
    static readonly Color Amber = Color.FromArgb(255, 150, 40);
    static readonly Color Pale = Color.FromArgb(200, 220, 230);
    static readonly Color QubitFill = Color.FromArgb(18, 30, 40);
    static readonly Color QubitEdge = Color.FromArgb(150, 200, 220);
    static readonly Color QuietCheck = Color.FromArgb(90, 120, 135);
    static readonly Color PanelFill = Color.FromArgb(235, 5, 10, 16);

    // GTA's own button prompts. The D-pad group number is from R*'s input group list and needs confirming in game.
    const int InputGroupFrontendDpadAll = 6;

    static readonly Random rng = new();
    static HackPuzzle puzzle;
    static HackLevel level;
    static int cursor, flips, openedAt, timerStart, errorAt, lastNewError = -1, solvedAt = -1;
    static readonly int[] flashAt = new int[64], newErrorAt = new int[64];   // per qubit, game time of the last flip / new error
    static Entity frozen;
    static bool wasInvincible;
    static Scaleform buttons;

    public static bool IsOpen { get; private set; }

    /// <summary>True for the frame the hack closes after being solved, then cleared by the next Tick.</summary>
    public static bool JustSolved { get; private set; }

    /// <summary>Which level the last solve was for, so a gate only opens on its own hack.</summary>
    public static HackLevel SolvedLevel { get; private set; }

    public static void Open(HackLevel hackLevel)
    {
        if (IsOpen) return;
        level = hackLevel;
        NewLayout();
        cursor = 0;
        flips = 0;
        openedAt = timerStart = errorAt = Game.GameTime;
        solvedAt = lastNewError = -1;
        Array.Clear(flashAt, 0, flashAt.Length);
        Array.Clear(newErrorAt, 0, newErrorAt.Length);

        var ped = Game.Player.Character;
        frozen = ped.IsInVehicle() ? ped.CurrentVehicle : ped;
        frozen.IsPositionFrozen = true;
        wasInvincible = Game.Player.IsInvincible;
        if (HackInvulnerable) Game.Player.IsInvincible = true;
        IsOpen = true;

        Log.Write($"hack open {level}: {puzzle.Rows}x{puzzle.Cols}, {Flips} flipped, layout {puzzle.LayoutText}, invulnerable {HackInvulnerable}");
    }

    /// <summary>Puts back everything Open changed. Safe to call at any time, including with the hack shut.</summary>
    public static void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (frozen != null && frozen.Exists()) frozen.IsPositionFrozen = false;
        frozen = null;
        if (HackInvulnerable) Game.Player.IsInvincible = wasInvincible;
        buttons?.Dispose();
        buttons = null;
        Log.Write("hack closed");
    }

    /// <summary>Call first thing every tick, in every phase.</summary>
    public static void Tick()
    {
        JustSolved = false;
        if (!IsOpen) return;

        var ped = Game.Player.Character;
        if (!ped.Exists() || ped.IsDead)
        {
            Log.Write("hack: player died");
            Close();
            return;
        }

        Game.DisableAllControlsThisFrame();
        Game.EnableControlThisFrame(Control.FrontendUp);
        Game.EnableControlThisFrame(Control.FrontendDown);
        Game.EnableControlThisFrame(Control.FrontendLeft);
        Game.EnableControlThisFrame(Control.FrontendRight);
        Game.EnableControlThisFrame(Control.FrontendAccept);
        Game.EnableControlThisFrame(Control.FrontendCancel);

        int now = Game.GameTime;
        if (solvedAt >= 0)
        {
            Draw(now);
            if (now - solvedAt >= SuccessMs)
            {
                Close();
                SolvedLevel = level;
                JustSolved = true;
            }
            return;
        }

        if (Seconds > 0 && now - timerStart >= Seconds * 1000)
        {
            NewLayout();
            timerStart = errorAt = now;
            Array.Clear(newErrorAt, 0, newErrorAt.Length);
            Log.Write($"hack {level}: timer ran out, new layout {puzzle.LayoutText}");
        }

        if (NewErrorSeconds > 0 && now - errorAt >= NewErrorSeconds * 1000)
        {
            int i = rng.Next(puzzle.Count);
            puzzle.Disturb(i);
            errorAt = lastNewError = newErrorAt[i] = now;
            Log.Write($"hack {level}: new error on qubit {i}, {puzzle.LitCount} checks lit");
            if (puzzle.IsSolved)   // it flipped back one of the errors and nothing else was left
            {
                solvedAt = now;
                Log.Write($"hack {level}: solved by a new error in {(now - openedAt) / 1000.0:0.0} s with {flips} flips");
                Draw(now);
                return;
            }
        }

        if (now - openedAt >= InputGuardMs && !HandleInput(now)) return;
        Draw(now);
    }

    static int Rows => level == HackLevel.FIB ? FibRows : ZancudoRows;
    static int Cols => level == HackLevel.FIB ? FibCols : ZancudoCols;
    static int Flips => level == HackLevel.FIB ? FibFlips : ZancudoFlips;
    static int Seconds => level == HackLevel.FIB ? FibSeconds : ZancudoSeconds;
    static int NewErrorSeconds => level == HackLevel.FIB ? FibNewErrorSeconds : ZancudoNewErrorSeconds;

    // A fresh layout also clears the player's marks, since they belong to the old one.
    static void NewLayout() => puzzle = HackPuzzle.Generate(Rows, Cols, Flips, rng);

    /// <summary>False if the hack was closed.</summary>
    static bool HandleInput(int now)
    {
        int row = cursor / puzzle.Cols, col = cursor % puzzle.Cols;
        if (Game.IsControlJustPressed(Control.FrontendUp)) row = Math.Max(0, row - 1);
        if (Game.IsControlJustPressed(Control.FrontendDown)) row = Math.Min(puzzle.Rows - 1, row + 1);
        if (Game.IsControlJustPressed(Control.FrontendLeft)) col = Math.Max(0, col - 1);
        if (Game.IsControlJustPressed(Control.FrontendRight)) col = Math.Min(puzzle.Cols - 1, col + 1);
        cursor = puzzle.Index(row, col);

        if (Game.IsControlJustPressed(Control.FrontendAccept))
        {
            puzzle.Flip(cursor);
            flips++;
            flashAt[cursor] = now;
            Log.Write($"hack {level}: flip {cursor}, {puzzle.LitCount} checks lit");
            if (puzzle.IsSolved)
            {
                solvedAt = now;
                Log.Write($"hack {level}: solved in {(now - openedAt) / 1000.0:0.0} s with {flips} flips");
            }
        }
        else if (Game.IsControlJustPressed(Control.FrontendCancel))
        {
            Log.Write($"hack {level}: cancelled after {(now - openedAt) / 1000.0:0.0} s and {flips} flips");
            Close();
            return false;
        }
        return true;
    }

    // ---- drawing -------------------------------------------------------------------
    static void Draw(int now)
    {
        bool solved = solvedAt >= 0;
        int rows = puzzle.Rows, cols = puzzle.Cols;
        bool big = rows >= 5;                          // 5 x 5 a little smaller, to clear the button bar
        float cell = big ? 48f : 56f, gap = big ? 24f : 30f;
        float gridW = cols * cell + (cols - 1) * gap, gridH = rows * cell + (rows - 1) * gap;
        float titleOff = Head + BigGap, instrOff = titleOff + TitlePx + SmallGap, gridOff = instrOff + InstrPx + BigGap;
        float h = gridOff + gridH + BigGap + 1 + SmallGap + Row + Foot;
        float x = (CanvasW - PanelW) / 2, y = (CanvasH - h) / 2, centre = CanvasW / 2;
        var accent = solved ? Green : Accent;

        // open: dim the world, grow the panel from a line
        float open = Math.Min(1f, (now - openedAt) / OpenMs);
        Box(0, 0, CanvasW, CanvasH, Color.FromArgb((int)(115 * open), 0, 8, 14));
        float ph = h * open, py = y + (h - ph) / 2;
        Box(x, py, PanelW, ph, PanelFill);
        for (float gy = py; gy < py + ph; gy += GridLine) Box(x, gy, PanelW, 1, A(accent, 0.035f));
        for (float gx = x; gx < x + PanelW; gx += GridLine) Box(gx, py, 1, ph, A(accent, 0.035f));
        Frame(x, py, PanelW, ph, 1, A(accent, 0.35f));
        Brackets(x - 4, py - 4, PanelW + 8, ph + 8, 22, 3, A(accent, 0.9f));
        if (open < 1f) return;

        // header strip
        Box(x + 1, y + 1, PanelW - 2, Head - 2, A(accent, 0.09f));
        Box(x + 1, y + Head - 1, PanelW - 2, 1, A(accent, 0.25f));
        Text(level == HackLevel.FIB ? FibSite : ZancudoSite, x + 16, y + (Head - 13) / 2, 13, A(accent, 0.9f), Alignment.Left);
        bool blink = now / 500 % 2 == 0;
        Box(x + PanelW - 120, y + (Head - 9) / 2, 9, 9, A(solved ? Green : Red, blink ? 1f : 0.3f));
        Text(solved ? LinkText : BreachText, x + PanelW - 104, y + (Head - 13) / 2, 13, A(accent, 0.9f), Alignment.Left);
        if (!solved && lastNewError >= 0 && now - lastNewError < 1500)
            Text(NewErrorText, x + PanelW - 140, y + (Head - 13) / 2, 13, Amber, Alignment.Right);

        // title, with a short red and cyan glitch every few seconds
        float ty = y + titleOff;
        if (!solved && now % 3200 < 140)
        {
            Text(Title, centre - 3, ty, TitlePx, A(Red, 0.7f), Alignment.Center);
            Text(Title, centre + 3, ty, TitlePx, A(Accent, 0.7f), Alignment.Center);
        }
        Text(Title, centre, ty, TitlePx, Color.White, Alignment.Center);
        Text(Instruction, centre, y + instrOff, InstrPx, A(Pale, 0.75f), Alignment.Center);

        // checks between neighbours
        float gx0 = (CanvasW - gridW) / 2, gy0 = y + gridOff;
        float pulse = 0.65f + 0.35f * (float)Math.Sin(now / 140.0);
        foreach (var (a, b) in puzzle.Checks())
        {
            bool lit = puzzle.IsCheckLit(a, b);
            float ax = gx0 + a % cols * (cell + gap), ay = gy0 + a / cols * (cell + gap);
            bool right = b == a + 1;
            float bx = right ? ax + cell + 4 : ax + cell / 2, by = right ? ay + cell / 2 : ay + cell + 4, len = gap - 8;
            if (lit)
            {
                if (right) Glow(bx, by - 3, len, 6, Red, pulse, 3);
                else Glow(bx - 3, by, 6, len, Red, pulse, 3);
            }
            else
            {
                var c = solved ? A(Green, 0.7f) : A(QuietCheck, 0.55f);
                if (right) Box(bx, by - 1, len, 2, c);
                else Box(bx - 1, by, 2, len, c);
            }
        }

        // qubits
        for (int i = 0; i < puzzle.Count; i++)
        {
            float qx = gx0 + i % cols * (cell + gap), qy = gy0 + i / cols * (cell + gap);
            float wave = solved ? Clamp01((now - solvedAt - (i % cols + i / cols) * 70) / 200f) : 0f;   // green sweeps across on success
            Box(qx, qy, cell, cell, solved ? A(Green, 0.10f + 0.25f * wave) : QubitFill);
            Frame(qx, qy, cell, cell, 2, solved ? A(Green, 0.4f + 0.6f * wave) : A(QubitEdge, 0.55f));
            Text($"q{i}", qx + 5, qy + 4, 10, A(QubitEdge, 0.5f), Alignment.Left);
            if (puzzle.PlayerMarks[i]) Glow(qx + cell / 2 - 8, qy + cell / 2 - 8, 16, 16, solved ? Green : Accent, 1f, 3);
            float ne = newErrorAt[i] > 0 ? 1f - (now - newErrorAt[i]) / 900f : 0f;
            if (ne > 0)
            {
                Frame(qx - 3, qy - 3, cell + 6, cell + 6, 3, A(Amber, ne));
                Box(qx, qy, cell, cell, A(Amber, 0.35f * ne));
            }
            float fl = flashAt[i] > 0 ? 1f - (now - flashAt[i]) / 220f : 0f;
            if (fl > 0) Box(qx, qy, cell, cell, A(Color.White, 0.7f * fl));
            if (i == cursor && !solved)
            {
                float o = 5 + 2 * (float)Math.Sin(now / 160.0);
                Brackets(qx - o, qy - o, cell + 2 * o, cell + 2 * o, 12, 3, Yellow);
            }
        }

        // status row
        float divY = gy0 + gridH + BigGap, sy = divY + 1 + SmallGap;
        Box(x + 24, divY, PanelW - 48, 1, A(accent, 0.25f));
        if (solved)
        {
            int typed = Math.Min(SuccessText.Length, (now - solvedAt) / 60);
            Text(SuccessText.Substring(0, typed) + (blink ? "_" : ""), centre, sy, 30, Green, Alignment.Center);
        }
        else
        {
            int lit = puzzle.LitCount;
            Text($"{LitText} {lit}", x + 24, sy + (Row - 15) / 2, 15, lit > 0 ? Red : Green, Alignment.Left);
            Text($"{FlipsText} {flips}", x + PanelW - 24, sy + (Row - 15) / 2, 15, A(Pale, 0.8f), Alignment.Right);
            if (Seconds > 0)
            {
                int leftMs = Math.Max(0, Seconds * 1000 - (now - timerStart)), secs = (leftMs + 999) / 1000;
                float frac = leftMs / (Seconds * 1000f), bw = 300, bx = centre - bw / 2;
                bool warn = secs <= 10;
                Box(bx, sy + 4, bw, 6, A(Color.White, 0.08f));
                Glow(bx, sy + 4, bw * frac, 6, warn ? Red : Accent, warn ? pulse : 0.9f, 2);
                Text($"{secs / 60}:{secs % 60:00}  {RescrambleText}", centre, sy + 17, 12, warn ? Red : A(Pale, 0.7f), Alignment.Center);
            }
            else Text(NoTimerText, centre, sy + (Row - 12) / 2, 12, A(Pale, 0.45f), Alignment.Center);
        }

        // scanlines and a slow sweep, over the panel only
        for (float s = y; s < y + h; s += 3) Box(x, s, PanelW, 1, Color.FromArgb(46, 0, 0, 0));
        float sweep = y + now / 9 % (h + 60) - 60;
        float top = Math.Max(y, sweep);
        Box(x, top, PanelW, Math.Max(0, Math.Min(sweep + 60, y + h) - top), A(accent, 0.03f));
        if (solved && now - solvedAt < 160) Box(0, 0, CanvasW, CanvasH, A(Green, 0.25f * (1 - (now - solvedAt) / 160f)));

        if (!solved) DrawButtons();
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    static Color A(Color c, float alpha) => Color.FromArgb((int)(255 * Clamp01(alpha)), c.R, c.G, c.B);

    static void Box(float x, float y, float w, float h, Color color)
    {
        if (w <= 0 || h <= 0 || color.A == 0) return;
        new ContainerElement(new PointF(x, y), new SizeF(w, h), color).Draw();
    }

    static void Frame(float x, float y, float w, float h, float t, Color c)
    {
        Box(x, y, w, t, c); Box(x, y + h - t, w, t, c); Box(x, y, t, h, c); Box(x + w - t, y, t, h, c);
    }

    static void Brackets(float x, float y, float w, float h, float len, float t, Color c)
    {
        Box(x, y, len, t, c); Box(x, y, t, len, c); Box(x + w - len, y, len, t, c); Box(x + w - t, y, t, len, c);
        Box(x, y + h - t, len, t, c); Box(x, y + h - len, t, len, c); Box(x + w - len, y + h - t, len, t, c); Box(x + w - t, y + h - len, t, len, c);
    }

    // A glow is three wider rectangles at falling alpha under the bar itself.
    static void Glow(float x, float y, float w, float h, Color c, float alpha, float spread)
    {
        for (int k = 3; k >= 1; k--) Box(x - k * spread, y - k * spread, w + 2 * k * spread, h + 2 * k * spread, A(c, alpha * 0.12f * (4 - k)));
        Box(x, y, w, h, A(c, alpha));
    }

    // Text sizes are given in canvas pixels (as in the preview) and turned into GTA's text scale.
    static void Text(string s, float x, float y, float px, Color color, Alignment align) =>
        new TextElement(s, new PointF(x, y), px / TextPxPerScale, color, Font.Monospace, align).Draw();

    // GTA's prompt bar in the bottom right, rebuilt each frame so it follows a switch between keyboard and pad.
    // Slot 0 is drawn rightmost, so this reads move, flip, quit from left to right.
    static void DrawButtons()
    {
        buttons ??= new Scaleform("instructional_buttons");
        if (!buttons.IsLoaded) return;
        buttons.CallFunction("CLEAR_ALL");
        buttons.CallFunction("SET_DATA_SLOT", 0, ButtonString(Control.FrontendCancel), "quit");
        buttons.CallFunction("SET_DATA_SLOT", 1, ButtonString(Control.FrontendAccept), "flip");
        buttons.CallFunction("SET_DATA_SLOT", 2, Function.Call<string>(Hash.GET_CONTROL_GROUP_INSTRUCTIONAL_BUTTONS_STRING, 2, InputGroupFrontendDpadAll, true), "move");
        buttons.CallFunction("DRAW_INSTRUCTIONAL_BUTTONS", -1);
        buttons.Render2D();
    }

    static string ButtonString(Control control) =>
        Function.Call<string>(Hash.GET_CONTROL_INSTRUCTIONAL_BUTTONS_STRING, 2, (int)control, true);
}
