using System;
using System.IO;
using System.Linq;
using QDay;
using Xunit;

public class TimelineTests
{
    static int Changes(int ms) => QDayTitleTimeline.Changes(ms);

    [Fact]
    public void About_forty_changes_in_the_first_two_seconds()
    {
        Assert.InRange(Changes(1999), 38, 40);
        Assert.Equal(40, Changes(2000));
        Assert.Equal(10, Changes(500));   // 20 a second
    }

    [Fact]
    public void The_rate_falls_between_two_and_three_seconds()
    {
        int[] perQuarter = Enumerable.Range(0, 4).Select(q => Changes(2000 + (q + 1) * 250) - Changes(2000 + q * 250)).ToArray();
        for (int q = 1; q < 4; q++) Assert.True(perQuarter[q] <= perQuarter[q - 1], $"quarter {q}: {string.Join(",", perQuarter)}");
        Assert.True(perQuarter[0] > perQuarter[3]);
        Assert.True(perQuarter[0] <= 5, "no faster than 20 a second");
        Assert.True(perQuarter[3] <= 1, "about 3 a second by the end");
    }

    [Fact]
    public void The_slowdown_rate_runs_from_twenty_to_about_three()
    {
        double Rate(int ms)
        {
            // The continuous count, before rounding down, over one millisecond.
            double u0 = (ms - 2000) / 1000.0, u1 = (ms + 1 - 2000) / 1000.0;
            double I(double u) => 3 * u + 17 * (1 - Math.Pow(1 - u, 3)) / 3;
            return (I(u1) - I(u0)) * 1000;
        }
        Assert.InRange(Rate(2000), 19.9, 20.0);
        Assert.InRange(Rate(2999), 3.0, 3.1);
        Assert.Equal(40 + 8, Changes(2999));   // 40, then 3 + 17/3 = 8.7 in the slowdown
    }

    [Fact]
    public void The_landing_seed_shows_from_three_seconds_to_four_and_a_half()
    {
        const int start = 7, landing = 99, count = 120;
        Assert.NotEqual(landing, QDayTitleTimeline.SeedIndex(2999, start, landing, count));
        foreach (int ms in new[] { 3000, 3500, 4000, 4499, 4500, 4999 })
            Assert.Equal(landing, QDayTitleTimeline.SeedIndex(ms, start, landing, count));
        Assert.False(QDayTitleTimeline.Landed(2999));
        Assert.True(QDayTitleTimeline.Landed(3000));
    }

    [Fact]
    public void Alpha_is_one_until_four_and_a_half_then_zero_at_five()
    {
        foreach (int ms in new[] { 0, 1000, 3000, 4499 }) Assert.Equal(1, QDayTitleTimeline.Alpha(ms));
        Assert.Equal(0.5, QDayTitleTimeline.Alpha(4750), 6);
        Assert.Equal(0, QDayTitleTimeline.Alpha(5000));
        Assert.True(QDayTitleTimeline.Finished(5000));
        Assert.False(QDayTitleTimeline.Finished(4999));
    }

    [Fact]
    public void The_line_shows_during_the_hold_and_fade_only()
    {
        Assert.False(QDayTitleTimeline.ShowsLine(2999));
        Assert.True(QDayTitleTimeline.ShowsLine(3000));
        Assert.True(QDayTitleTimeline.ShowsLine(4999));
        Assert.False(QDayTitleTimeline.ShowsLine(5000));
    }

    [Fact]
    public void The_start_index_wraps_round_the_seeds()
    {
        Assert.Equal(110, QDayTitleTimeline.SeedIndex(0, 110, 0, 120));
        Assert.Equal(119, QDayTitleTimeline.SeedIndex(450, 110, 0, 120));   // 9 changes
        Assert.Equal(0, QDayTitleTimeline.SeedIndex(500, 110, 0, 120));     // 10 changes
        Assert.Equal(10, QDayTitleTimeline.SeedIndex(1000, 110, 0, 120));   // 20 changes
        Assert.All(Enumerable.Range(0, 300), k => Assert.InRange(QDayTitleTimeline.SeedIndex(k * 10, 119, 0, 120), 0, 119));
    }

    [Fact]
    public void The_landing_index_comes_from_a_random_number()
    {
        Assert.Equal(5, QDayTitleTimeline.LandingIndex(125, 120));
        Assert.Equal(0, QDayTitleTimeline.LandingIndex(240, 120));
        Assert.Equal(119, QDayTitleTimeline.LandingIndex(-1, 120));
    }
}

public class FontTests
{
    static readonly GridFont Font = GridFont.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "qday_title_gridfont.json")));

    [Fact]
    public void The_export_reads_as_described()
    {
        Assert.Equal("qday activated", Font.Text);
        Assert.Equal((3, 7, 4, 2), (Font.Cols, Font.Rows, Font.BaselineRow, Font.XHeightRow));
        Assert.Equal(56, Font.Quanta.Length);
        Assert.Equal(120, Font.SeedCount);
        Assert.Equal(Enumerable.Range(6584, 120), Font.SeedNumbers);
        Assert.All(Font.Glyphs, g => Assert.Empty(g[4]));
    }

    [Fact]
    public void A_broken_file_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => GridFont.Parse("{}"));
        Assert.ThrowsAny<Exception>(() => GridFont.Parse("{\"text\":\"ab\",\"grid\":{\"cols\":3,\"rows\":7,\"baseline_row\":4,\"x_height_row\":2},\"quanta\":[{\"id\":0,\"from\":[0,0],\"to\":[9,0]}],\"seeds\":[]}"));
    }

    [Fact]
    public void Letters_are_two_cells_wide_one_apart_with_three_for_the_space()
    {
        var starts = TitleLayout.Starts("qday activated", 2, out int width);
        Assert.Equal(new[] { 0, 3, 6, 9 }, starts.Take(4));
        Assert.Equal(14, starts[5]);           // 'y' ends at 11, then three cells
        Assert.Equal(14 + 8 * 3, starts[13]);  // the last 'd'
        Assert.Equal(40, width);
    }

    [Theory]
    [InlineData(16.0 / 9)]
    [InlineData(4.0 / 3)]
    [InlineData(21.0 / 9)]
    public void The_phrase_is_centred_seventy_percent_wide_and_even_in_every_direction(double aspect)
    {
        for (int seed = 0; seed < Font.SeedCount; seed++)
        {
            var strokes = TitleLayout.Strokes(Font, seed, aspect);
            double c = TitleLayout.Cell(Font, aspect), t = TitleLayout.StrokeWeight * c;
            Assert.All(strokes, s => Assert.Equal(t, s.Thickness, 9));
            Assert.All(strokes.Where(s => s.Horizontal || s.Vertical), s => Assert.Equal(c + t, s.Length, 9));
            Assert.All(strokes.Where(s => !s.Horizontal && !s.Vertical), s => Assert.Equal(Math.Sqrt(2) * c + t, s.Length, 9));
            Assert.All(strokes.Where(s => !s.Horizontal && !s.Vertical), s => Assert.Equal(45, Math.Abs(Math.Abs(s.AngleDegrees) - 90), 9));
        }
        // The grid box, not the inked parts, is what's centred: 40 cells across, 70% of the width.
        double cell = TitleLayout.Cell(Font, aspect);
        Assert.Equal(0.70, 40 * cell / aspect, 9);
        var (top, bottom) = TitleLayout.Box(Font, aspect);
        Assert.Equal(TitleLayout.CentreY, (top + bottom) / 2, 9);
        Assert.True(TitleLayout.CentreY < 0.5);
    }

    [Fact]
    public void Letters_share_a_baseline()
    {
        // Every letter's grid starts on the same top row, so row 4 of each sits at the same height.
        double aspect = 16.0 / 9, c = TitleLayout.Cell(Font, aspect), top = TitleLayout.Box(Font, aspect).top;
        var strokes = TitleLayout.Strokes(Font, 0, aspect);
        Assert.All(strokes, s => Assert.InRange(s.CY, top - 1e-9, top + 6 * c + 1e-9));
        double baseline = top + Font.BaselineRow * c;
        Assert.Contains(strokes, s => s.Horizontal && Math.Abs(s.CY - baseline) < 1e-9);
    }
}
