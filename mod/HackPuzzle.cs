// The "Fix the errors" hack, the rules only. No ScriptHookVDotNet or GTA types here, so
// tests/HackPuzzle.Tests can run it without the game. mod/Hack.cs draws it and reads the controls.
//
// A grid of qubits that should all be in the same state, with some flipped. Each pair of neighbours
// has a check that lights when they disagree. This is a simplified picture of how error correction
// finds faulty qubits. It isn't a quantum computation and nothing in it calls Atlas.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QDay;

public sealed class HackPuzzle
{
    public int Rows { get; }
    public int Cols { get; }
    public int Count => Rows * Cols;

    /// <summary>The hidden state, true where a qubit is flipped. Never drawn: the player only sees the checks.</summary>
    public bool[] State { get; }

    /// <summary>The qubits the player has flipped an odd number of times. Shows what they did, not the hidden state.</summary>
    public bool[] PlayerMarks { get; }

    public HackPuzzle(int rows, int cols, bool[] state)
    {
        if (rows < 1 || cols < 1) throw new ArgumentException("the grid needs at least one row and one column");
        if (state == null || state.Length != rows * cols) throw new ArgumentException("state must have one entry per qubit");
        Rows = rows;
        Cols = cols;
        State = (bool[])state.Clone();
        PlayerMarks = new bool[rows * cols];
    }

    public int Index(int row, int col) => row * Cols + col;

    /// <summary>Flips a qubit, and toggles the player's mark on it, so flipping twice undoes both.</summary>
    public void Flip(int i)
    {
        if (i < 0 || i >= Count) throw new ArgumentOutOfRangeException(nameof(i));
        State[i] = !State[i];
        PlayerMarks[i] = !PlayerMarks[i];
    }

    /// <summary>
    /// A new error: flips a qubit's hidden state without touching the player's marks, so the marks still
    /// show only what the player did. Fort Zancudo calls this on a timer while the hack is open.
    /// </summary>
    public void Disturb(int i)
    {
        if (i < 0 || i >= Count) throw new ArgumentOutOfRangeException(nameof(i));
        State[i] = !State[i];
    }

    public bool AreNeighbours(int a, int b)
    {
        if (a < 0 || b < 0 || a >= Count || b >= Count) return false;
        int ra = a / Cols, ca = a % Cols, rb = b / Cols, cb = b % Cols;
        return Math.Abs(ra - rb) + Math.Abs(ca - cb) == 1;
    }

    /// <summary>True when the two neighbouring qubits disagree.</summary>
    public bool IsCheckLit(int a, int b)
    {
        if (!AreNeighbours(a, b)) throw new ArgumentException($"qubits {a} and {b} aren't neighbours");
        return State[a] != State[b];
    }

    /// <summary>Every check once, as (left, right) and (above, below) pairs.</summary>
    public IEnumerable<(int a, int b)> Checks()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (c + 1 < Cols) yield return (Index(r, c), Index(r, c + 1));
                if (r + 1 < Rows) yield return (Index(r, c), Index(r + 1, c));
            }
    }

    public int LitCount => Checks().Count(ch => IsCheckLit(ch.a, ch.b));

    /// <summary>Every check dim. That covers flipping the faulty qubits back and flipping all the others, since both leave the grid in one state.</summary>
    public bool IsSolved => LitCount == 0;

    /// <summary>The hidden state as rows of 0 and 1, for the log.</summary>
    public string LayoutText
    {
        get
        {
            var sb = new StringBuilder();
            for (int r = 0; r < Rows; r++)
            {
                if (r > 0) sb.Append('/');
                for (int c = 0; c < Cols; c++) sb.Append(State[Index(r, c)] ? '1' : '0');
            }
            return sb.ToString();
        }
    }

    /// <summary>A random layout with this many flipped qubits, which never starts solved.</summary>
    public static HackPuzzle Generate(int rows, int cols, int flips, Random rng)
    {
        int count = rows * cols;
        if (flips < 1 || flips >= count) throw new ArgumentException("flips must be at least 1 and fewer than the qubits, or the grid starts solved");
        if (rng == null) throw new ArgumentNullException(nameof(rng));
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var state = new bool[count];
            int placed = 0;
            while (placed < flips)
            {
                int i = rng.Next(count);
                if (state[i]) continue;
                state[i] = true;
                placed++;
            }
            var puzzle = new HackPuzzle(rows, cols, state);
            if (!puzzle.IsSolved) return puzzle;
        }
        throw new InvalidOperationException("couldn't make an unsolved layout");
    }
}
