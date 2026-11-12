using System;

namespace QDay;

/// <summary>
/// The getaway car's paint, from the Quantum Paint mod (github.com/moth-quantum/gta-quantum-paint, EntangledPaint.cs),
/// free of game types. GetawayCar applies it every frame.
///
/// The table is that mod's PEAKED paint job: Moth Atlas entanglement-shader-v1 job 56f1a157 (reflectance 0.2,
/// absorption 0.5, 3 layers, 8 rays, peaked), its reflectance lookup sampled with the engine's own thin-film phase
/// maths (thickness 500 nm, RGB wavelengths 650/530/470 nm), one sRGB colour per degree of viewing angle, 0 to 90.
/// It's a different parameter set from the force field's table (recorded/entanglement_lut.bin), so the car keeps the
/// mod's purple, green, red and silver sweep.
/// </summary>
public static class QuantumPaint
{
    public const int MaxAngle = 90;
    public const int SecondaryShift = 15;   // degrees: the secondary coat is the colour 15 degrees further on
    public const int PearlShift = 30;       // and the pearlescent layer 30 degrees further on
    public const float Smoothing = 6f;      // per second: how fast the base coat follows the angle, so sweeps glide

    // PEAKED, angle 0..90 degrees, r g b per degree: 91 x 3 bytes. Copied unchanged from EntangledPaint.cs.
    public static readonly byte[] Peaked = {
        107,82,149,107,82,149,106,84,149,105,85,148,104,88,147,102,91,147,101,95,145,99,100,143,96,105,141,93,111,138,91,117,135,88,124,130,84,132,125,81,139,120,78,147,113,75,155,105,73,162,97,71,170,87,69,177,76,69,183,63,68,190,48,69,195,30,70,199,0,71,203,0,72,206,0,73,207,0,73,206,0,72,204,29,71,198,61,69,190,89,68,178,115,68,162,140,70,143,162,75,120,180,81,96,194,88,69,206,98,40,213,108,0,215,118,0,212,128,0,203,139,28,187,147,54,164,153,75,138,155,93,114,152,105,98,146,115,92,137,122,94,128,129,100,121,137,106,115,144,110,111,151,115,110,154,120,109,154,126,112,148,134,121,138,143,139,126,146,165,114,144,198,106,131,236,100,107,255,93,74,255,85,34,255,84,0,255,101,15,255,144,61,255,208,93,204,255,110,141,255,141,95,255,208,0,255,255,3,255,255,59,215,255,77,179,255,114,174,255,144,177,247,163,179,220,177,183,204,187,186,198,192,194,195,196,201,198,201,203,205,207,205,208,210,212,210,214,215,216,221,218,220,227,225,224,232,232,230,237,237,237,243,243,243,248,248,248,254,254,254,255,255,255
    };

    /// <summary>
    /// The viewing angle in whole degrees, 0..90, as the mod works it out: theta = acos(|N . V|), with the car's forward
    /// axis standing in for the surface normal. Both vectors should be unit length; |dot| folds a full turn into 4 sweeps.
    /// </summary>
    public static int Angle(float forwardX, float forwardY, float forwardZ, float viewX, float viewY, float viewZ)
    {
        double dot = Math.Abs(forwardX * viewX + forwardY * viewY + forwardZ * viewZ);
        if (double.IsNaN(dot)) return 0;
        int deg = (int)Math.Round(Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
        return Math.Max(0, Math.Min(MaxAngle, deg));
    }

    /// <summary>The table's colour at an angle, clamped to 0..90.</summary>
    public static (int r, int g, int b) Colour(int degrees)
    {
        int i = Math.Max(0, Math.Min(MaxAngle, degrees)) * 3;
        return (Peaked[i], Peaked[i + 1], Peaked[i + 2]);
    }

    public static (int r, int g, int b) Secondary(int degrees) => Colour(Math.Min(degrees + SecondaryShift, MaxAngle));

    /// <summary>The pearlescent layer, as one of GTA's paint palette indices (SET_VEHICLE_EXTRA_COLOURS takes no RGB).</summary>
    public static int Pearl(int degrees)
    {
        var (r, g, b) = Colour(Math.Min(degrees + PearlShift, MaxAngle));
        return HueIndex(r, g, b);
    }

    /// <summary>A crude hue bucket to a GTA paint palette index, unchanged from the mod.</summary>
    public static int HueIndex(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 25) return 111;                  // near grey: pearl white
        if (r >= g && r >= b) return g > b ? 38 : 135;   // orange : hot pink
        if (g >= r && g >= b) return b > r ? 62 : 55;    // teal : green
        return r > g ? 145 : 64;                         // purple : blue
    }

    /// <summary>One frame of the base coat easing towards its target, so a sweep glides instead of stepping.</summary>
    public static float Ease(float current, float target, float seconds) =>
        current + (target - current) * Math.Min(1f, Smoothing * Math.Max(0f, seconds));
}
