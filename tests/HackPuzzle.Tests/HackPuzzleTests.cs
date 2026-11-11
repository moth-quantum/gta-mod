using System;
using System.Linq;
using QDay;
using Xunit;

public class HackPuzzleTests
{
    static HackPuzzle Clean(int rows, int cols) => new(rows, cols, new bool[rows * cols]);

    static HackPuzzle OneFlipped(int rows, int cols, int i)
    {
        var state = new bool[rows * cols];
        state[i] = true;
        return new(rows, cols, state);
    }

    [Theory]
    [InlineData(0, 2)]   // corner
    [InlineData(1, 3)]   // edge
    [InlineData(4, 4)]   // middle
    [InlineData(8, 2)]   // the opposite corner
    public void One_flip_lights_the_checks_round_it(int qubit, int expected)
    {
        var p = OneFlipped(3, 3, qubit);
        Assert.Equal(expected, p.LitCount);
        Assert.All(p.Checks().Where(c => p.IsCheckLit(c.a, c.b)), c => Assert.True(c.a == qubit || c.b == qubit));
    }

    [Fact]
    public void One_flip_on_a_4x4_grid_lights_2_3_or_4()
    {
        Assert.Equal(2, OneFlipped(4, 4, 15).LitCount);
        Assert.Equal(3, OneFlipped(4, 4, 2).LitCount);
        Assert.Equal(4, OneFlipped(4, 4, 5).LitCount);
    }

    [Fact]
    public void A_grid_with_nothing_flipped_is_solved()
    {
        Assert.True(Clean(3, 3).IsSolved);
    }

    [Fact]
    public void Flipping_the_right_qubit_back_solves_it()
    {
        var p = OneFlipped(3, 3, 4);
        Assert.False(p.IsSolved);
        p.Flip(4);
        Assert.True(p.IsSolved);
        Assert.Equal(0, p.LitCount);
    }

    [Fact]
    public void Flipping_every_other_qubit_also_solves_it()
    {
        var state = new bool[16];
        state[3] = state[9] = true;
        var p = new HackPuzzle(4, 4, state);
        for (int i = 0; i < 16; i++)
            if (i != 3 && i != 9) p.Flip(i);
        Assert.True(p.IsSolved);
        Assert.True(p.State.All(s => s));
    }

    [Fact]
    public void A_wrong_flip_lights_more_and_can_be_undone()
    {
        var p = OneFlipped(3, 3, 0);
        int before = p.LitCount;
        p.Flip(8);
        Assert.True(p.LitCount > before);
        p.Flip(8);
        Assert.Equal(before, p.LitCount);
    }

    [Fact]
    public void Marks_toggle_on_and_off()
    {
        var p = OneFlipped(3, 3, 4);
        Assert.All(p.PlayerMarks, m => Assert.False(m));
        p.Flip(2);
        Assert.True(p.PlayerMarks[2]);
        Assert.Equal(1, p.PlayerMarks.Count(m => m));
        p.Flip(2);
        Assert.False(p.PlayerMarks[2]);
    }

    [Fact]
    public void Marks_show_what_the_player_did_not_the_hidden_state()
    {
        var p = OneFlipped(3, 3, 4);
        Assert.False(p.PlayerMarks[4]);
        p.Flip(4);
        Assert.True(p.PlayerMarks[4]);
        Assert.False(p.State[4]);
    }

    [Fact]
    public void Checks_cover_every_neighbour_pair_once()
    {
        var p = Clean(4, 4);
        var checks = p.Checks().ToList();
        Assert.Equal(24, checks.Count);   // 4 rows of 3 across, 4 columns of 3 down
        Assert.Equal(checks.Count, checks.Distinct().Count());
        Assert.All(checks, c => Assert.True(p.AreNeighbours(c.a, c.b)));
    }

    [Fact]
    public void Checks_only_exist_between_neighbours()
    {
        var p = Clean(3, 3);
        Assert.False(p.AreNeighbours(2, 3));   // end of one row and start of the next
        Assert.False(p.AreNeighbours(0, 4));   // diagonal
        Assert.Throws<ArgumentException>(() => p.IsCheckLit(2, 3));
    }

    [Theory]
    [InlineData(3, 3, 1)]
    [InlineData(4, 4, 2)]   // FIB Building
    [InlineData(5, 5, 3)]   // Fort Zancudo
    public void A_thousand_generated_layouts_never_start_solved(int rows, int cols, int flips)
    {
        var rng = new Random(1234);
        for (int i = 0; i < 1000; i++)
        {
            var p = HackPuzzle.Generate(rows, cols, flips, rng);
            Assert.False(p.IsSolved);
            Assert.Equal(flips, p.State.Count(s => s));
            Assert.All(p.PlayerMarks, m => Assert.False(m));
        }
    }

    [Fact]
    public void A_layout_that_would_start_solved_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HackPuzzle.Generate(3, 3, 0, new Random(1)));
        Assert.Throws<ArgumentException>(() => HackPuzzle.Generate(3, 3, 9, new Random(1)));
    }

    [Fact]
    public void The_layout_text_shows_the_hidden_state_by_row()
    {
        Assert.Equal("000/010/000", OneFlipped(3, 3, 4).LayoutText);
    }

    [Fact]
    public void A_new_error_flips_the_state_but_leaves_the_marks_alone()
    {
        var p = Clean(5, 5);
        p.Disturb(12);
        Assert.True(p.State[12]);
        Assert.False(p.PlayerMarks[12]);
        Assert.Equal(4, p.LitCount);
        p.Flip(12);                      // the player fixes it: state back, and their mark shows the flip
        Assert.True(p.IsSolved);
        Assert.True(p.PlayerMarks[12]);
    }

    [Fact]
    public void A_new_error_on_a_flipped_qubit_fixes_it()
    {
        var p = OneFlipped(4, 4, 5);
        p.Disturb(5);
        Assert.True(p.IsSolved);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Disturb(16));
    }
}
