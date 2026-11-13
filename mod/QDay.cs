// Q-DAY: a GTA V mission where the player builds a fault-tolerant quantum computer
// out of stolen hardware, it factors every key on earth, and San Andreas loses its mind.
// Every number on the HUD comes from Moth Atlas (see Atlas.cs).
//
// Build: dotnet build   ->   bin/QDay.dll   ->   GTAV/scripts/QDay.dll
// In game: walk to the Q blip at Lester's and press E (INPUT_CONTEXT) to start, Insert reloads SHVDN scripts.
// Log: GTAV/scripts/QDay.log (our own) and GTAV/ScriptHookVDotNet.log (SHVDN's).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Screen = GTA.UI.Screen;
using Font = GTA.UI.Font;

namespace QDay;

public enum Phase { Idle, Pitch, Heist, Delivery, Calibrating, Threshold, QDay, Escape, Ending }

/// <summary>What a phase does on entry, every frame while active, and on exit. Missing callbacks do nothing.</summary>
public sealed class PhaseCallbacks(Action begin = null, Action tick = null, Action end = null)
{
    public readonly Action Begin = begin ?? Nop, Tick = tick ?? Nop, End = end ?? Nop;
    static void Nop() { }
}

public partial class QDayScript : Script
{
    const int DefaultChipIndex = ChipHumaneLabs;

    const int ChipHumaneLabs = 0;
    const int ChipFIB = 1;
    const int ChipMilitary = 2;

    // ---- state ---------------------------------------------------------------
    // The mission is a state machine over Phase. Every transition goes through GoTo(),
    // which runs the old phase's End, then the new phase's Begin; OnTick runs the
    // current phase's Tick. Data a phase needs on entry is set in a field before GoTo.
    readonly Dictionary<Phase, PhaseCallbacks> phases;
    Phase phase = Phase.Idle;
    DateTime phaseStarted = DateTime.UtcNow;

    int chipIndex = DefaultChipIndex;  // which of the three chips we are on
    // The part number on the HUD, 1 to 3. It has the old logical qubit counter's name because OnTick's Hud.Draw
    // line passes it, and the stacked branches change the lines next to that one. Rename it once they're merged.
    int logicalQubits => chipIndex + 1;
    double errorRate = double.NaN;     // live figure from Atlas, NaN = none yet
    double damagePenalty = 0;          // truck damage since pickup, added to errorRate
    const double Threshold = 0.02;     // the finished machine's target; each delivery is judged on its part's own target (Locations.Targets)

    // idle
    Blip startBlip;                    // the mission start, shown while Idle
    int? startRingSoundId;

    // heist
    bool heistSceneSpawned;
    bool heistCanReachTruck;
    bool partAttempted;                // this part has been tried before, so its opening line isn't replayed
    Prop chipProp;
    Vehicle truck;
    float truckHealthAtPickup;
    Blip objectiveBlip;                // the current objective
    Blip truckBlip;                    // on the truck during Delivery, hidden while the player is in it
    Prop keypad;
    readonly HackGate hackGate = new(); // FIB Building and Fort Zancudo: the terminal that releases the truck

    AtlasResult atlasResult;           // the result that arrived this tick, or null
    AtlasResult lastResult;            // latest Atlas result, shown on the HUD and in the ending
    AtlasResult qdayResult;            // the Q-Day job result, set before GoTo(Phase.QDay)
    readonly LockdownRun lockdown;     // holds its own result, so lastResult and the ending credit stay on Q-Day
    Random rng = new();                // replaced by Atlas bytes on Q-Day
    readonly List<string> ticker = [];
    static readonly Locations loc = Locations.Default;
    readonly Atlas atlas;
    readonly bool debug;               // [General] Debug in scripts\QDay.ini: enables the F3-F9 playtest keys

    public QDayScript()
    {
        Log.Init(BaseDirectory);
        TextureSwap.Init();
        // [Atlas] ApiKey in QDay.ini; no key or Offline = true plays from the recorded results.
        atlas = new(Settings.GetValue("Atlas", "ApiKey", ""), Settings.GetValue("Atlas", "Offline", false));
        lockdown = new(LockdownGame.LoadDistricts());

        phases = new()
        {
            [Phase.Idle] = new(begin: BeginIdle, tick: TickIdle, end: EndIdle),
            [Phase.Pitch] = new(begin: BeginPitch, tick: TickPitch),
            [Phase.Heist] = new(begin: BeginHeist, tick: TickHeist, end: EndHeist),
            [Phase.Delivery] = new(begin: BeginDelivery, tick: TickDelivery, end: EndDelivery),
            [Phase.Calibrating] = new(begin: BeginCalibrating, tick: TickCalibrating, end: EndCalibrating),
            [Phase.Threshold] = new(begin: BeginThreshold, tick: TickThreshold, end: EndThreshold),
            [Phase.QDay] = new(begin: BeginQDay, tick: TickQDay, end: EndQDay),
            [Phase.Escape] = new(begin: BeginEscape, tick: TickEscape, end: EndEscape),
            [Phase.Ending] = new(begin: BeginEnding, tick: TickEnding),
        };

        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        Interval = 0;
        debug = Settings.GetValue("General", "Debug", false);
        Log.Write($"Q-DAY loaded. debug={debug}");

        string startupMsg = "~b~Q-DAY~s~ loaded.~n~  go to the ~y~Q~s~ blip to start";
        if (debug)
        {
            startupMsg += "~n~~y~DEBUG:~s~~n~  F3 road test~n~  F4 test wall~n~  F5 teleport~n~  F6 skip wait~n~  F7 Q-Day now~n~  F8 Legion Square~n~  F9 start now~n~  F10 abort~n~  T textures on/off";
        }
        Notification.Show(startupMsg);
    }

    // ---- state machine -------------------------------------------------------
    void GoTo(Phase next)
    {
        Log.Write($"phase {phase} -> {next}");
        phases[phase].End();
        phase = next;
        phaseStarted = DateTime.UtcNow;
        phases[next].Begin();
    }

    double Elapsed() => (DateTime.UtcNow - phaseStarted).TotalSeconds;

    void OnTick(object sender, EventArgs e)
    {
        Hack.Tick();   // first, so its controls are disabled before anything reads them
        atlasResult = atlas.Take();
        if (atlasResult?.Kind == "lockdown") { lockdown.Store(atlasResult); atlasResult = null; }
        if (atlasResult != null)
        {
            lastResult = atlasResult;
            Log.Write($"atlas result {atlasResult.Name}: {atlasResult}");
            CatchQDayResult(atlasResult);   // QDayPrefetch.cs: kept whatever phase we are in
        }
        QDayTitle.Tick();   // the Q-Day title, an overlay; the phase's own drawing (the ticker) still runs

        phases[phase].Tick();
        LockdownGame.TickForceField();
        TextureSwap.Tick();
        if (phase != Phase.Idle) Hud.Draw(phase, chipIndex, logicalQubits, DisplayedError(), damagePenalty, Threshold, lastResult);
    }

    // Script unloading (Insert/F11 reload, or the game closing): cancel any Atlas jobs still running.
    // Blips outlive the script, so remove ours or a reload leaves a duplicate on the map.
    void OnAborted(object sender, EventArgs e)
    {
        TextureSwap.RemoveTextureHook();
        atlas.Dispose();
        CleanUpWorld();
    }

    void CleanUpWorld()
    {
        Log.Write($"cleaning up world");
        Function.Call(Hash.SET_CREATE_RANDOM_COPS, true);
        Function.Call(Hash.DISPLAY_CASH, true);
        Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
        LockdownGame.ClearForceField();
        startBlip?.Delete();
        objectiveBlip?.Delete();
        truckBlip?.Delete();
        truck?.MarkAsNoLongerNeeded();
        chipProp?.MarkAsNoLongerNeeded();
        QDayTitle.Stop();
        keypad?.MarkAsNoLongerNeeded();
        startBlip = null;
        objectiveBlip = null;
        truckBlip = null;
        truck = null;
        chipProp = null;
        keypad = null;
        ClearGuards();
        if (guardGroupMade) { guardGroup.Remove(); guardGroupMade = false; }
        Chaos.RemoveRelationships();
        Hack.Close();
        hackGate.Clear();
        ClearVaultCash();
        ReleaseVaultAudio();
        ClearBriefcases();
        pilot?.MarkAsNoLongerNeeded();
        pilot = null;
        plane?.MarkAsNoLongerNeeded();
        plane = null;
        GetawayCar.Clear();
        TextureSwap.ReplacementEnabled = false;
    }

    /// <summary>This tick's Atlas result if it is the named job and it succeeded, else null.</summary>
    AtlasResult Arrived(string name) => atlasResult is { Status: "done" } r && r.Name == name ? r : null;

    double DisplayedError() => double.IsNaN(errorRate) ? double.NaN : errorRate + damagePenalty;

    // ---- input ---------------------------------------------------------------
    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!debug) return;
        if (e.KeyCode == Keys.F10) { Log.Write("abort"); GoTo(Phase.Idle); }
        // Playtest shortcuts, only with Debug = true in QDay.ini. F4 drops a force field test wall ahead, F5 teleports to the current
        // objective, F6 fakes the pending Atlas result (Threshold: a library pick), F7 jumps straight
        // to Q-Day, F8 drops you in Legion Square, F9 starts the mission from anywhere. All logged so a
        // recording that used them is obvious.
        // F2 opens the current heist's hack anywhere: Fort Zancudo's during that heist, the FIB Building's otherwise.
        if (e.KeyCode == Keys.F2 && !Hack.IsOpen)
        {
            var level = phase == Phase.Heist && chipIndex == ChipMilitary ? HackLevel.Zancudo : HackLevel.FIB;
            Log.Write($"DEBUG F2 open hack {level}");
            Hack.Open(level);
        }
        if (e.KeyCode == Keys.F9 && phase == Phase.Idle) { Log.Write("DEBUG F9 start"); GoTo(Phase.Pitch); }
        // F6 in Calibrating uses the part's recorded result, and returns so the press can't also fire the F6 line below for the next phase.
        // The older F6 Calibrating line further down is left in place, unreachable, to keep it out of the stacked branches' way.
        if (e.KeyCode == Keys.F6 && phase == Phase.Calibrating) { Log.Write("DEBUG F6 recorded calibration"); FinishCalibration(Atlas.RecordedErrorRate(loc.Chips[chipIndex].Key)); return; }
        if (e.KeyCode == Keys.F5) DebugTeleport();
        if (e.KeyCode == Keys.F4) LockdownGame.DropTestWall();
        // F3 tries the next road switching variant on the last F4 wall, Shift+F3 stops and undoes it, Ctrl+F3 reads the nodes round the player.
        if (e.KeyCode == Keys.F3) LockdownGame.RoadTestKey(e.Shift, e.Control, objectiveBlip);
        // Else-ifs on the phase at the moment of the press: acting moves the phase on, so separate checks would chain one press through several phases.
        if (e.KeyCode == Keys.F6)
        {
            if (phase == Phase.Calibrating) { Log.Write("DEBUG F6 in Calibrating: recorded calibration"); FinishCalibration(chipIndex == loc.Chips.Count - 1 ? 0.0173 : 0.085); }
            else if (phase == Phase.Threshold) { Log.Write("DEBUG F6 in Threshold: recorded qday"); StartQDay(QDayLibrary.Pick()); }
            else if (phase == Phase.QDay) { Log.Write("DEBUG F6 in QDay: skip the riot"); GoTo(Phase.Escape); }
        }
        // T toggles the texture replacement; the SetTexture hook stays installed. Not while the hack is open: it takes typing.
        if (e.KeyCode == Keys.T && !Hack.IsOpen && TextureSwap.HookInstalled)
        {
            TextureSwap.ReplacementEnabled = !TextureSwap.ReplacementEnabled;
            Log.Write($"DEBUG T texture replacement {(TextureSwap.ReplacementEnabled ? "on" : "off")}");
            Notification.Show($"~y~DEBUG:~s~ texture replacement {(TextureSwap.ReplacementEnabled ? "~g~on" : "~r~off")}");
        }
        if (e.KeyCode == Keys.F8) { Game.Player.Character.Position = new(195.0f, -933.0f, 30.7f); Log.Write("DEBUG F8 teleport to Legion Square"); }
        if (e.KeyCode == Keys.F7)
        {
            Log.Write("DEBUG F7 jump to Q-Day");
            if (phase == Phase.Idle) StopStoryMissions();
            StartQDay(QDayLibrary.Pick());
        }
    }

    void DebugTeleport()
    {
        var ped = Game.Player.Character;
        var target = phase switch
        {
            Phase.Idle => loc.Start.Pos,
            Phase.Heist => loc.Chips[chipIndex].Pos,
            Phase.Escape => EscapeTarget(),
            _ => loc.Lab.Pos,
        };
        // In Delivery, first put the player back in the truck (story missions can yank them away), then bring the truck home.
        if (phase == Phase.Delivery && truck != null && truck.Exists() && !ped.IsInVehicle(truck))
        {
            ped.SetIntoVehicle(truck, VehicleSeat.Driver);
            Log.Write("DEBUG F5 put player back in the truck");
            Screen.ShowSubtitle("~y~debug: back in the truck. F5 again to drive home~s~", 2500);
            return;
        }
        var ent = ped.IsInVehicle() ? (Entity)ped.CurrentVehicle : ped;
        ent.Position = target + new Vector3(3f, 3f, 0.5f);
        Log.Write($"DEBUG F5 teleport to {target} in phase {phase}");
        Screen.ShowSubtitle("~y~debug teleport~s~", 1500);
    }

    bool CheckForPlayerDeath()
    {
        var ped = Game.Player.Character;
        if (ped.Exists() && ped.IsDead)
        {
            FailMission("you died");
            return true;
        }

        return false;
    }

    // ---- Idle: no mission. Entering it tears everything down ---------------------
    void BeginIdle()
    {
        CleanUpWorld();
        ticker.Clear();
        Hud.ResetTicker();
        // Delivery runs at 3-5 stars. Without this, aborting with F10 mid-delivery
        // ended the mission but left the player being hunted by the military.
        Game.Player.WantedLevel = 0;
        chipIndex = DefaultChipIndex; partAttempted = false; errorRate = double.NaN; damagePenalty = 0;
        lastResult = null; qdayResult = null;
        lockdown.Reset();
        ResetQDayRun();
        stormStarted = false; armedTotal = 0;
    }

    void TickIdle()
    {
        var inMission = Function.Call<bool>(Hash.GET_MISSION_FLAG);
        if (inMission)
        {
            startBlip?.Delete();
            startBlip = null;
            return;
        }

        // Created here rather than in BeginIdle: the script loads straight into Idle without a
        // Begin, and a blip can go missing (save loaded, another script clearing blips).
        if (startBlip == null || !startBlip.Exists())
        {
            startBlip = World.CreateBlip(loc.Start.Pos);
            startBlip.Sprite = (BlipSprite)940;
            startBlip.Color = BlipColor.GolfPlayer2;
            startBlip.Name = "Q-DAY";
        }

        var ped = Game.Player.Character;
        var dist = ped.Position.DistanceTo(loc.Start.Pos);
        if (dist > 50f)
        {
            if (startRingSoundId.HasValue)
            {
                Audio.StopSound(startRingSoundId.Value);
                Function.Call(Hash.RELEASE_NAMED_SCRIPT_AUDIO_BANK, "SCRIPT\\ASSASSINATION_MULTI");
                startRingSoundId = null;
            }
        }
        else
        {
            if (!startRingSoundId.HasValue)
            {
                if (Function.Call<bool>(Hash.REQUEST_SCRIPT_AUDIO_BANK, "SCRIPT\\ASSASSINATION_MULTI", false, -1))
                    startRingSoundId = GTA.Audio.PlaySoundAt(loc.Start.Pos, "ASS_PAYPHONE_RING_master");
            }

            // On foot only: in a vehicle INPUT_CONTEXT is also the horn.
            if (dist <= 2.5f && !ped.IsInVehicle() && !ped.IsDead)
            {
                Screen.ShowHelpTextThisFrame("Press ~INPUT_CONTEXT~ to start ~y~Q-DAY~s~.");
                if (Game.IsControlJustPressed(GTA.Control.Context))
                    GoTo(Phase.Pitch);
            }
        }
    }

    void EndIdle()
    {
        startBlip?.Delete();
        startBlip = null;
        if (startRingSoundId.HasValue)
        {
            Audio.StopSound(startRingSoundId.Value);
            Function.Call(Hash.RELEASE_NAMED_SCRIPT_AUDIO_BANK, "SCRIPT\\ASSASSINATION_MULTI");
            startRingSoundId = null;
        }
    }

    /// <summary>
    /// End the run as a failure: say why, fade out, tear everything down and go back
    /// to Idle, where the start blip starts it again from the beginning. No checkpoint, no auto-restart.
    /// </summary>
    void FailMission(string reason)
    {
        Log.Write("MISSION FAILED: " + reason);
        Hack.Close();                    // before the waits below, so the player isn't left frozen through them
        // Clear the wanted level now: delivery runs at 3-5 stars and the fade below takes seconds.
        Game.Player.WantedLevel = 0;
        QDayTitle.Stop();                // so the failure card isn't drawn under it
        Hud.BigMessage("MISSION FAILED", reason);
        Wait(2500);                      // let it be read before the screen goes
        Screen.FadeOut(1500);
        Wait(1800);
        GoTo(Phase.Idle);                // blip, truck, weather, cash, control
        Screen.FadeIn(1500);
        Screen.ShowSubtitle("~r~Mission failed.~s~ Go back to the ~y~Q~s~ blip to start again.", 6000);
    }

    // ---- Pitch: Lester explains the machine ------------------------------------------
    // Early story missions (Franklin and Lamar, Repossession, Chop) fail the moment the player leaves,
    // which snaps them back to a checkpoint. Q-DAY switches them off while it runs.
    // Controllers first, so nothing is left to notice the mission ending and show "Mission failed".
    static readonly string[] StoryScripts = { "replay_controller", "flow_controller", "candidate_controller", "mission_triggerer_a", "mission_triggerer_b", "mission_triggerer_c", "mission_triggerer_d",
                                              "armenian1", "armenian2", "armenian3", "franklin0", "franklin1" };
    void StopStoryMissions()
    {
        foreach (var name in StoryScripts) Function.Call(Hash.TERMINATE_ALL_SCRIPTS_WITH_THIS_NAME, name);
        Function.Call(Hash.DO_SCREEN_FADE_IN, 500);
        Log.Write("story missions stopped");
    }

    static readonly string[] PitchLines =
    [
        "I've been building something in my garage...",
        "A quantum computer. One that works could open every bank account in Los Santos. Every police file. Every phone.",
        "Mine doesn't work yet. Too many errors.",
        "If I get the error rate under 2%, it starts fixing its own mistakes. Then it opens everything.",
        "All I need is a fridge colder than outer space, a better command system and a better chip. I know where all three are. You in?",
    ];
    const double PitchLineSeconds = 3.5;   // between Lester's texts
    const double PitchCardSeconds = 5;     // the Q-DAY card's subtitle, before the first heist's objective replaces it
    int pitchLinesSent;
    bool pitchCardShown;

    void BeginPitch()
    {
        StopStoryMissions();
        Log.Write("mission start");
        SendQDayJob();                   // runs through the heists, see QDayPrefetch.cs
        pitchLinesSent = 0;
        pitchCardShown = false;
    }

    void TickPitch()
    {
        if (CheckForPlayerDeath()) return;

        double t = Elapsed();
        if (pitchLinesSent < PitchLines.Length && t >= pitchLinesSent * PitchLineSeconds) LesterText(PitchLines[pitchLinesSent++]);
        double cardAt = PitchLines.Length * PitchLineSeconds;
        if (!pitchCardShown && t >= cardAt) { pitchCardShown = true; Hud.BigMessage("Q-DAY", "build a fault-tolerant quantum computer"); }
        if (t > cardAt + PitchCardSeconds) GoTo(Phase.Heist);
    }

    /// <summary>One of Lester's lines, as a text on the phone.</summary>
    static void LesterText(string text) => Notification.Show(NotificationIcon.Lester, "Lester", "Q-Day", text);

    // ---- Heist: go to the current chip --------------------------------------------
    void BeginHeist()
    {
        var chip = loc.Chips[chipIndex];
        objectiveBlip = World.CreateBlip(chip.Pos);
        objectiveBlip.Sprite = BlipSprite.Standard;
        objectiveBlip.Color = BlipColor.Yellow;
        objectiveBlip.ShowRoute = true;
        heistSceneSpawned = false;
        if (!partAttempted) LesterText(chip.Line);   // not again on a retry of the same part
        Screen.ShowSubtitle(chip.Objective, 6000);
        Log.Write($"heist {chipIndex} -> {chip.Label} at {chip.Pos}{(partAttempted ? ", retry" : "")}");
        partAttempted = true;
    }

    void TickHeist()
    {
        if (CheckForPlayerDeath()) return;

        var ped = Game.Player.Character;
        if (!ped.Exists()) return;

        var distToChip = ped.Position.DistanceTo(loc.Chips[chipIndex].Pos);

        if (!heistSceneSpawned && distToChip < 100.0f)
        {
            switch (chipIndex)
            {
                case ChipHumaneLabs: // Humane Labs
                    truck = World.CreateVehicle(VehicleHash.Mule3, new(3611.74f, 3737.08f, 28.92f), 325.04f);
                    if (truck == null) { Log.Write("truck spawn failed"); return; }
                    truck.PlaceOnGround();
                    truck.IsPersistent = true;
                    truck.Mods.PrimaryColor = VehicleColor.MatteWhite;
                    TuneTruck();
                    truck.Doors[VehicleDoorIndex.BackLeftDoor].Open(loose: false, instantly: true);
                    truck.Doors[VehicleDoorIndex.BackRightDoor].Open(loose: false, instantly: true);

                    chipProp = World.CreateProp(new("prop_military_pickup_01"), Vector3.Zero, false, false);
                    chipProp.AttachTo(truck, new(0f, -1f, 0.9f), new(0f, 0f, 90f));

                    keypad = World.CreateProp(new("prop_ld_keypad_01"), KeypadPos, new(0f, 0f, 145.5f), false, false);
                    break;
                case ChipFIB: // FIB
                    truck = World.CreateVehicle(VehicleHash.Boxville2, new(178.55f, -698.54f, 32.94f), 68.86f);
                    if (truck == null) { Log.Write("truck spawn failed"); return; }
                    truck.PlaceOnGround();
                    truck.IsPersistent = true;
                    truck.Mods.PrimaryColor = VehicleColor.MetallicBlack;
                    TuneTruck();

                    chipProp = World.CreateProp(new("prop_military_pickup_01"), Vector3.Zero, false, false);
                    chipProp.AttachTo(truck, new(0f, -1.6f, 0.9f), new(0f, 0f, 90f));
                    hackGate.Spawn(HackLevel.FIB, loc.Chips[chipIndex], truck);
                    SpawnGuards("s_m_m_fibsec_01", WeaponHash.CarbineRifle);
                    break;
                case ChipMilitary: // Military
                    truck = World.CreateVehicle(VehicleHash.Mule3, loc.Chips[chipIndex].Pos, 0.0f);
                    if (truck == null) { Log.Write("truck spawn failed"); return; }
                    truck.PlaceOnGround();
                    truck.IsPersistent = true;
                    truck.Mods.PrimaryColor = VehicleColor.WornOliveArmyGreen;
                    TuneTruck();

                    chipProp = World.CreateProp(new("prop_military_pickup_01"), Vector3.Zero, false, false);
                    chipProp.AttachTo(truck, new(0f, -1f, 0.9f), new(0f, 0f, 90f));
                    hackGate.Spawn(HackLevel.Zancudo, loc.Chips[chipIndex], truck);
                    SpawnGuards("s_m_y_marine_01", WeaponHash.CarbineRifle);
                    break;
            }

            heistSceneSpawned = true;
            heistCanReachTruck = false;
        }

        if (heistSceneSpawned)
        {
            if (truck == null || !truck.Exists() || truck.IsDead)
            {
                FailMission("the chip was destroyed");
                return;
            }

            switch (chipIndex)
            {
                case ChipHumaneLabs: // Humane Lab
                                     // Unlock garage doors if keypad is shot
                    bool unlocked = !keypad.Exists() || keypad.Health < 1000;
                    if (unlocked && !heistCanReachTruck)
                    {
                        heistCanReachTruck = true;
                        SpawnGuards("s_m_m_chemsec_01", WeaponHash.Pistol);
                        World.CreateParticleEffectNonLooped(new("core"), "ent_sht_electrical_box", keypad);
                    }

                    // Held on screen while the player is by the keypad and the shutters are shut.
                    if (!unlocked && ped.Position.DistanceTo(KeypadPos) < KeypadHintRadius)
                    {
                        Screen.ShowSubtitle("Destroy the ~r~keypad~s~ to open the doors.", 100);
                    }

                    Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, Game.GenerateHash("v_ilev_bl_shutter2"), 3627.71f, 3746.72f, 27.7f, /* locked */ !unlocked, /* open ratio */ unlocked ? 1.0 : 0.0, false);
                    Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, Game.GenerateHash("v_ilev_bl_shutter2"), 3620.84f, 3751.53f, 27.7f, /* locked */ !unlocked, /* open ratio */ unlocked ? 1.0 : 0.0, false);
                    break;
                case ChipFIB: // FIB
                case ChipMilitary: // Military
                                   // Locked until the hack at the terminal is solved (HackGate.cs)
                    heistCanReachTruck = hackGate.Tick();
                    truck.LockStatus = heistCanReachTruck ? VehicleLockStatus.Unlocked : VehicleLockStatus.CannotEnter;

                    if (chipIndex == ChipMilitary)
                    {
                        DisableTanks();
                    }
                    break;
            }

            if (heistCanReachTruck)
            {
                var distToTruck = ped.Position.DistanceTo(truck.Position);
                if (distToTruck < 6f && Function.Call<bool>(Hash.IS_ENTITY_ON_SCREEN, truck))
                {
                    GoTo(Phase.Delivery);
                }
            }
        }
    }

    static readonly Vector3 KeypadPos = new(3624.6f, 3749.3f, 28.95f);   // Humane Labs, by the shutters
    const float KeypadHintRadius = 15f;   // metres from the keypad where the hint shows

    const int GuardCount = 4;
    const float GuardRadius = 6f;
    readonly List<Ped> guards = [];
    RelationshipGroup guardGroup;
    bool guardGroupMade;

    void SpawnGuards(string model, WeaponHash weapon)
    {
        for (int i = 0; i < GuardCount; i++)
        {
            double a = 2 * Math.PI * i / GuardCount;
            var at = truck.Position + new Vector3((float)Math.Cos(a) * GuardRadius, (float)Math.Sin(a) * GuardRadius, 0f);
            SpawnGuard(model, weapon, at, (float)(a * 180 / Math.PI) + 90f);   // facing outwards
        }
        Log.Write($"guards: {guards.Count} x {model} round the truck");
    }

    /// <summary>One guard, hostile to the player, standing guard. Goes in guards, so ClearGuards releases it.</summary>
    Ped SpawnGuard(string model, WeaponHash weapon, Vector3 at, float heading)
    {
        if (!guardGroupMade) { guardGroup = World.AddRelationshipGroup("QDAY_GUARDS"); guardGroupMade = true; }
        guardGroup.SetRelationshipBetweenGroups(Game.Player.Character.RelationshipGroup, Relationship.Hate, true);

        var g = World.CreatePed(model, at, heading);
        if (g == null) { Log.Write($"guard {model} spawn failed at {at}"); return null; }
        g.IsPersistent = true;
        g.RelationshipGroup = guardGroup;
        g.Weapons.Give(weapon, 500, true, true);
        Function.Call(Hash.SET_PED_HAS_AI_BLIP, g, true);
        Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, g, "WORLD_HUMAN_GUARD_STAND", 0, true);
        guards.Add(g);
        return g;
    }

    void ClearGuards()
    {
        foreach (var g in guards)
        {
            if (g == null || !g.Exists()) continue;
            Function.Call(Hash.SET_PED_HAS_AI_BLIP, g, false);
            g.MarkAsNoLongerNeeded();
        }
        guards.Clear();
    }

    void TuneTruck()
    {
        truck.Mods.InstallModKit();
        truck.Mods[VehicleModType.Engine].Index = truck.Mods[VehicleModType.Engine].Count - 1;
        truck.Mods[VehicleModType.Brakes].Index = truck.Mods[VehicleModType.Brakes].Count - 1;
        truck.Mods[VehicleModType.Transmission].Index = truck.Mods[VehicleModType.Transmission].Count - 1;
        truck.Mods[VehicleModType.Armor].Index = truck.Mods[VehicleModType.Armor].Count - 1;
        // Only collisions should count as damage, so the truck ignores gunfire. Fire, explosions and crashes still
        // damage it, so a destroyed truck still fails the mission. Tyres are left as they are.
        truck.IsBulletProof = true;
    }

    void DisableTanks()
    {
        // Prevent tanks at Zancudo from shooting the truck
        var tankWeapon = Game.GenerateHash("VEHICLE_WEAPON_TANK");
        foreach (var v in World.GetAllVehicles(["rhino"]))
        {
            Function.Call(Hash.DISABLE_VEHICLE_WEAPON, true, tankWeapon, v, v.Driver);
        }
        Function.Call(Hash.REMOVE_ALL_PROJECTILES_OF_TYPE, tankWeapon, false);
    }

    void EndHeist()
    {
        objectiveBlip?.Delete();
        objectiveBlip = null;
        hackGate.Clear();
    }

    // ---- Delivery: drive the truck home without breaking it -------------------------
    void BeginDelivery()
    {
        var chip = loc.Chips[chipIndex];
        truckHealthAtPickup = truck.BodyHealth;
        damagePenalty = 0;
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, chip.Wanted, false);
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
        objectiveBlip = World.CreateBlip(loc.Lab.Pos);
        objectiveBlip.Color = BlipColor.Yellow;
        objectiveBlip.ShowRoute = true;
        truckBlip = truck.AddBlip();
        truckBlip.Sprite = BlipSprite.Truck;
        truckBlip.Color = BlipColor.Blue;
        truckBlip.Scale = 0.8f;
        truckBlip.IsFlashing = true;
        truckBlip.FlashInterval = 250;
        truckBlip.FlashTimeLeft = 2000;
        LesterText("Got it. Bring it to me. Every bump makes it more error-prone.");
        Log.Write($"pickup {chip.Key}, wanted {chip.Wanted}");
    }

    void TickDelivery()
    {
        if (CheckForPlayerDeath()) return;

        // The truck IS the chip. Delivery only completed while the player was sitting
        // in that exact truck, and nothing anywhere noticed if it stopped existing:
        // there is no timeout on this phase and nothing respawns it, so a truck
        // destroyed during the escape left the mission waiting forever on a marker
        // the player could never satisfy. The only way out was F10, a debug key.
        // Losing the chip ends the run.
        if (truck == null || !truck.Exists() || truck.IsDead)
        {
            FailMission("the chip was destroyed with the truck");
            return;
        }

        switch (chipIndex)
        {
            case ChipHumaneLabs: // Humane Lab
                                 // Unlock garage doors
                Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, Game.GenerateHash("v_ilev_bl_shutter2"), 3627.71f, 3746.72f, 27.7f, /* locked */ false, 1.0, false);
                Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, Game.GenerateHash("v_ilev_bl_shutter2"), 3620.84f, 3751.53f, 27.7f, /* locked */ false, 1.0, false);
                break;
            case ChipFIB: // FIB
                break;
            case ChipMilitary: // Military
                DisableTanks();
                break;
        }

        // Body damage since pickup -> up to +5% on the error meter. Honest and visible.
        var lost = Math.Max(0, truckHealthAtPickup - truck.BodyHealth);
        damagePenalty = Math.Min(0.05, lost / 1000.0 * 0.05);

        var ped = Game.Player.Character;
        var inTruck = ped.IsInVehicle(truck);
        truckBlip.DisplayType = inTruck ? BlipDisplayType.NoDisplay : BlipDisplayType.Default;

        if (Elapsed() > 10 && !inTruck)
        {
            Screen.ShowSubtitle($"Get in the ~b~truck~s~.", 100);
        }

        if (inTruck && ped.Position.DistanceTo(loc.Lab.Pos) < 12f && truck.Speed < 0.5f)
        {
            ped.Task.LeaveVehicle();
            truck.LockStatus = VehicleLockStatus.CannotEnter;
            GoTo(Phase.Calibrating);
        }
    }

    void EndDelivery()
    {
        Game.Player.WantedLevel = 0;
        objectiveBlip?.Delete();
        truckBlip?.Delete();
        objectiveBlip = null;
        truckBlip = null;
    }

    // ---- Calibrating: Atlas measures the delivered chip -----------------------------
    string CalibrationJob => "cal" + chipIndex;

    void BeginCalibrating()
    {
        var chip = loc.Chips[chipIndex];
        atlas.Calibrate(CalibrationJob, chip.Key);
        LesterText("Installing the new part. Let's see where the errors are now.");
        Log.Write($"delivered {chip.Key}, penalty {damagePenalty:P2}, calibrating");
    }

    void TickCalibrating()
    {
        if (CheckForPlayerDeath()) return;

        if (Arrived(CalibrationJob) is { } r) FinishCalibration(r.ErrorRate);
        // Timeout keeps the demo alive, with the part's own recorded result, so a slow job can't fail a part that would pass.
        else if (Elapsed() > 180)
        {
            var key = loc.Chips[chipIndex].Key;
            var recorded = Atlas.RecordedErrorRate(key);
            Log.Write($"calibration timeout, using the recorded {key} result {recorded:P2}");
            FinishCalibration(recorded);
        }
    }

    const int ResultBeforeLesterMs = 2500;   // the result title card shows this long before Lester's text, so the card is read first
    const int LesterBeforeNextMs = 4000;

    /// <summary>
    /// Judges a delivered part on its own target. A pass on the last part reaches Threshold, a pass on another moves
    /// on to the next part, and a part over its target sends the player back for the same part.
    /// </summary>
    void FinishCalibration(double measured)
    {
        var chip = loc.Chips[chipIndex];
        double target = loc.Target(chip);
        errorRate = measured;
        var total = DisplayedError();
        bool clean = total < target;
        bool lastChip = chipIndex == loc.Chips.Count - 1;
        Log.Write($"calibrated {chip.Key}: live {errorRate:P2} + damage {damagePenalty:P2} = {total:P2}, target {Percent(target)}% -> {(clean ? "under the target" : "over the target, same part again")}");

        Hud.BigMessage($"ERROR RATE {total * 100:0.0}%", !clean ? $"over the {Percent(target)}% target" : lastChip ? $"under {Percent(target)}%" : $"target under {Percent(target)}%");
        Wait(ResultBeforeLesterMs);
        LesterText(clean ? PassLine(chip.Key) : "Over the target. That's your driving. Get me another one, and don't damage it.");
        Wait(LesterBeforeNextMs);

        if (clean && lastChip) { GoTo(Phase.Threshold); return; }
        if (clean)
        {
            chipIndex++;
            partAttempted = false;
        }
        GoTo(Phase.Heist);
    }

    static string PassLine(string key) => key switch
    {
        "brisbane" => "Under 11%. Good enough for now.",
        "torino" => "Under 6%. Getting closer.",
        "fez" => "Under 2%. It's fixing its own mistakes. We have a working machine.",
        _ => "Under the target.",
    };

    /// <summary>A target as a percentage without trailing zeros: 11, 6, 2.</summary>
    static string Percent(double fraction) => (fraction * 100).ToString("0.##");

    void EndCalibrating()
    {
        truck?.MarkAsNoLongerNeeded();
        truck = null;
        chipProp?.MarkAsNoLongerNeeded();
        chipProp = null;
        ClearGuards();
    }

    // ---- Threshold: a fixed ThresholdSeconds; the Q-Day job was sent at the pitch -------
    bool thresholdLine;

    void BeginThreshold()
    {
        Function.Call(Hash.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.3f);
        Function.Call(Hash.ANIMPOSTFX_PLAY, "Dont_tazeme_bro", 1500, false);   // short screen flicker
        // The storm used to start HERE. A thunderstorm clears the streets, so by
        // the time the riot began there was nobody left to arm. It now starts in
        // TickQDay, once peds have actually been armed.
        Hud.BigMessage("THRESHOLD", "switching on...");
        thresholdLine = false;
        Log.Write($"threshold: {ThresholdSeconds}s, {QDayJobName} {(liveQDay != null ? "already back" : "still running")}");
    }

    void TickThreshold()
    {
        if (CheckForPlayerDeath()) return;

        if (!thresholdLine && Elapsed() > 3) { thresholdLine = true; LesterText("It's running."); }
        if (Elapsed() > ThresholdSeconds) StartQDay(ThresholdResult());
    }

    void EndThreshold()
    {
        Function.Call(Hash.ANIMPOSTFX_STOP, "Dont_tazeme_bro");
    }

    // ---- QDay: every key on earth is public, the city riots --------------------------
    void StartQDay(AtlasResult r)
    {
        qdayResult = r;
        GoTo(Phase.QDay);
    }

    void BeginQDay()
    {
        var r = qdayResult;
        lastResult = r;
        if (r.Ints is { Count: > 0 }) rng = new AtlasRandom(r.Ints, r.Start);
        Function.Call(Hash.ANIMPOSTFX_PLAY, "DeathFailMPIn", 1500, false);
        Function.Call(Hash.SET_CREATE_RANDOM_COPS, false);
        // The title lands on the seed picked by the first Q-Day random number, taken before the ticker and riot use them.
        // While it runs, the Q-DAY card below is skipped (Hud.BigMessage); if the font fails to load, that card shows instead.
        QDayTitle.Start(r.Ints is { Count: > 0 } ? rng.Next() : null, r.S);
        stormStarted = false;
        armedTotal = 0;
        BuildTicker(r);
        TextureSwap.ReplacementEnabled = true;
        lockdown.Submit(atlas, r);
        Hud.BigMessage("Q-DAY", $"every key on earth is public    Bell S = {r.S:0.00} > 2");
        Log.Write($"Q-DAY. S={r.S} commit={r.Commit} backend={r.Backend} certified={r.Certified} job={r.JobId} offset={r.Start}");
    }

    DateTime lastArm = DateTime.MinValue;
    bool stormStarted;          // the storm is held back until people are armed
    int armedTotal;             // how many peds the riot has actually reached
    // 60m was too tight: it only covered what was almost on top of the player, and
    // the arming pass regularly found nobody at all.
    const float ArmRadius = 150f;

    const double RiotArmSeconds = 2;

    void TickQDay()
    {
        if (CheckForPlayerDeath()) return;

        TickRiotWorld();
        Hud.Ticker(ticker);
        // The escape starts straight after the title (script.md section 8); the riot carries on through it.
        if (Elapsed() > QDayTitleMaxSeconds || (Elapsed() > QDayTitleSeconds && !QDayTitle.IsActive)) GoTo(Phase.Escape);
    }

    /// <summary>The riot's world state, kept up through Q-Day and the escape.</summary>
    void TickRiotWorld()
    {
        // Money does not exist any more.
        Game.Player.Money = 0;
        Function.Call(Hash.DISPLAY_CASH, false);
        Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 3);

        // Everyone goes mental: arm and enrage nearby peds on a timer, chosen by Atlas randomness.
        if ((DateTime.UtcNow - lastArm).TotalSeconds > RiotArmSeconds)
        {
            lastArm = DateTime.UtcNow;
            // MUST come before the loop: EnsureRelationships is what creates the
            // QDAY_RIOT group. Called afterwards, the first pass assigned peds a
            // default (hash 0) group, so they got weapons but none of the
            // hate-everyone relationships - and they were never revisited, because
            // the loop only touches peds that are still Unarmed.
            Chaos.EnsureRelationships();
            var me = Game.Player.Character;
            int armedThisPass = 0, candidates = 0;
            foreach (var p in World.GetNearbyPeds(me, ArmRadius))
            {
                candidates++;
                if (p == me || !p.IsHuman || p.IsDead || p == pilot) continue;   // the riot leaves the escape's pilot alone
                if (p.Weapons.Current.Hash == WeaponHash.Unarmed)
                {
                    var w = Chaos.Weapons[rng.Next(Chaos.Weapons.Length)];
                    p.Weapons.Give(w, 200, true, true);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 46, true);   // always fight
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 5, true);    // can fight armed peds while unarmed
                    Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, p, 0, false);
                    Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, p, true, true);
                    p.RelationshipGroup = Chaos.RiotGroup;
                    armedThisPass++;
                }
            }

            armedTotal += armedThisPass;
            Log.Write($"riot pass: {candidates} peds within {ArmRadius}m, armed {armedThisPass} this pass, {armedTotal} total");

            // Only now let the weather turn. Starting the storm earlier emptied the
            // streets before anyone could be armed, which is why the riot was never
            // seen. Keep the storm - just start it once there is a riot to look at.
            if (!stormStarted && armedTotal > 0)
            {
                stormStarted = true;
                Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "THUNDER");
                Log.Write($"riot: {armedTotal} armed, starting the storm");
            }
        }
    }

    void EndQDay()
    {
        // Nothing: the riot, its texture replacement and the ticker carry on into the escape.
        // EndEscape switches the textures off, and going to Idle cleans up everything else.
    }

    void BuildTicker(AtlasResult r)
    {
        ticker.Clear();
        Hud.ResetTicker();
        List<string> lines = [.. Chaos.Leaks];
        // shuffle with Atlas bytes
        for (int i = lines.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (lines[i], lines[j]) = (lines[j], lines[i]); }
        // A live Atlas result can come back without a commit hash, which would leave a gap in the headline.
        var commit = string.IsNullOrEmpty(r.Commit)
            ? "UNVERIFIED"
            : r.Commit.Substring(0, Math.Min(16, r.Commit.Length)) + "...";
        ticker.Add($"KEY {commit} FACTORED");
        ticker.AddRange(lines);
    }

    // ---- Escape: the bank, the passport, the way out, through the riot (script.md section 9) ----
    // Starts straight after the Q-Day title; the riot, the ticker and the lockdown's force fields carry on.
    // Any car will do for the way out (script step 5), so it has no step of its own; the stretch-goal car (GetawayCar, the
    // Quantum Paint mod's paint) is parked on the road by the briefcase that held the passport, for the player to take or not.
    // The passport is in one of two briefcases, both on the map at once, so the player picks which to go to first. A quantum
    // coin toss (rng, the Q-Day job's numbers) decides whether the first one opened holds it; if not, its blip goes and the
    // other one's is left, one more stop before the plane.
    enum EscapeStep { ToBank, Vault, Passport, WayOut }

    const double QDayTitleSeconds = 5, QDayTitleMaxSeconds = 15;   // Q-Day hands over once the title is done, never later than the max
    EscapeStep escapeStep;
    readonly Blip[] briefcaseBlips = new Blip[2];   // Passport: one per briefcase not opened yet, null once opened
    readonly Pickup[] briefcasePickups = new Pickup[2];   // Passport: the briefcases themselves, picked up to open them
    const string BriefcaseModel = "m24_2_prop_m42_metalbriefcase_01a";
    Ped pilot;
    Vehicle plane;   // WayOut: the pilot's Shamal, parked beside him and locked
    bool shownGetawayCarMessage = false;
    DateTime getawayCarMessageStarted;


    // The Fleeca vault door, swung open by turning it -80 degrees on Z over VaultDoorSeconds, with the heist's unlock sound.
    const string VaultDoorModel = "v_ilev_gb_vauldr";
    const float VaultDoorTurn = -80f;
    const double VaultDoorSeconds = 4;
    Prop vaultDoor;                    // set while the door is opening
    Vector3 vaultDoorShut;             // its rotation before opening
    DateTime vaultDoorStarted;

    /// <summary>Starts the door opening. False if there's no vault door at Escape.VaultDoor.</summary>
    bool OpenVaultDoor()
    {
        var d = loc.Escape.VaultDoor;
        var door = Function.Call<Prop>(Hash.GET_CLOSEST_OBJECT_OF_TYPE, d.X, d.Y, d.Z, 1.0f, Game.GenerateHash(VaultDoorModel), true, false, false);
        if (door == null || !door.Exists()) { Log.Write($"escape: no {VaultDoorModel} near {d}"); return false; }

        Function.Call(Hash.FREEZE_ENTITY_POSITION, door, true);
        if (!vaultAudioLoaded) Log.Write("escape: vault_door audio bank not loaded yet, the unlock sound may not play");
        var p = door.Position;
        Function.Call(Hash.PLAY_SOUND_FROM_COORD, -1, "vault_unlock", p.X, p.Y, p.Z, "dlc_heist_fleeca_bank_door_sounds", false, 0, false);
        vaultDoor = door;
        vaultDoorShut = door.Rotation;
        vaultDoorStarted = DateTime.UtcNow;
        Log.Write("escape: vault door opening");
        return true;
    }

    /// <summary>Call every tick while the door opens: turns it a little further. True once it's fully open.</summary>
    bool TickVaultDoor()
    {
        if (!vaultDoor.Exists()) return true;
        double t = Math.Min(1.0, (DateTime.UtcNow - vaultDoorStarted).TotalSeconds / VaultDoorSeconds);
        float eased = (float)(t * t * (3 - 2 * t));   // starts and stops gently, like a heavy door
        var r = vaultDoorShut;
        Function.Call(Hash.SET_ENTITY_ROTATION, vaultDoor, r.X, r.Y, r.Z + VaultDoorTurn * eased, 2, true);
        return t >= 1.0;
    }

    // The cash: a trolley inside the vault with two cash piles on it, spawned as the door starts opening. The step moves on
    // once both piles are picked up.
    const string VaultTrolleyModel = "m25_2_prop_m52_cash_trolly_empty", VaultCashModel = "ex_cash_pile_01";
    const int VaultCashValue = 100000;   // per pile
    static readonly Vector3 VaultTrolleyPos = new(147.631f, -1047.535f, 28.358f);
    // A bank guard by the vault, spawned with the cash.
    const string VaultGuardModel = "s_m_m_security_01";
    static readonly Vector3 VaultGuardPos = new(148.61f, -1050.26f, 28.35f);
    const float VaultGuardHeading = 340.0f;
    Prop vaultTrolley;
    readonly Pickup[] vaultCash = new Pickup[2];
    bool vaultCashSpawned;

    void SpawnVaultCash()
    {
        if (vaultCashSpawned) return;
        vaultCashSpawned = true;
        vaultTrolley = World.CreateProp(VaultTrolleyModel, VaultTrolleyPos, new(0.0f, 0.0f, 45.0f), false, false);
        if (vaultTrolley != null) vaultTrolley.IsPositionFrozen = true;
        else Log.Write($"escape: couldn't create {VaultTrolleyModel}");
        vaultCash[0] = World.CreatePickup(PickupType.MoneyVariable, new(147.508f, -1047.673f, 29.22f), new(0.0f, 0.0f, 225.0f), VaultCashModel, VaultCashValue);
        vaultCash[1] = World.CreatePickup(PickupType.MoneyVariable, new(147.708f, -1047.484f, 29.215f), new(0.0f, 0.0f, 45.0f), VaultCashModel, VaultCashValue);
        Log.Write($"escape: vault cash spawned ({vaultCash.Count(c => c != null)} of {vaultCash.Length} pickups)");
        if (SpawnGuard(VaultGuardModel, WeaponHash.Pistol, VaultGuardPos, VaultGuardHeading) != null) Log.Write("escape: vault guard spawned");
    }

    /// <summary>True once every cash pickup is collected. A pickup that failed to spawn or has gone counts as taken.</summary>
    bool IsVaultCashTaken() => vaultCash.All(c => c == null || !c.Exists() || c.IsCollected);

    void ClearVaultCash()
    {
        for (int i = 0; i < vaultCash.Length; i++)
        {
            if (vaultCash[i] != null && vaultCash[i].Exists()) vaultCash[i].Delete();
            vaultCash[i] = null;
        }
        if (vaultTrolley != null && vaultTrolley.Exists()) vaultTrolley.Delete();
        vaultTrolley = null;
        vaultCashSpawned = false;
    }

    void CashTaken()
    {
        ReleaseVaultDoor();
        EscapeTo(EscapeStep.Passport, "Got it? Good. Now get your passport. We need to get out of here. I'll text you where the flight's leaving from as soon as I know. I'll see you on the other side.");
    }

    // The door's audio bank, requested from the moment the player reaches the bank so the unlock sound is ready on E.
    bool vaultAudioRequested, vaultAudioLoaded;

    /// <summary>Call every tick from reaching the bank: REQUEST_SCRIPT_AUDIO_BANK loads over several frames, true once loaded.</summary>
    void LoadVaultAudio()
    {
        if (vaultAudioLoaded) return;
        vaultAudioRequested = true;
        vaultAudioLoaded = Function.Call<bool>(Hash.REQUEST_SCRIPT_AUDIO_BANK, "vault_door", false, -1);
        if (vaultAudioLoaded) Log.Write("escape: vault_door audio bank loaded");
    }

    void ReleaseVaultAudio()
    {
        if (!vaultAudioRequested) return;
        vaultAudioRequested = vaultAudioLoaded = false;
        Function.Call(Hash.RELEASE_NAMED_SCRIPT_AUDIO_BANK, "vault_door");
    }

    void ReleaseVaultDoor()
    {
        ReleaseVaultAudio();
        if (vaultDoor == null) return;
        vaultDoor.MarkAsNoLongerNeeded();
        vaultDoor = null;
    }

    void BeginEscape()
    {
        if (lockdown.Begin()) LockdownGame.RaiseForceField(lockdown.Graph, lockdown.Layout);
        EscapeTo(EscapeStep.ToBank, $"Every bank balance just hit zero. Cash is king. We broke the door codes on every major bank. Get to {loc.Escape.BankName} NOW.");
    }

    void TickEscape()
    {
        if (CheckForPlayerDeath()) return;

        TickRiotWorld();
        Hud.Ticker(ticker);
        if (lockdown.Layout != null)
        {
            // if (debug) LockdownGame.DrawWalls(lockdown.Graph, lockdown.Layout);
            LockdownGame.DrawHud(lockdown.Layout);
        }

        GetawayCar.Tick();

        var ped = Game.Player.Character;
        bool onFoot = !ped.IsInVehicle();
        var e = loc.Escape;
        switch (escapeStep)
        {
            case EscapeStep.ToBank:
                if (onFoot && ped.Position.DistanceTo(e.BankDoor) < 6f) EscapeTo(EscapeStep.Vault);
                break;
            case EscapeStep.Vault:
                // E opens the vault door and the cash appears behind it; the step moves on once both piles are taken.
                if (!vaultCashSpawned)
                {
                    LoadVaultAudio();
                    if (EscapeUse(e.Vault, "Press ~INPUT_CONTEXT~ to open the vault door."))
                    {
                        OpenVaultDoor();   // no door found: the cash still appears, don't hold the player up
                        SpawnVaultCash();
                        Screen.ShowSubtitle("Take the ~g~cash~s~.", 8000);
                        objectiveBlip?.Delete();
                        objectiveBlip = World.CreateBlip(VaultTrolleyPos);   // EscapeTarget() is the trolley now
                        objectiveBlip.Sprite = BlipSprite.CashPickup;
                        objectiveBlip.Color = BlipColor.Green;
                    }
                }
                else
                {
                    if (vaultDoor != null && TickVaultDoor()) ReleaseVaultDoor();   // fully open: leave it there
                    if (IsVaultCashTaken()) CashTaken();
                }
                break;
            case EscapeStep.Passport:
                for (int i = 0; i < e.Briefcases.Length; i++)
                {
                    if (briefcaseBlips[i] == null || !BriefcaseTaken(i)) continue;
                    int other = 1 - i;
                    bool here = briefcaseBlips[other] == null || rng.Next(2) == 0;   // the second one opened always has it
                    Log.Write($"escape: briefcase {i}, {e.BriefcaseNames[i]} ({(rng is AtlasRandom ? "Atlas" : "System.Random")}): passport {(here ? "in" : "not in")} it");
                    if (here)
                    {
                        EscapeTo(EscapeStep.WayOut, $"Flight's leaving from {e.WayOutName}. The pilot only takes cash. Go.");
                        break;
                    }
                    briefcaseBlips[i].Delete();
                    briefcaseBlips[i] = null;
                    briefcasePickups[i] = null;   // collected: the game has removed it
                    briefcaseBlips[other].ShowRoute = true;
                    LesterText($"Empty? Then it's in the other one, at {e.BriefcaseNames[other]}.");
                    Screen.ShowSubtitle($"Get the other briefcase from ~y~{e.BriefcaseNames[other]}~s~.", 8000);
                    break;
                }
                break;
            case EscapeStep.WayOut:
                if (!shownGetawayCarMessage && (DateTime.UtcNow - getawayCarMessageStarted).TotalSeconds > 3f)
                {
                    shownGetawayCarMessage = true;
                    if (GetawayCar.Car?.Exists() ?? false)
                        LesterText("There's a ~b~little something~s~ parked next to you. The owner sends his regards. Well, he doesn't know yet. Try not to scratch it.");
                }

                if (ped.Position.DistanceTo(e.Pilot) < 20f)
                {
                    if (pilot != null && pilot.Exists() && !pilot.IsDead)
                    {
                        Screen.ShowSubtitle("Pay the ~b~pilot~s~.", 100);
                        if (EscapeUse(pilot.Position, "Press ~INPUT_CONTEXT~ to pay the pilot."))
                            GoTo(Phase.Ending);
                    }
                    else GoTo(Phase.Ending);   // no pilot: the screen fades on arrival
                }
                break;
        }
    }

    void EndEscape()
    {
        TextureSwap.ReplacementEnabled = false;   // on since BeginQDay
        LockdownGame.ClearForceField();
        objectiveBlip?.Delete();
        objectiveBlip = null;
        ClearBriefcases();
        ReleaseVaultDoor();
        ClearVaultCash();
        pilot?.MarkAsNoLongerNeeded();
        pilot = null;
        plane?.MarkAsNoLongerNeeded();   // not deleted: it would vanish in front of the player as the ending fades
        plane = null;
        GetawayCar.Clear();   // likewise not deleted, and the player may be in it
    }

    /// <summary>Moves the escape on: Lester's line if any, the objective, and the blip.</summary>
    void EscapeTo(EscapeStep step, string line = null)
    {
        Log.Write($"escape: {escapeStep} -> {step}");
        escapeStep = step;
        var e = loc.Escape;
        if (line != null) LesterText(line);
        Screen.ShowSubtitle(step switch
        {
            EscapeStep.ToBank => "Get to the ~y~bank~s~.",
            EscapeStep.Vault => "Open the ~y~vault door~s~.",
            EscapeStep.Passport => $"Your passport's in one of two ~g~briefcases~s~, at {e.BriefcaseNames[0]} or {e.BriefcaseNames[1]}.",
            EscapeStep.WayOut => $"Meet the ~b~pilot~s~ at the airport.",
            _ => "",
        }, 8000);

        objectiveBlip?.Delete();
        objectiveBlip = null;
        ClearBriefcases();

        if (step == EscapeStep.WayOut)
        {
            pilot = World.CreatePed("s_m_m_pilot_01", e.Pilot, e.PilotHeading);
            pilot.IsInvincible = true;
            pilot.BlockPermanentEvents = true;
            Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, pilot, "WORLD_HUMAN_SMOKING", 0, true);
            plane = World.CreateVehicle(VehicleHash.Shamal, e.Plane, e.PlaneHeading);
            if (plane != null)
            {
                plane.IsInvincible = true;   // the riot doesn't get to blow up the way out
                plane.LockStatus = VehicleLockStatus.CannotEnter;
            }
            else Log.Write($"escape: couldn't create the Shamal at {e.Plane}");
            objectiveBlip = pilot.AddBlip();
            objectiveBlip.Sprite = BlipSprite.VIP;
            objectiveBlip.Color = BlipColor.Blue;
            objectiveBlip.ShowRoute = true;

            getawayCarMessageStarted = DateTime.UtcNow;
            shownGetawayCarMessage = false;
            GetawayCar.Spawn(Game.Player.Character.Position);
        }
        else if (step == EscapeStep.Passport)
        {
            // Both briefcases, with no route: the player picks.
            for (int i = 0; i < briefcaseBlips.Length; i++)
            {
                briefcaseBlips[i] = World.CreateBlip(e.Briefcases[i]);
                briefcaseBlips[i].Sprite = BlipSprite.CaptureBriefcase;
                briefcaseBlips[i].Color = BlipColor.Green;
                briefcaseBlips[i].Scale = 0.8f;
                briefcaseBlips[i].Name = "Passport Briefcase";
                briefcasePickups[i] = World.CreatePickup(PickupType.CustomScript, e.Briefcases[i], new(0.0f, 0.0f, 0.0f), BriefcaseModel, 0);
                if (briefcasePickups[i] == null) Log.Write($"escape: couldn't create the {BriefcaseModel} pickup at {e.BriefcaseNames[i]}, falling back to E");
            }
            return;
        }
        else
        {
            objectiveBlip = World.CreateBlip(EscapeTarget());
            objectiveBlip.Color = BlipColor.Yellow;
            objectiveBlip.ShowRoute = true;
        }
    }

    /// <summary>True on the frame briefcase i is picked up. If its pickup failed to spawn, E at the spot opens it instead.</summary>
    bool BriefcaseTaken(int i)
    {
        var p = briefcasePickups[i];
        if (p != null && p.Exists()) return p.IsCollected;
        return EscapeUse(loc.Escape.Briefcases[i], "Press ~INPUT_CONTEXT~ to open the briefcase.");
    }

    void ClearBriefcases()
    {
        for (int i = 0; i < briefcaseBlips.Length; i++)
        {
            briefcaseBlips[i]?.Delete();
            briefcaseBlips[i] = null;
            if (briefcasePickups[i] != null && briefcasePickups[i].Exists()) briefcasePickups[i].Delete();
            briefcasePickups[i] = null;
        }
    }

    /// <summary>Where the current step's blip goes, and where F5 takes the player.</summary>
    Vector3 EscapeTarget() => escapeStep switch
    {
        EscapeStep.ToBank => loc.Escape.BankDoor,
        EscapeStep.Vault => vaultCashSpawned ? VaultTrolleyPos : loc.Escape.Vault,
        EscapeStep.Passport => loc.Escape.Briefcases[briefcaseBlips[0] != null ? 0 : 1],
        _ => loc.Escape.Pilot,
    };

    /// <summary>On foot next to an E site: shows the help text, true on the frame E is pressed.</summary>
    bool EscapeUse(Vector3 at, string help)
    {
        var ped = Game.Player.Character;
        if (ped.IsInVehicle() || ped.Position.DistanceTo(at) > 1.8f) return false;
        Screen.ShowHelpTextThisFrame(help);
        return Game.IsControlJustPressed(GTA.Control.Context);
    }

    // ---- Ending: fade out, credit the hardware, back to Idle -------------------------
    bool endingTitleTried, endingTitleShown, endingCardShown, endingPlaced;
    const double EndingFallbackSeconds = 12;   // how long the fallback card stays before Idle, as before the title

    void BeginEnding()
    {
        Screen.FadeOut(3000);
        Log.Write("ending");
        endingTitleTried = endingTitleShown = endingCardShown = endingPlaced = false;
    }

    void PlaceForEnding()
    {
        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "CLEAR");   // CleanUpWorld lets it go again back in Idle
        var ped = Game.Player.Character;
        if (ped.IsInVehicle()) ped.Task.ClearAllImmediately();   // out of the plane or car, or the vehicle would come along
        var site = loc.Ending;
        ped.Position = site.Pos;
        ped.Heading = site.Heading;
        GameplayCamera.RelativeHeading = 0f;
        Function.Call(Hash.ADVANCE_CLOCK_TIME_TO, 21, 0, 0);
        Log.Write($"ending: clear weather, player at {site.Name} {site.Pos}, heading {site.Heading:F2}");
    }

    void TickEnding()
    {
        // Once the screen is black, and before the title: the storm's over, and the player is at the ending spot.
        if (!endingPlaced && Elapsed() > 3 && (Screen.IsFadedOut || Elapsed() > 4))
        {
            endingPlaced = true;
            PlaceForEnding();
        }
        // Once the screen is black: "you survived qday", landing on the next of the riot's Atlas numbers, with the credit under it.
        if (!endingTitleTried && Elapsed() > 3 && (Screen.IsFadedOut || Elapsed() > 4))
        {
            endingTitleTried = true;
            var credit = lastResult;
            endingTitleShown = QDayTitle.StartEnding(rng is AtlasRandom ? rng.Next() : null, $"Random numbers generated on {credit?.Backend ?? "IBM hardware"} via Moth Atlas. Job {credit?.JobId}.");
        }
        if (endingTitleTried && !endingTitleShown && !endingCardShown)   // the fallback, if the ending font wouldn't load
        {
            var r = lastResult;
            endingCardShown = true; Hud.BigMessage("YOU SURVIVED QDAY", "");
            Notification.Show($"Random numbers generated on {r?.Backend ?? "IBM hardware"} via Moth Atlas. Job {r?.JobId}.");
        }
        // Back to Idle only once the title has finished, or after the fallback card has had its time.
        if (endingTitleTried && !QDayTitle.IsActive && (endingTitleShown || Elapsed() > EndingFallbackSeconds)) { Screen.FadeIn(2000); GoTo(Phase.Idle); }
    }
}

// Deterministic RNG seeded from Atlas bytes, so the chaos is literally quantum-chosen.
public class AtlasRandom : Random
{
    readonly List<int> bytes; int pos;
    // start: where to begin reading. 0 for live runs; library picks start at a random point.
    public AtlasRandom(List<int> b, int start = 0) : base(0) { bytes = b; pos = start; }
    public override int Next(int max) { if (max <= 0) return 0; var v = bytes[pos++ % bytes.Count]; return v % max; }
    public override int Next() => bytes[pos++ % bytes.Count];
}

public static class Chaos
{
    public static readonly WeaponHash[] Weapons = { WeaponHash.Pistol, WeaponHash.Bat, WeaponHash.Knife, WeaponHash.PumpShotgun, WeaponHash.MicroSMG, WeaponHash.GolfClub, WeaponHash.Molotov, WeaponHash.Crowbar };
    public static readonly string[] Leaks = {
        "LSPD INTERNAL MAIL: NOW PUBLIC",
        "MAZE BANK: ALL ACCOUNTS READ $0",
        "LIFEINVADER DMs: EVERY ONE. LIVE.",
        "FLEECA VAULT PIN: 0000",
        "FIB AGENTS: HOME ADDRESSES POSTED",
        "WEAZEL NEWS: 'IS MONEY REAL?'",
        "LOS SANTOS CUSTOMS: YOUR CAR IS NOT YOURS",
        "MERRYWEATHER PAYROLL: PUBLIC",
        "EVERY BURNER PHONE IN BLAINE COUNTY: TRANSCRIBED",
        "CITY HALL: DEEDS DATABASE EMPTY",
    };
    public static RelationshipGroup RiotGroup;
    static bool built;
    /// <summary>Removes the riot's group, so the next run (or a script reload) makes it afresh.</summary>
    public static void RemoveRelationships()
    {
        if (!built) return;
        RiotGroup.Remove();
        built = false;
    }

    public static void EnsureRelationships()
    {
        if (!built) { RiotGroup = World.AddRelationshipGroup("QDAY_RIOT"); built = true; }
        // Everyone hates everyone, including each other and the cops.
        RiotGroup.SetRelationshipBetweenGroups(RiotGroup, Relationship.Hate, true);
        RiotGroup.SetRelationshipBetweenGroups(Game.Player.Character.RelationshipGroup, Relationship.Hate, true);
        RiotGroup.SetRelationshipBetweenGroups(new RelationshipGroup(Game.GenerateHash("COP")), Relationship.Hate, true);
        RiotGroup.SetRelationshipBetweenGroups(new RelationshipGroup(Game.GenerateHash("CIVMALE")), Relationship.Hate, true);
        RiotGroup.SetRelationshipBetweenGroups(new RelationshipGroup(Game.GenerateHash("CIVFEMALE")), Relationship.Hate, true);
    }
}

public static class Hud
{
    static void DrawPanel(Phase phase, int chip, int logical, double err, double penalty, double threshold, AtlasResult last)
    {
        if (Hack.IsOpen) return;   // the hack's panel takes the screen; checked here rather than in OnTick to keep that line unchanged
        var errText = double.IsNaN(err) ? "--" : (err * 100).ToString("0.00") + "%";
        double target = Locations.Default.Target(Locations.Default.Chips[Math.Min(chip, Locations.Default.Chips.Count - 1)]);   // the part's, not the threshold passed in
        if (!double.IsNaN(err)) errText = (err * 100).ToString("0.0") + "%";   // one decimal, set here to leave the line above as it was
        var color = double.IsNaN(err) ? Color.White : (err < target ? Color.LimeGreen : Color.OrangeRed);
        new ContainerElement(new PointF(920, 140), new SizeF(340, 92), Color.FromArgb(160, 0, 0, 0)).Draw();
        new TextElement("Q-DAY  " + phase.ToString().ToUpper(), new PointF(930, 144), 0.42f, Color.White, Font.ChaletLondon, Alignment.Left).Draw();
        new TextElement($"part {logical} / 3", new PointF(930, 168), 0.38f, Color.White, Font.ChaletLondon, Alignment.Left).Draw();
        new TextElement($"error {errText}  target {target * 100:0.##}%  penalty {penalty * 100:0.0}%", new PointF(930, 190), 0.38f, color, Font.ChaletLondon, Alignment.Left).Draw();
        if (phase == Phase.Calibrating || phase == Phase.Threshold)
            new TextElement(phase == Phase.Threshold ? "switching on..." : "calibrating on Atlas...", new PointF(930, 212), 0.34f, Color.LightBlue, Font.ChaletLondon, Alignment.Left).Draw();
    }

    // ---- the leak ticker ------------------------------------------------
    // A news crawl, built as a belt: messages laid end to end, each with its own
    // MEASURED width. Three things this gets right that the first version did not:
    //   1. it advances by REAL TIME (Game.LastFrameTime), not a fixed step per
    //      frame, so the speed is identical at 30, 60 or 144 fps. The old
    //      `x -= 0.004f` every frame made the crawl track the framerate and
    //      lurch on every dip - the main reason it looked broken.
    //   2. items retire on their own measured width, so the text never snaps
    //      back to the right edge mid-sentence and the bar is never empty.
    //   3. the text is given a huge wrap width: by default GTA WRAPS text that
    //      reaches the right edge onto a second line underneath the bar.
    // SHVDN UI elements use a fixed 1280x720 canvas, not the real resolution.
    class TickerItem { public string Text; public float W; }
    static readonly List<TickerItem> belt = new List<TickerItem>();
    static float tickerOffset;
    static int tickerFeed;

    const float CanvasW = 1280f, CanvasH = 720f;
    const float TickerBarH = 18f;     // thin enough to sit clear of the minimap
    const float TickerScale = 0.28f;   // matched to the bar height
    const float TickerSpeed = 150f;    // canvas pixels per SECOND
    const float TickerGap = 60f;     // space between messages
    const float NoWrap = 6000f;   // effectively disables GTA's text wrapping
    const string TickerSep = "  •  ";
    static readonly Color TickerBarCol = Color.FromArgb(205, 100, 0, 0);
    static readonly Color TickerEdgeCol = Color.FromArgb(230, 190, 30, 30);
    static readonly Color TickerTextCol = Color.FromArgb(255, 255, 240, 235);

    /// <summary>Clear the crawl so a new Q-Day starts from the beginning.</summary>
    public static void ResetTicker() { belt.Clear(); tickerOffset = 0f; tickerFeed = 0; }

    static float MeasureTicker(string s)
    {
        var t = new TextElement(s, new PointF(0, 0), TickerScale, TickerTextCol,
                                Font.ChaletLondon, Alignment.Left, false, true, NoWrap);
        float w = t.Width;
        return w > 0f ? w : s.Length * TickerScale * 16f;   // metrics fallback
    }

    public static void Ticker(List<string> lines)
    {
        if (lines.Count == 0) return;

        float dt = Game.LastFrameTime;
        if (dt <= 0f || dt > 0.5f) dt = 1f / 60f;           // guard pauses and hitches

        // Keep the belt long enough to cover the screen plus a lead-in.
        float total = 0f;
        for (int i = 0; i < belt.Count; i++) total += belt[i].W;
        int guard = 0;
        while (total < CanvasW * 2f && guard++ < 40)
        {
            var text = lines[tickerFeed++ % lines.Count] + TickerSep;
            var item = new TickerItem { Text = text, W = MeasureTicker(text) + TickerGap };
            belt.Add(item);
            total += item.W;
        }

        tickerOffset += TickerSpeed * dt;
        while (belt.Count > 0 && tickerOffset >= belt[0].W)
        {
            tickerOffset -= belt[0].W;                       // keeps the offset bounded
            belt.RemoveAt(0);
        }

        float barY = CanvasH - TickerBarH;
        new ContainerElement(new PointF(0, barY), new SizeF(CanvasW, TickerBarH), TickerBarCol).Draw();
        new ContainerElement(new PointF(0, barY), new SizeF(CanvasW, 1.5f), TickerEdgeCol).Draw();

        float textH = TickerScale * 36f;
        float ty = barY + (TickerBarH - textH) * 0.5f;       // centre the glyphs in the bar
        float x = -tickerOffset;
        for (int i = 0; i < belt.Count && x < CanvasW; i++)
        {
            var item = belt[i];
            if (x + item.W > 0f)
                new TextElement(item.Text, new PointF(x, ty), TickerScale, TickerTextCol,
                                Font.ChaletLondon, Alignment.Left, false, true, NoWrap).Draw();
            x += item.W;
        }
    }

    /// <summary>The HUD panel, which steps aside while the Q-Day title is on screen.</summary>
    public static void Draw(Phase phase, int chip, int logical, double err, double penalty, double threshold, AtlasResult last)
    {
        if (!QDayTitle.IsActive) DrawPanel(phase, chip, logical, err, penalty, threshold, last);
    }

    public static void BigMessage(string title, string sub)
    {
        if (title == "Q-DAY" && QDayTitle.IsActive) return;   // the animated title replaces this card; it's the fallback if the title can't start
        Screen.ShowSubtitle($"~b~{title}~s~  {sub}", 6000);
        Notification.Show($"~b~{title}~s~ {sub}");
    }
}

public class Locations
{
    public class Chip { public string Key, Label, Line, Objective; public Vector3 Pos; public int Wanted; public Vector3 HackPos; public float HackHeading; }
    public class Site { public string Name; public Vector3 Pos; public float Heading; }
    public Site Start, Lab; public List<Chip> Chips; public Site Ending;

    /// <summary>The escape's stops (QDayScript.TickEscape). All placeholders, to verify in game: script.md section 9, "For Alejandro".</summary>
    public class EscapeSites
    {
        public string BankName, WayOutName;                       // as Lester and the objectives say them
        public string[] BriefcaseNames;                           // the two briefcases' places, as the objectives say them
        public Vector3 BankDoor, VaultDoor, Vault, Pilot, Plane;
        public float PilotHeading, PlaneHeading;
        public Vector3[] Briefcases;                              // two, both on the map during the passport step
    }
    public EscapeSites Escape;
    // Each part's error-rate target, keyed by Chip.Key. It's kept out of Chip because feature/hack changes those lines; fold it in once that's merged.
    public Dictionary<string, double> Targets;
    public double Target(Chip chip) => Targets[chip.Key];

    public static readonly Locations Default = new()
    {
        Start = new()
        {
            Name = "Some payphone (mission start)",
            Pos = new(395.24f, -798.65f, 28.78f)
        },
        Lab = new()
        {
            Name = "Lester's house (the machine is built here)",
            Pos = new(1279.96f, -1733.71f, 53.55f),
        },
        Chips = [
            new() { Key = "brisbane", Label = "Humane Labs cryostat",       Pos = new(3627.89f, 3754.49f, 28.52f), Wanted = 3,
                    Line = "First, the fridge. The chip only works when it's kept just above absolute zero, and mine isn't cold enough. Every bit of heat causes errors. Humane Labs has what we need... a cryostat.",
                    Objective = "Steal the ~y~cryostat~s~ from Humane Labs." },
            // HackPos and HackHeading are the hack terminal: placeholders 4 m to the right of each truck, which need verifying in game.
            new() { Key = "torino",   Label = "FIB Building control plane", Pos = new(156.67f, -713.8f, 33.0f),    Wanted = 4,
                    Line = "Next, the command system. It tells the qubits what to do. Mine's too old. Too many errors. I've heard the FIB has a brand new, government-grade one. Get it however you can.",
                    Objective = "Steal the ~y~command system~s~ from the FIB Building.",
                    HackPos = new(179.99f, -694.81f, 32.94f), HackHeading = 68.86f },
            new() { Key = "fez",      Label = "Fort Zancudo qubit array",   Pos = new(-2047.0f, 3132.0f, 32.8f),   Wanted = 5,
                    Line = "Last one. The chip. The qubits sit on it, and mine's too flawed to hold them steady. Fort Zancudo has a military-grade one. It's called a qubit array. Do whatever it takes, and DON'T DAMAGE IT.",
                    Objective = "Steal the ~y~qubit array~s~ from Fort Zancudo.",
                    HackPos = new(-2043.0f, 3132.0f, 33.8f), HackHeading = 0f },
        ],
        // Each part has to come in under its own target, which tightens as the machine gets closer. With the recorded
        // error rates (8.5%, 4.56%, 1.73%) and 5 points per 1000 body health lost, the drive back can lose about 500
        // body health with the fridge, about 280 with the command system and about 54 with the chip.
        Targets = new() { ["brisbane"] = 0.11, ["torino"] = 0.06, ["fez"] = 0.02 },
        Ending = new()
        {
            Name = "Vinewood sign",   // where the player stands as the ending fades back in
            Pos = new(726.32f, 1195.25f, 325.97f),
            Heading = 160.55f
        },
        // Placeholders from memory of the map, none checked in game yet. Bank: the Legion Square Fleeca. Briefcases: Forum Drive
        // (Strawberry) and Grove Street. Way out: a hangar apron at LSIA (the airport gates and fence are unchecked; a helipad is the alternative).
        Escape = new()
        {
            BankName = "the Fleeca by Legion Square",
            WayOutName = "the airport",
            BankDoor = new(150.3f, -1037.4f, 29.4f),
            VaultDoor = new(148.03f, -1044.36f, 29.51f),   // GET_CLOSEST_OBJECT_OF_TYPE looks within 1 m of this
            Vault = new(146.9f, -1046.5f, 29.4f),
            BriefcaseNames = ["Forum Drive", "Grove Street"],
            Briefcases = [new(-59.655f, -1441.741f, 31.487f), new(53.948f, -1853.865f, 22.222f)],
            Pilot = new(-1388.35f, -3248.55f, 13.94f),
            PilotHeading= 278.0f,
            Plane = new(-1388.77f, -3257.72f, 13.94f),
            PlaneHeading = 330.0f,
        },
    };
}
