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
    const int FibRows = 3, FibCols = 3, FibFlips = 1, FibSeconds = 0;              // 0 = no timer
    const int ZancudoRows = 4, ZancudoCols = 4, ZancudoFlips = 2, ZancudoSeconds = 60;

    /// <summary>Fort Zancudo puts its own wanted level on the player, who can't move while hacking.</summary>
    public const bool HackInvulnerable = true;

    const int InputGuardMs = 250;      // so the press that opened the hack isn't read as a move
    const int SuccessMs = 1500;        // SYSTEM OPEN stays up this long before the hack closes

    // ---- text, exactly as specified --------------------------------------------------
    const string Title = "FIX THE ERRORS";
    const string Instruction = "A red light means two neighbours disagree. Find the flipped qubits and flip them back.";
    const string SuccessText = "SYSTEM OPEN";

    // ---- layout on SHVDN's 1280 x 720 canvas -----------------------------------------
    const float CanvasW = 1280f, CanvasH = 720f;
    const float PanelW = 700f;
    const float Cell = 52f;            // a qubit square
    const float Gap = 26f;             // between squares, where the checks sit
    const float CursorBorder = 4f;
    const float MarkSize = 14f;
    const float CheckInset = 4f;       // a check stops short of the squares at each end
    const float LitThickness = 10f;    // lit checks are thicker as well as red, so colour isn't the only cue
    const float DimThickness = 4f;
    const float GridTop = 100f;        // from the top of the panel
    const float BelowGrid = 74f;

    static readonly Color PanelColor = Color.FromArgb(200, 0, 0, 0);
    static readonly Color QubitColor = Color.FromArgb(255, 205, 205, 210);
    static readonly Color CursorColor = Color.FromArgb(255, 255, 205, 0);
    static readonly Color MarkColor = Color.FromArgb(255, 25, 25, 35);
    static readonly Color LitColor = Color.FromArgb(255, 255, 45, 45);
    static readonly Color DimColor = Color.FromArgb(255, 70, 70, 70);
    static readonly Color SuccessColor = Color.FromArgb(255, 90, 230, 120);

    // GTA's own button prompts. The D-pad group number is from R*'s input group list and needs confirming in game.
    const int InputGroupFrontendDpadAll = 6;

    static readonly Random rng = new();
    static HackPuzzle puzzle;
    static HackLevel level;
    static int cursor, flips, openedAt, timerStart, solvedAt = -1;
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
        openedAt = timerStart = Game.GameTime;
        solvedAt = -1;

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
            timerStart = now;
            Log.Write($"hack {level}: timer ran out, new layout {puzzle.LayoutText}");
        }

        if (now - openedAt >= InputGuardMs && !HandleInput(now)) return;
        Draw(now);
    }

    static int Rows => level == HackLevel.FIB ? FibRows : ZancudoRows;
    static int Cols => level == HackLevel.FIB ? FibCols : ZancudoCols;
    static int Flips => level == HackLevel.FIB ? FibFlips : ZancudoFlips;
    static int Seconds => level == HackLevel.FIB ? FibSeconds : ZancudoSeconds;

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
        float gridW = puzzle.Cols * Cell + (puzzle.Cols - 1) * Gap;
        float gridH = puzzle.Rows * Cell + (puzzle.Rows - 1) * Gap;
        float panelH = GridTop + gridH + BelowGrid;
        float panelX = (CanvasW - PanelW) / 2, panelY = (CanvasH - panelH) / 2;
        float gridX = (CanvasW - gridW) / 2, gridY = panelY + GridTop;
        float centre = CanvasW / 2;

        Box(panelX, panelY, PanelW, panelH, PanelColor);
        Text(Title, centre, panelY + 14, 0.6f, Color.White);
        Text(Instruction, centre, panelY + 60, 0.32f, Color.White);

        foreach (var (a, b) in puzzle.Checks())
        {
            bool lit = puzzle.IsCheckLit(a, b);
            float t = lit ? LitThickness : DimThickness;
            var color = lit ? LitColor : DimColor;
            float ax = gridX + a % puzzle.Cols * (Cell + Gap), ay = gridY + a / puzzle.Cols * (Cell + Gap);
            if (b == a + 1) Box(ax + Cell + CheckInset, ay + (Cell - t) / 2, Gap - 2 * CheckInset, t, color);   // to the right
            else Box(ax + (Cell - t) / 2, ay + Cell + CheckInset, t, Gap - 2 * CheckInset, color);              // below
        }

        for (int i = 0; i < puzzle.Count; i++)
        {
            float x = gridX + i % puzzle.Cols * (Cell + Gap), y = gridY + i / puzzle.Cols * (Cell + Gap);
            if (i == cursor && solvedAt < 0) Box(x - CursorBorder, y - CursorBorder, Cell + 2 * CursorBorder, Cell + 2 * CursorBorder, CursorColor);
            Box(x, y, Cell, Cell, QubitColor);
            if (puzzle.PlayerMarks[i]) Box(x + (Cell - MarkSize) / 2, y + (Cell - MarkSize) / 2, MarkSize, MarkSize, MarkColor);
        }

        float statusY = gridY + gridH + 18;
        if (solvedAt >= 0) Text(SuccessText, centre, statusY, 0.7f, SuccessColor);
        else if (Seconds > 0)
        {
            int left = Math.Max(0, Seconds - (now - timerStart) / 1000);
            Text($"{left / 60}:{left % 60:00}", centre, statusY, 0.5f, left <= 10 ? LitColor : Color.White);
        }

        if (solvedAt < 0) DrawButtons();
    }

    static void Box(float x, float y, float w, float h, Color color) =>
        new ContainerElement(new PointF(x, y), new SizeF(w, h), color).Draw();

    static void Text(string s, float x, float y, float scale, Color color) =>
        new TextElement(s, new PointF(x, y), scale, color, Font.ChaletLondon, Alignment.Center).Draw();

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
