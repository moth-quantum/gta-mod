// Force field on sealed roads, the logic: where a wall is, what it does to the player, what colour it is.
// No ScriptHookVDotNet types in this file, so tests/LockdownCheck can run it without the game.
// mod/ForceFieldGame.cs does the drawing, physics, particles and blips.
//
// The colour comes from R_lut only, a table from entanglement-shader-v1 baked by
// tools/bake_entanglement.py. T_lut is baked too, but nothing reads it.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace QDay;

/// <summary>A wall as a line on the map plus a height band. Only ForceField.Segment makes one.</summary>
public readonly struct WallSegment(float ax, float ay, float bx, float by, float normalX, float normalY, float z)
{
    public readonly float AX = ax, AY = ay, BX = bx, BY = by;
    public readonly float NormalX = normalX, NormalY = normalY;   // horizontal, along the road
    public readonly float Z = z;                                  // the road surface at the wall

    public float CentreX => (AX + BX) / 2;
    public float CentreY => (AY + BY) / 2;
    public float Width => (float)Math.Sqrt((BX - AX) * (BX - AX) + (BY - AY) * (BY - AY));
    public float PointX(float f) => AX + (BX - AX) * f;
    public float PointY(float f) => AY + (BY - AY) * f;
}

/// <summary>Which side of one wall the player is on. Forgotten when the wall goes out of range.</summary>
public sealed class WallTrack
{
    public bool Known;
    public int Side;
    public float LastX, LastY;
    public void Forget() => Known = false;
}

public enum PushKind { None, Push, PutBack }

/// <summary>What one wall does to the player this tick. X and Y are only meaningful for PutBack.</summary>
public readonly struct PushResult(PushKind kind, float x = 0, float y = 0, float vx = 0, float vy = 0)
{
    public readonly PushKind Kind = kind;
    public readonly float X = x, Y = y, Vx = vx, Vy = vy;
}

/// <summary>How close the player's centre can get, which depends on what it is the centre of.</summary>
public readonly struct Mover(float pushRange, float clearance, float endMargin, float minPushSpeed)
{
    public readonly float PushRange = pushRange;       // metres from the wall where an approach is pushed back
    public readonly float Clearance = clearance;       // metres from the wall when put back
    public readonly float EndMargin = endMargin;       // metres past each end still covered, as the centre can pass while the body hits
    public readonly float MinPushSpeed = minPushSpeed; // m/s away from the wall, so a slow approach still moves
}

public static class ForceField
{
    public const float WallHeight = 6f;      // metres above the road; a road passing above or below isn't in the band
    public const float BandBelow = 2f;       // metres under the road surface still counted, for slopes
    public const float MaxJump = 50f;        // metres in one tick: a teleport, not a crossing
    public const float Restitution = 0.6f;   // share of the approach speed kept when reflected
    public const float MinApproach = 0.2f;   // m/s towards the wall that counts as moving towards it

    public const float CheckRadius = 150f;   // draw and check inside this
    public const float BlipRadius = 300f;    // minimap blip inside this

    public const int Strips = 8;             // DRAW_POLY is one colour per triangle, so a wall needs strips to show bands
    public const int WallAlpha = 150;        // fixed; T_lut would make the walls nearly opaque

    public const float ThicknessCentre = 500f;   // nm, the shader's default
    public const float ThicknessDrift = 50f;     // nm either way
    public const float ShimmerSeconds = 8f;      // one full drift

    public static readonly Mover Vehicle = new(6f, 5f, 1.5f, 6f);
    public static readonly Mover OnFoot = new(1.5f, 1.5f, 0.5f, 3f);

    /// <summary>The direction a GTA heading points: 0 is +Y (north), and it turns counter-clockwise, so 90 is -X (west).</summary>
    public static (float x, float y) Forward(float headingDegrees)
    {
        double h = headingDegrees * Math.PI / 180;
        return ((float)-Math.Sin(h), (float)Math.Cos(h));
    }

    /// <summary>
    /// A wall record as a line across the road, and the only place that interprets one. Heading is taken
    /// as the direction the road runs, so the wall lies square to it. If the district file settles on
    /// another meaning, this is the one function to change.
    /// </summary>
    public static WallSegment Segment(Wall w)
    {
        var (nx, ny) = Forward(w.Heading);
        float acrossX = ny, acrossY = -nx;   // the road direction turned 90 degrees
        float half = w.Width / 2;
        return new(w.X - acrossX * half, w.Y - acrossY * half, w.X + acrossX * half, w.Y + acrossY * half, nx, ny, w.Z);
    }

    /// <summary>Which side of the wall's line a point is on: +1 is the way the normal points, -1 the other.</summary>
    public static int SideOf(in WallSegment s, float x, float y) => Distance(s, x, y) >= 0 ? 1 : -1;

    public static bool InBand(in WallSegment s, float z) => z >= s.Z - BandBelow && z <= s.Z + WallHeight;

    static float Distance(in WallSegment s, float x, float y) => (x - s.CentreX) * s.NormalX + (y - s.CentreY) * s.NormalY;

    // Measured from the centre along the wall.
    static float Along(in WallSegment s, float x, float y) => (x - s.CentreX) * s.NormalY - (y - s.CentreY) * s.NormalX;

    /// <summary>
    /// One tick for one wall. The player can't be inside a wall, so all it needs is the side they
    /// approached from: a move to the other side through the wall is put back, and an approach within
    /// range is turned round. Going round the end of a wall is allowed and just changes the side.
    /// </summary>
    public static PushResult Step(in WallSegment s, WallTrack track, in Mover m, float x, float y, float z, float vx, float vy)
    {
        if (!InBand(s, z))
        {
            track.Forget();
            return default;
        }

        float d = Distance(s, x, y);
        int side = d >= 0 ? 1 : -1;
        float dx = x - track.LastX, dy = y - track.LastY;
        if (!track.Known || dx * dx + dy * dy > MaxJump * MaxJump)
        {
            track.Known = true;
            track.Side = side;
            track.LastX = x;
            track.LastY = y;
            return default;
        }

        float reach = s.Width / 2 + m.EndMargin;
        float nx = s.NormalX * track.Side, ny = s.NormalY * track.Side;   // pointing to the player's side
        if (side != track.Side)
        {
            float d0 = Distance(s, track.LastX, track.LastY);
            float f = d0 / (d0 - d);   // where along this tick's move it met the wall's line
            float a0 = Along(s, track.LastX, track.LastY);
            float along = a0 + (Along(s, x, y) - a0) * f;
            if (Math.Abs(along) > reach)
            {
                track.Side = side;   // went round the end
                track.LastX = x;
                track.LastY = y;
                return default;
            }
            var (rx, ry) = Reflect(vx, vy, nx, ny, m.MinPushSpeed);
            float px = s.CentreX + s.NormalY * along + nx * m.Clearance;
            float py = s.CentreY - s.NormalX * along + ny * m.Clearance;
            track.LastX = px;
            track.LastY = py;
            return new(PushKind.PutBack, px, py, rx, ry);
        }

        track.LastX = x;
        track.LastY = y;
        if (Math.Abs(d) > m.PushRange || Math.Abs(Along(s, x, y)) > reach || vx * nx + vy * ny > -MinApproach) return default;
        var (ux, uy) = Reflect(vx, vy, nx, ny, m.MinPushSpeed);
        return new(PushKind.Push, 0, 0, ux, uy);
    }

    /// <summary>Velocity turned away from the wall: sideways speed kept, approach speed reversed and reduced, never less than the minimum.</summary>
    static (float, float) Reflect(float vx, float vy, float nx, float ny, float minSpeed)
    {
        float into = vx * nx + vy * ny;
        float away = Math.Max(Math.Abs(into) * Restitution, minSpeed);
        return (vx + nx * (away - into), vy + ny * (away - into));
    }

    /// <summary>Cos of the angle between the wall's normal and the line to the camera, as the shader's |n.v|.</summary>
    public static float ViewCos(in WallSegment s, float x, float y, float z, float camX, float camY, float camZ)
    {
        float vx = camX - x, vy = camY - y, vz = camZ - z;
        float len = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (len < 1e-3f) return 1f;
        return Math.Min(1f, Math.Abs(vx * s.NormalX + vy * s.NormalY) / len);
    }

    /// <summary>The shimmer: thin-film thickness drifting slowly, so every colour is still a lookup in the table.</summary>
    public static float Thickness(double seconds) =>
        ThicknessCentre + ThicknessDrift * (float)Math.Sin(2 * Math.PI * (seconds % ShimmerSeconds) / ShimmerSeconds);
}

/// <summary>
/// The baked table and the shader's lookup. The tables hold one number per cell, so colour comes from
/// sampling R_lut three times, once per wavelength. Columns are phase (wraps), rows are the angle from
/// head-on at row 0 to edge-on at the last row (clamps). T_lut is kept in the file but nothing reads it.
/// </summary>
public sealed class EntanglementTable
{
    const double TwoPi = 2 * Math.PI;
    static readonly double[] Wavelengths = [650, 530, 470];   // nm, R, G, B, from the engine's HLSL

    int width, height;
    ushort[] r;

    public string Engine { get; private set; }
    public string JobId { get; private set; }
    public string Params { get; private set; }
    public double Scale { get; private set; }
    public JsonArray Samples { get; private set; }            // worked in Python by the bake, for the harness to compare

    public string LogLine => $"forcefield: colour from R_lut only, engine {Engine} job {JobId} params {Params}, {width} x {height}, scale {Scale:0.0000}";

    /// <summary>Reads what tools/bake_entanglement.py writes. Anything malformed throws, so a bad file can't draw wrong colours.</summary>
    public static EntanglementTable Parse(byte[] data)
    {
        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "QLUT") throw new InvalidDataException("not an entanglement table");
        int headLength = BitConverter.ToInt32(data, 4);
        if (headLength <= 0 || headLength > data.Length - 8) throw new InvalidDataException("bad header length");
        var head = JsonNode.Parse(Encoding.UTF8.GetString(data, 8, headLength)) ?? throw new InvalidDataException("empty header");

        EntanglementTable t = new()
        {
            width = head["width"].GetValue<int>(),
            height = head["height"].GetValue<int>(),
            Scale = head["scale"].GetValue<double>(),
            Engine = head["engine"]?.GetValue<string>(),
            JobId = head["job_id"]?.GetValue<string>(),
            Params = head["params"]?.ToJsonString(),
            Samples = head["samples"] as JsonArray,
        };
        if (t.width <= 0 || t.height <= 0 || t.Scale <= 0) throw new InvalidDataException("bad table size or scale");
        int cells = t.width * t.height;
        if (data.Length != 8 + headLength + cells * 4) throw new InvalidDataException($"expected {cells * 4} table bytes, found {data.Length - 8 - headLength}");
        t.r = new ushort[cells];
        Buffer.BlockCopy(data, 8 + headLength, t.r, 0, cells * 2);   // T follows, unread
        return t;
    }

    /// <summary>R_lut at phase s and angle t, bilinear. Stored values were capped at 1 by the bake.</summary>
    double Sample(double s, double t)
    {
        double x = (s - Math.Floor(s)) * width - 0.5;
        double y = Math.Min(Math.Max(t * height - 0.5, 0), height - 1);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        int y1 = Math.Min(y0 + 1, height - 1);
        int x1 = (x0 + 1) % width;
        x0 = (x0 + width) % width;
        double top = r[y0 * width + x0] * (1 - fx) + r[y0 * width + x1] * fx;
        double bottom = r[y1 * width + x0] * (1 - fx) + r[y1 * width + x1] * fx;
        return (top * (1 - fy) + bottom * fy) / 65535;
    }

    /// <summary>The shader's colour, R, G and B from 0 to 1, for a view angle and film thickness.</summary>
    public (double r, double g, double b) Colour(double cosTheta, double thicknessNm)
    {
        cosTheta = Math.Min(Math.Max(cosTheta, 0), 1);
        double t = Math.Acos(cosTheta) / (Math.PI / 2);
        double d = -2 * TwoPi * thicknessNm * cosTheta;
        double Channel(int i)
        {
            double phase = d / Wavelengths[i] % TwoPi;
            if (phase < 0) phase += TwoPi;
            return Math.Min(1, Sample(phase / TwoPi, t) / Scale);
        }
        return (Channel(0), Channel(1), Channel(2));
    }
}
