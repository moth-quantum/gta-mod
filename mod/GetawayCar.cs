// The escape's getaway car (script.md section 9, step 5, the stretch goal): a Jester (racecar) with the Quantum Paint
// mod's paint, parked on the road nearest the briefcase that held the passport. The paint maths is QuantumPaint; this
// file is the natives. The car wears livery 11, whose texture LiveryTexture TextureSwap replaces with the embedded
// LiveryReplacement (recorded/getaway_livery/qblur_livery.dds, from media/qblur_livery.png in gta-quantum-paint).

using System;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QDay;

public static class GetawayCar
{
    public const VehicleHash Model = VehicleHash.Zentorno;
    public const int Livery = 11;
    public const string LiveryTexture = "zentorno_livery_12";   // the texture of Livery, swapped by TextureSwap
    public const string LiveryReplacement = "qblur_livery";      // embedded as textures.qblur_livery.dds
    const float SearchRadius = 60f;   // metres from the briefcase to look for a road; further, and it's parked beside the player

    static Vehicle car;
    static Blip blip;
    static float curR = -1f, curG, curB;   // the eased base coat, -1 = snap on the first frame
    static float hueFactor;                // 1 with the engine running, 0 off (the paint's grey), fading between
    static bool engineOn;
    static float sinceEngineStart;         // seconds, drives the flicker while the hue fades in

    /// <summary>
    /// How much of the paint's hue shows: 1 with the engine running, 0 with it off, fading over QuantumPaint.HueFadeSeconds.
    /// While it rises the paint flickers around it (QuantumPaint.Flicker); this is the fade without the flicker.
    /// </summary>
    public static float HueFactor => hueFactor;

    public static Vehicle Car => car != null && car.Exists() ? car : null;

    /// <summary>Parks the car on the road nearest <paramref name="near"/>, painted and blipped. False if it couldn't be made.</summary>
    public static bool Spawn(Vector3 near)
    {
        Clear();
        var (pos, heading) = Parking(near);
        car = World.CreateVehicle(Model, pos, heading);
        if (car == null)
        {
            Log.Write($"getaway car: couldn't create {Model} at {pos}");
            return false;
        }
        Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, car, 5f);
        car.Mods.InstallModKit();
        car.Mods.Livery = Livery;
        car.Mods[VehicleModType.Engine].Index = car.Mods[VehicleModType.Engine].Count - 1;
        car.Mods[VehicleModType.Brakes].Index = car.Mods[VehicleModType.Brakes].Count - 1;
        car.Mods[VehicleModType.Transmission].Index = car.Mods[VehicleModType.Transmission].Count - 1;
        car.Mods[VehicleModType.Armor].Index = car.Mods[VehicleModType.Armor].Count - 1;
        car.Mods[VehicleModType.Suspension].Index = car.Mods[VehicleModType.Suspension].Count - 1;
        car.Mods[VehicleToggleModType.Turbo].IsInstalled = true;
        for (int i = 0; i < 4; i++) car.Mods.SetNeonLightsOn((VehicleNeonLight)i, true);
        curR = -1f;
        engineOn = car.IsEngineRunning;
        hueFactor = engineOn ? 1f : 0f;
        Paint(0f);

        blip = car.AddBlip();
        blip.Sprite = BlipSprite.SportsCar;
        blip.Color = BlipColor.Blue;
        blip.Name = "Getaway Car";
        Log.Write($"getaway car: {Model} at {pos}, heading {heading:F0}");
        return true;
    }

    /// <summary>Every tick while the car exists: repaints it from the camera's angle, and drops the blip once the player's in it.</summary>
    public static void Tick()
    {
        if (car == null) return;
        if (!car.Exists() || car.IsDead) { Log.Write("getaway car: gone or wrecked"); Forget(); return; }
        if (blip != null && Game.Player.Character.CurrentVehicle == car) { blip.Delete(); blip = null; }
        Paint(Game.LastFrameTime);
    }

    /// <summary>Stops painting and lets the game have the car back. It isn't deleted: the player may be sitting in it.</summary>
    public static void Clear()
    {
        if (car != null && car.Exists())
        {
            car.MarkAsNoLongerNeeded();
        }
        Forget();
    }

    static void Forget()
    {
        blip?.Delete();
        blip = null;
        car = null;
    }

    static void Paint(float seconds)
    {
        Vector3 view = car.Position - GameplayCamera.Position;
        view.Normalize();
        Vector3 f = car.ForwardVector;
        int deg = QuantumPaint.Angle(f.X, f.Y, f.Z, view.X, view.Y, view.Z);

        var (r, g, b) = QuantumPaint.Colour(deg);
        if (curR < 0f) { curR = r; curG = g; curB = b; }
        curR = QuantumPaint.Ease(curR, r, seconds);
        curG = QuantumPaint.Ease(curG, g, seconds);
        curB = QuantumPaint.Ease(curB, b, seconds);
        bool on = car.IsEngineRunning;
        if (on && !engineOn) sinceEngineStart = 0f;
        engineOn = on;
        sinceEngineStart += Math.Max(0f, seconds);
        hueFactor = QuantumPaint.StepHue(hueFactor, on ? 1f : 0f, seconds);
        float hue = on ? QuantumPaint.Flicker(hueFactor, sinceEngineStart) : hueFactor;   // flickers in, fades out smoothly

        var (r1, g1, b1) = QuantumPaint.Hue(((int)curR, (int)curG, (int)curB), hue);
        var (r2, g2, b2) = QuantumPaint.Hue(QuantumPaint.Secondary(deg), hue);

        car.Mods.CustomPrimaryColor = Color.FromArgb(r1, g1, b1);
        car.Mods.CustomSecondaryColor = Color.FromArgb(r2, g2, b2);
        car.Mods.PearlescentColor = (VehicleColor)QuantumPaint.Pearl(deg, hue);
        car.Mods.NeonLightsColor = Color.FromArgb(r1, g1, b1);
    }

    /// <summary>The nearest road node to <paramref name="near"/> and its heading, or beside the player if none is close.</summary>
    static (Vector3, float) Parking(Vector3 near)
    {
        OutputArgument outPos = new(), outHeading = new();
        if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING, near.X, near.Y, near.Z, outPos, outHeading, 1, 3f, 0f))
        {
            var p = outPos.GetResult<Vector3>();
            if (p.DistanceTo(near) <= SearchRadius) return (p, outHeading.GetResult<float>());
            Log.Write($"getaway car: nearest road is {p.DistanceTo(near):F0} m from {near}, parking beside the player instead");
        }
        var ped = Game.Player.Character;
        return (ped.Position + ped.RightVector * 4f, ped.Heading);
    }
}
