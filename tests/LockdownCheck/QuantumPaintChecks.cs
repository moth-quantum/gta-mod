using System;
using QDay;

/// <summary>The getaway car's paint maths only. How it looks needs the game: docs/getaway-car-test.md.</summary>
static class QuantumPaintChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(QuantumPaint.Peaked.Length == (QuantumPaint.MaxAngle + 1) * 3, "the paint table has one colour per degree, 0 to 90");
        check(QuantumPaint.Colour(0) == (107, 82, 149) && QuantumPaint.Colour(90) == (255, 255, 255), "the table starts purple and ends white, as in the mod");

        check(QuantumPaint.Angle(0, 1, 0, 0, 1, 0) == 0, "looking along the car is 0 degrees");
        check(QuantumPaint.Angle(0, 1, 0, 0, -1, 0) == 0, "from the front is also 0 degrees (|dot| folds)");
        check(QuantumPaint.Angle(0, 1, 0, 1, 0, 0) == 90, "side on is 90 degrees");
        float c = (float)Math.Cos(Math.PI / 4);
        check(QuantumPaint.Angle(0, 1, 0, c, c, 0) == 45, "45 degrees off the axis is 45");
        check(QuantumPaint.Angle(0, 1, 0, 0, 1.0001f, 0) == 0, "a dot a hair over 1 doesn't break acos");
        check(QuantumPaint.Angle(0, 0, 0, 0, 0, 0) == 90 && QuantumPaint.Angle(float.NaN, 0, 0, 0, 1, 0) == 0, "degenerate vectors still give an angle in range");

        check(QuantumPaint.Colour(-5) == QuantumPaint.Colour(0) && QuantumPaint.Colour(200) == QuantumPaint.Colour(90), "angles outside 0..90 are clamped");
        check(QuantumPaint.Secondary(10) == QuantumPaint.Colour(25) && QuantumPaint.Secondary(85) == QuantumPaint.Colour(90), "the secondary coat is 15 degrees on, clamped");

        check(QuantumPaint.HueIndex(120, 120, 130) == 111, "near grey is pearl white");
        check(QuantumPaint.HueIndex(255, 120, 0) == 38 && QuantumPaint.HueIndex(255, 0, 120) == 135, "reds go orange or hot pink");
        check(QuantumPaint.HueIndex(0, 255, 120) == 62 && QuantumPaint.HueIndex(120, 255, 0) == 55, "greens go teal or green");
        check(QuantumPaint.HueIndex(120, 0, 255) == 145 && QuantumPaint.HueIndex(0, 120, 255) == 64, "blues go purple or blue");
        bool pearlsValid = true;
        for (int d = 0; d <= 90; d++) pearlsValid &= Array.IndexOf(new[] { 111, 38, 135, 62, 55, 145, 64 }, QuantumPaint.Pearl(d)) >= 0;
        check(pearlsValid, "every angle gives one of the seven pearl palette indices");

        check(QuantumPaint.Ease(0, 100, 0) == 0, "no time, no change");
        check(Math.Abs(QuantumPaint.Ease(0, 100, 0.1f) - 60) < 1e-3, "a tenth of a second covers 60% of the way");
        check(QuantumPaint.Ease(0, 100, 1) == 100 && QuantumPaint.Ease(0, 100, -1) == 0, "a long frame snaps, a negative one doesn't move");
    }
}
