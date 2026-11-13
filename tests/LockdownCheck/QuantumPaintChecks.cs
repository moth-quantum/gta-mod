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
        check(QuantumPaint.Hue((107, 82, 149), 1) == (107, 82, 149), "hue factor 1 leaves the colour alone");
        var grey = QuantumPaint.Hue((107, 82, 149), 0);
        check(grey.r == grey.g && grey.g == grey.b && grey.r == 97, "hue factor 0 is the colour's grey");
        check(QuantumPaint.Hue((200, 0, 0), 0.5f) == (130, 30, 30), "hue factor 0.5 is halfway to grey");
        check(QuantumPaint.Hue((200, 0, 0), 2) == (200, 0, 0) && QuantumPaint.Hue((200, 0, 0), -1) == QuantumPaint.Hue((200, 0, 0), 0), "the hue factor is clamped to 0..1");
        check(QuantumPaint.Pearl(0, 0) == 111, "with no hue the pearl is pearl white");
        check(QuantumPaint.StepHue(0, 1, 0) == 0 && QuantumPaint.StepHue(0, 1, -1) == 0, "no time, no hue change");
        check(Math.Abs(QuantumPaint.StepHue(0, 1, QuantumPaint.HueFadeSeconds / 2) - 0.5f) < 1e-4, "half the fade time is half the hue");
        check(QuantumPaint.StepHue(0, 1, 10) == 1 && QuantumPaint.StepHue(1, 0, 10) == 0, "the hue stops at its target");
        check(QuantumPaint.Flicker(0, 0.1f) == 0 && QuantumPaint.Flicker(1, 0.1f) == 1, "the flicker leaves engine off and fully on alone");
        bool above = false, below = false, bounded = true, smooth = true;
        float dt = 0.005f;
        for (int i = 0; i < 2000; i++)
        {
            float t = i * dt, v = QuantumPaint.Flicker(0.5f, t);
            above |= v > 0.55f; below |= v < 0.45f;
            bounded &= Math.Abs(v - 0.5f) <= QuantumPaint.FlickerDepth + 1e-4f;
            smooth &= Math.Abs(QuantumPaint.Flicker(0.5f, t + dt) - v) < 0.05f;
        }
        check(above && below, "halfway through the fade the flicker goes both above and below it");
        check(bounded, "the flicker stays within FlickerDepth of the fade");
        check(smooth, "the flicker glides: 5 ms never moves it more than 0.05");
        float worstEarly = 0, worstMid = 0;
        for (int i = 0; i < 500; i++)
        {
            worstEarly = Math.Max(worstEarly, Math.Abs(QuantumPaint.Flicker(0.05f, i * 0.01f) - 0.05f));
            worstMid = Math.Max(worstMid, Math.Abs(QuantumPaint.Flicker(0.5f, i * 0.01f) - 0.5f));
        }
        check(worstEarly < worstMid * 0.5f, $"the flicker is gentler near the ends of the fade ({worstEarly:F3} at 0.05, {worstMid:F3} at 0.5)");
        check(QuantumPaint.Ease(0, 100, 1) == 100 && QuantumPaint.Ease(0, 100, -1) == 0, "a long frame snaps, a negative one doesn't move");
    }
}
