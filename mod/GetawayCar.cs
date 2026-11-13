// The escape's getaway car (script.md section 9, step 5, the stretch goal): a Jester (racecar) with the Quantum Paint
// mod's paint, parked on the road nearest the briefcase that held the passport. The paint maths is QuantumPaint; this
// file is the natives. The car's livery texture is left to TextureSwap (media/qblur_livery.png in gta-quantum-paint);
// this only selects livery 0 so a swapped texture would show.

using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QDay;

public static class GetawayCar
{
    public const VehicleHash Model = VehicleHash.Jester2;
    const float SearchRadius = 60f;   // metres from the briefcase to look for a road; further, and it's parked beside the player

    const ulong SetNeonEnabled = 0x2AA720E4287BF269;   // SET_VEHICLE_NEON_ENABLED
    const ulong SetNeonColour = 0x8E0A582209A62695;    // SET_VEHICLE_NEON_COLOUR

    static Vehicle car;
    static Blip blip;
    static int stockWheelColour;
    static float curR = -1f, curG, curB;   // the eased base coat, -1 = snap on the first frame

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
        car.IsPersistent = true;
        Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, car, 5f);
        Function.Call(Hash.SET_VEHICLE_MOD_KIT, car, 0);
        Function.Call(Hash.SET_VEHICLE_LIVERY, car, 0);
        Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, car, 1);   // unlocked
        car.IsEngineRunning = true;
        for (int i = 0; i < 4; i++) Function.Call((Hash)SetNeonEnabled, car, i, true);
        OutputArgument pearl = new(), wheel = new();
        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, car, pearl, wheel);
        stockWheelColour = wheel.GetResult<int>();
        curR = -1f;
        Paint(0f);

        blip = car.AddBlip();
        blip.Sprite = BlipSprite.SportsCar;
        blip.Color = BlipColor.Blue;
        blip.Name = "Getaway car";
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
        var (r2, g2, b2) = QuantumPaint.Secondary(deg);

        Function.Call(Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR, car, (int)curR, (int)curG, (int)curB);
        Function.Call(Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR, car, r2, g2, b2);
        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, car, QuantumPaint.Pearl(deg), stockWheelColour);
        Function.Call((Hash)SetNeonColour, car, (int)curR, (int)curG, (int)curB);
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
