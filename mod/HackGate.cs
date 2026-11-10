// The terminal that gates the FIB Building and Fort Zancudo heists: a prop by the truck with a marker
// and a blip. The truck stays locked until the player solves the hack there (Hack.cs). Humane Labs
// keeps its keypad and doesn't use this. Calls natives, so script thread only.

using System.Drawing;
using GTA;
using GTA.Math;
using GTA.UI;
using Screen = GTA.UI.Screen;

namespace QDay;

public sealed class HackGate
{
    const string TerminalModel = "prop_elecbox_02a";   // an electrical box, big enough to spot; needs checking in game
    const string FallbackModel = "prop_laptop_01a";
    const float UseRadius = 1.5f;                       // metres, measured flat, from the terminal
    const float UseHeight = 2f;                         // and no more than this above or below it
    const string Objective = "Hack the ~y~terminal~s~ to release the truck.";
    const string HelpText = "Press ~INPUT_CONTEXT~ to hack the terminal";
    const string SuccessSound = "Hack_Success", SuccessSoundSet = "DLC_HEIST_BIOLAB_PREP_HACKING_SOUNDS";   // name unconfirmed
    static readonly Color MarkerColor = Color.FromArgb(150, 255, 205, 0);

    Prop terminal;
    Blip blip;
    Vehicle truck;
    HackLevel level;
    Vector3 ground;
    bool spawned, wasOpen;
    int? soundId;

    public bool Solved { get; private set; }

    /// <summary>Puts the terminal up by this heist's truck and locks the truck.</summary>
    public void Spawn(HackLevel hackLevel, Locations.Chip chip, Vehicle heistTruck)
    {
        Clear();
        level = hackLevel;
        truck = heistTruck;
        Solved = false;
        wasOpen = false;
        spawned = true;
        truck.LockStatus = VehicleLockStatus.CannotEnter;

        float z = World.GetGroundHeight(chip.HackPos + new Vector3(0f, 0f, 2f));
        ground = z > 0f ? new Vector3(chip.HackPos.X, chip.HackPos.Y, z) : chip.HackPos;

        Model model = new(TerminalModel);
        if (!model.IsInCdImage) model = new(FallbackModel);
        terminal = World.CreateProp(model, ground, new Vector3(0f, 0f, chip.HackHeading), false, true);
        if (terminal == null) Log.Write($"hack terminal: could not create {model.Hash:X}, marker and blip only");

        blip = World.CreateBlip(ground);
        blip.Sprite = BlipSprite.Standard;
        blip.Color = BlipColor.Green;
        blip.Scale = 0.6f;
        blip.Name = "Terminal";

        Screen.ShowSubtitle(Objective, 6000);
        Log.Write($"hack terminal for {level} at {ground}, truck locked");
    }

    /// <summary>Call every heist tick once spawned. True once the hack is solved and the truck is free.</summary>
    public bool Tick()
    {
        if (!spawned) return false;
        if (Solved) return true;

        if (Hack.JustSolved && Hack.SolvedLevel == level)
        {
            Release();
            return true;
        }

        bool open = Hack.IsOpen;
        if (wasOpen && !open) Screen.ShowSubtitle(Objective, 6000);   // closed without solving
        wasOpen = open;
        if (open) return false;

        World.DrawMarker(MarkerType.VerticalCylinder, ground + new Vector3(0f, 0f, 0.02f), Vector3.Zero, Vector3.Zero, new Vector3(1.2f, 1.2f, 0.4f), MarkerColor);

        var ped = Game.Player.Character;
        if (ped.IsInVehicle() || ped.IsDead) return false;
        var at = terminal != null && terminal.Exists() ? terminal.Position : ground;
        if (ped.Position.DistanceTo2D(at) <= UseRadius && System.Math.Abs(ped.Position.Z - at.Z) <= UseHeight)
        {
            Screen.ShowHelpTextThisFrame(HelpText);
            if (Game.IsControlJustPressed(Control.Context)) Hack.Open(level);
        }
        return false;
    }

    void Release()
    {
        Solved = true;
        if (truck != null && truck.Exists()) truck.LockStatus = VehicleLockStatus.Unlocked;
        blip?.Delete();
        blip = null;
        soundId = Audio.PlaySoundFrontend(SuccessSound, SuccessSoundSet);
        Log.Write($"hack {level} solved at the terminal, truck unlocked");
    }

    /// <summary>Removes the blip and lets the game have the prop back. Safe to call at any time.</summary>
    public void Clear()
    {
        if (soundId.HasValue) Audio.ReleaseSound(soundId.Value);
        soundId = null;
        if (!spawned) return;
        spawned = false;
        blip?.Delete();
        blip = null;
        terminal?.MarkAsNoLongerNeeded();
        terminal = null;
        if (truck != null && truck.Exists() && !Solved) truck.LockStatus = VehicleLockStatus.Unlocked;
        truck = null;
    }
}
