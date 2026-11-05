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
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Screen = GTA.UI.Screen;
using Font = GTA.UI.Font;

namespace QDay;

public enum Phase { Idle, Pitch, Heist, Delivery, Calibrating, Threshold, QDay, Ending }

/// <summary>What a phase does on entry, every frame while active, and on exit. Missing callbacks do nothing.</summary>
public sealed class PhaseCallbacks(Action begin = null, Action tick = null, Action end = null)
{
    public readonly Action Begin = begin ?? Nop, Tick = tick ?? Nop, End = end ?? Nop;
    static void Nop() { }
}

public class QDayScript : Script
{
    // ---- state ---------------------------------------------------------------
    // The mission is a state machine over Phase. Every transition goes through GoTo(),
    // which runs the old phase's End, then the new phase's Begin; OnTick runs the
    // current phase's Tick. Data a phase needs on entry is set in a field before GoTo.
    readonly Dictionary<Phase, PhaseCallbacks> phases;
    Phase phase = Phase.Idle;
    DateTime phaseStarted = DateTime.UtcNow;

    int chipIndex = 0;                 // which of the three chips we are on
    int logicalQubits = 0;             // deliveries that came in under threshold
    double errorRate = double.NaN;     // live figure from Atlas, NaN = none yet
    double damagePenalty = 0;          // truck damage since pickup, added to errorRate
    const double Threshold = 0.02;     // fez/marrakesh land at ~1.7%, brisbane at 8.5%
    Vehicle truck;
    float truckHealthAtPickup;
    Blip blip;                         // the current objective
    Blip startBlip;                    // the mission start, shown while Idle
    AtlasResult atlasResult;           // the result that arrived this tick, or null
    AtlasResult lastResult;            // latest Atlas result, shown on the HUD and in the ending
    AtlasResult qdayResult;            // the Q-Day job result, set before GoTo(Phase.QDay)
    Random rng = new();                // replaced by Atlas bytes on Q-Day
    readonly List<string> ticker = [];
    static readonly Locations loc = Locations.Default;
    readonly Atlas atlas;
    readonly bool debug;               // [General] Debug in scripts\QDay.ini: enables the F5-F9 playtest keys

    public QDayScript()
    {
        Log.Init(BaseDirectory);
        // [Atlas] ApiKey in QDay.ini; no key or Offline = true plays from the recorded results.
        atlas = new(Settings.GetValue("Atlas", "ApiKey", ""), Settings.GetValue("Atlas", "Offline", false));

        phases = new()
        {
            [Phase.Idle]        = new(begin: BeginIdle,        tick: TickIdle,        end: EndIdle),
            [Phase.Pitch]       = new(begin: BeginPitch,       tick: TickPitch),
            [Phase.Heist]       = new(begin: BeginHeist,       tick: TickHeist,       end: EndHeist),
            [Phase.Delivery]    = new(begin: BeginDelivery,    tick: TickDelivery,    end: EndDelivery),
            [Phase.Calibrating] = new(begin: BeginCalibrating, tick: TickCalibrating, end: EndCalibrating),
            [Phase.Threshold]   = new(begin: BeginThreshold,   tick: TickThreshold,   end: EndThreshold),
            [Phase.QDay]        = new(begin: BeginQDay,        tick: TickQDay,        end: EndQDay),
            [Phase.Ending]      = new(begin: BeginEnding,      tick: TickEnding),
        };

        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        Interval = 0;
        debug = Settings.GetValue("General", "Debug", false);
        Log.Write($"Q-DAY loaded. debug={debug}");

        string startupMsg = "~b~Q-DAY~s~ loaded.~n~  go to the ~y~Q~s~ blip to start~n~  F10 abort";
        if (debug)
        {
          startupMsg += "~n~~y~DEBUG:~s~~n~  F5 teleport~n~  F6 skip wait~n~  F7 Q-Day now~n~  F8 Legion Square~n~  F9 start now";
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
        atlasResult = atlas.Take();
        if (atlasResult != null)
        {
            lastResult = atlasResult;
            Log.Write($"atlas result {atlasResult.Name}: {atlasResult}");
        }

        phases[phase].Tick();
        if (phase != Phase.Idle) Hud.Draw(phase, chipIndex, logicalQubits, DisplayedError(), Threshold, lastResult);
    }

    // Script unloading (Insert/F11 reload, or the game closing): cancel any Atlas jobs still running.
    // Blips outlive the script, so remove ours or a reload leaves a duplicate on the map.
    void OnAborted(object sender, EventArgs e)
    {
        atlas.Dispose();
        startBlip?.Delete();
        blip?.Delete();
    }

    /// <summary>This tick's Atlas result if it is the named job and it succeeded, else null.</summary>
    AtlasResult Arrived(string name) => atlasResult is { Status: "done" } r && r.Name == name ? r : null;

    double DisplayedError() => double.IsNaN(errorRate) ? double.NaN : errorRate + damagePenalty;

    // ---- input ---------------------------------------------------------------
    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F10) { Log.Write("abort"); GoTo(Phase.Idle); }
        if (!debug) return;
        // Playtest shortcuts, only with Debug = true in QDay.ini. F5 teleports to the current
        // objective, F6 fakes the pending Atlas result from the recorded run, F7 jumps straight
        // to Q-Day, F8 drops you in Legion Square, F9 starts the mission from anywhere. All logged so a
        // recording that used them is obvious.
        if (e.KeyCode == Keys.F9 && phase == Phase.Idle) { Log.Write("DEBUG F9 start"); GoTo(Phase.Pitch); }
        if (e.KeyCode == Keys.F5) DebugTeleport();
        if (e.KeyCode == Keys.F6 && phase == Phase.Calibrating) { Log.Write("DEBUG F6 recorded calibration"); FinishCalibration(chipIndex == loc.Chips.Count - 1 ? 0.0173 : 0.085); }
        if (e.KeyCode == Keys.F6 && phase == Phase.Threshold) { Log.Write("DEBUG F6 recorded qday"); StartQDay(AtlasResult.Recorded()); }
        if (e.KeyCode == Keys.F8) { Game.Player.Character.Position = new(195.0f, -933.0f, 30.7f); Log.Write("DEBUG F8 teleport to Legion Square"); }
        if (e.KeyCode == Keys.F7)
        {
            Log.Write("DEBUG F7 jump to Q-Day");
            if (phase == Phase.Idle) StopStoryMissions();
            StartQDay(AtlasResult.Recorded());
        }
    }

    void DebugTeleport()
    {
        var ped = Game.Player.Character;
        var target = phase switch
        {
          Phase.Idle => loc.Start.Pos,
          Phase.Heist => loc.Chips[chipIndex].Pos,
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

    // ---- Idle: no mission. Entering it tears everything down ---------------------
    void BeginIdle()
    {
        Function.Call(Hash.SET_RIOT_MODE_ENABLED, false);
        Function.Call(Hash.SET_CREATE_RANDOM_COPS, true);
        Function.Call(Hash.DISPLAY_CASH, true);
        Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
        Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
        blip?.Delete(); blip = null;
        truck?.MarkAsNoLongerNeeded(); truck = null;
        ticker.Clear(); Hud.ResetTicker();
        // Delivery runs at 3-5 stars. Without this, aborting with F10 mid-delivery
        // ended the mission but left the player being hunted by the military.
        Game.Player.WantedLevel = 0;
        chipIndex = 0; logicalQubits = 0; errorRate = double.NaN; damagePenalty = 0;
        lastResult = null; qdayResult = null;
        stormStarted = false; armedTotal = 0;
    }

    const float StartRadius = 2.5f;

    void TickIdle()
    {
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
        if (dist > 50f) return;
        World.DrawMarker(MarkerType.VerticalCylinder, loc.Start.Pos, Vector3.Zero, Vector3.Zero,
                         new(StartRadius * 2, StartRadius * 2, 1f), Color.FromArgb(120, 240, 200, 80));
        // On foot only: in a vehicle INPUT_CONTEXT is also the horn.
        if (dist > StartRadius || ped.IsInVehicle() || ped.IsDead) return;
        Screen.ShowHelpTextThisFrame("Press ~INPUT_CONTEXT~ to start ~y~Q-DAY~s~.");
        if (Game.IsControlJustPressed(GTA.Control.Context)) GoTo(Phase.Pitch);
    }

    void EndIdle() { startBlip?.Delete(); startBlip = null; }

    /// <summary>
    /// End the run as a failure: say why, fade out, tear everything down and go back
    /// to Idle, where the start blip starts it again from the beginning. No checkpoint, no auto-restart.
    /// </summary>
    void FailMission(string reason)
    {
        Log.Write("MISSION FAILED: " + reason);
        // Clear the wanted level now: delivery runs at 3-5 stars and the fade below takes seconds.
        Game.Player.WantedLevel = 0;
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
        Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
        Log.Write("story missions stopped");
    }

    void BeginPitch()
    {
        StopStoryMissions();
        Log.Write("mission start");
        Screen.ShowSubtitle("~b~Lester:~s~ A machine that only factors numbers. That's all it does.", 6000);
        Hud.BigMessage("Q-DAY", "build a fault-tolerant quantum computer");
    }

    void TickPitch()
    {
        if (Elapsed() > 7) GoTo(Phase.Heist);
    }

    // ---- Heist: go to the current chip --------------------------------------------
    void BeginHeist()
    {
        var chip = loc.Chips[chipIndex];
        blip = World.CreateBlip(chip.Pos);
        blip.Sprite = BlipSprite.Standard; blip.Color = BlipColor.Blue; blip.ShowRoute = true;
        Screen.ShowSubtitle($"~b~Lester:~s~ {chip.Line}", 6000);
        Log.Write($"heist {chipIndex} -> {chip.Label} at {chip.Pos}");
    }

    void TickHeist()
    {
        var ped = Game.Player.Character;
        if (ped.Position.DistanceTo(loc.Chips[chipIndex].Pos) >= 6f) return;

        // Pick up: the crate is the truck. If the spawn fails, stay here and try again next tick.
        truck = World.CreateVehicle(VehicleHash.Mule, ped.Position + ped.ForwardVector * 5f, ped.Heading);
        if (truck == null) { Log.Write("truck spawn failed"); return; }
        truck.PlaceOnGround();
        // Keep the engine from streaming the truck out when the player gets far
        // from it. Without this, "the truck no longer exists" in Delivery could mean
        // "despawned" rather than "destroyed", and fail the mission unfairly.
        truck.IsPersistent = true;
        GoTo(Phase.Delivery);
    }

    void EndHeist() { blip?.Delete(); blip = null; }

    // ---- Delivery: drive the truck home without breaking it -------------------------
    void BeginDelivery()
    {
        var chip = loc.Chips[chipIndex];
        truckHealthAtPickup = truck.BodyHealth;
        damagePenalty = 0;
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, chip.Wanted, false);
        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
        blip = World.CreateBlip(loc.Lab.Pos); blip.Color = BlipColor.Green; blip.ShowRoute = true;
        Screen.ShowSubtitle($"~b~Lester:~s~ Got it. Bring it to me. ~r~Every bump is decoherence.~s~", 6000);
        Log.Write($"pickup {chip.Key}, wanted {chip.Wanted}");
    }

    void TickDelivery()
    {
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

        // Body damage since pickup -> up to +5% on the error meter. Honest and visible.
        var lost = Math.Max(0, truckHealthAtPickup - truck.BodyHealth);
        damagePenalty = Math.Min(0.05, lost / 1000.0 * 0.05);

        var ped = Game.Player.Character;
        if (ped.IsInVehicle(truck) && ped.Position.DistanceTo(loc.Lab.Pos) < 12f) GoTo(Phase.Calibrating);
    }

    void EndDelivery()
    {
        Game.Player.WantedLevel = 0;
        blip?.Delete(); blip = null;
    }

    // ---- Calibrating: Atlas measures the delivered chip -----------------------------
    string CalibrationJob => "cal" + chipIndex;

    void BeginCalibrating()
    {
        var chip = loc.Chips[chipIndex];
        atlas.Calibrate(CalibrationJob, chip.Key);
        Screen.ShowSubtitle("~b~Lester:~s~ Installing. Calibrating on the real noise model...", 5000);
        Log.Write($"delivered {chip.Key}, penalty {damagePenalty:P2}, calibrating");
    }

    void TickCalibrating()
    {
        if (Arrived(CalibrationJob) is { } r) FinishCalibration(r.ErrorRate);
        // Timeout keeps the demo alive.
        else if (Elapsed() > 180) { Log.Write("calibration timeout"); FinishCalibration(0.1); }
    }

    void FinishCalibration(double measured)
    {
        errorRate = measured;
        var total = DisplayedError();
        bool clean = total <= Threshold;
        bool lastChip = chipIndex == loc.Chips.Count - 1;
        Log.Write($"calibrated chip {chipIndex}: live {errorRate:P2} + damage {damagePenalty:P2} = {total:P2} -> {(clean ? "LOGICAL QUBIT" : "too noisy")}");
        if (clean) { logicalQubits++; Hud.BigMessage("LOGICAL QUBIT", $"{logicalQubits} / 3  error {total:P1}"); }
        else Hud.BigMessage("TOO NOISY", $"error {total:P1} > threshold {Threshold:P0}. " + (lastChip ? "bring another one, carefully." : "upgrade the chip."));
        Wait(4000);

        // Rule: the machine reaches threshold when the best chip is installed clean.
        // Brisbane and torino will never clear 2% (8.5% and 4.6% measured), they are the story of
        // getting there. Fez lands at 1.7%, so only truck damage can make it fail; then repeat fez.
        if (lastChip && clean) { GoTo(Phase.Threshold); return; }
        if (!lastChip) chipIndex++;
        GoTo(Phase.Heist);
    }

    void EndCalibrating() { truck?.MarkAsNoLongerNeeded(); truck = null; }

    // ---- Threshold: the Q-Day job runs on IBM hardware, the player waits --------------
    void BeginThreshold()
    {
        Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, false, 0);
        Function.Call(Hash.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.3f);
        // The storm used to start HERE, then run for the whole IBM queue (up to 900s)
        // with the player frozen in place. A thunderstorm clears the streets, so by
        // the time the riot began there was nobody left to arm. It now starts in
        // TickQDay, once peds have actually been armed.
        atlas.QDay("qday");
        Hud.BigMessage("THRESHOLD", "factoring RSA-2048 on IBM hardware...");
        Log.Write("threshold: qpu job requested");
    }

    void TickThreshold()
    {
        // The countdown is the real hardware queue. Nothing to fake here.
        if (Arrived("qday") is { } r) StartQDay(r);
        else if (Elapsed() > 900) { Log.Write("qpu timeout, using recorded"); StartQDay(AtlasResult.Recorded()); }
    }

    void EndThreshold() => Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);

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
        if (r.Ints is { Count: > 0 }) rng = new AtlasRandom(r.Ints);
        Function.Call(Hash.ANIMPOSTFX_PLAY, "DeathFailMPIn", 1500, false);
        Function.Call(Hash.SET_RIOT_MODE_ENABLED, true);
        // Deliberately NOT starting the storm here either -- it waits until the first
        // arming pass has found people. Keeping the streets populated long enough to
        // arm them is what makes the riot visible at all.
        Function.Call(Hash.SET_CREATE_RANDOM_COPS, false);
        stormStarted = false;
        armedTotal = 0;
        BuildTicker(r);
        Hud.BigMessage("Q-DAY", $"Bell S = {r.S:0.00} > 2    every key on earth is public");
        Log.Write($"Q-DAY. S={r.S} commit={r.Commit} backend={r.Backend} certified={r.Certified}");
    }

    DateTime lastArm = DateTime.MinValue;
    bool stormStarted;          // the storm is held back until people are armed
    int armedTotal;             // how many peds the riot has actually reached
    // 60m was too tight: it only covered what was almost on top of the player, and
    // the arming pass regularly found nobody at all.
    const float ArmRadius = 150f;

    void TickQDay()
    {
        // Money does not exist any more.
        Game.Player.Money = 0;
        Function.Call(Hash.DISPLAY_CASH, false);
        Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, 3);

        // Everyone goes mental: arm and enrage nearby peds every two seconds, chosen by Atlas randomness.
        if ((DateTime.UtcNow - lastArm).TotalSeconds > 2)
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
                if (p == me || !p.IsHuman || p.IsDead) continue;
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
        Hud.Ticker(ticker);
        if (Elapsed() > 300) GoTo(Phase.Ending);
    }

    void EndQDay() => Function.Call(Hash.SET_RIOT_MODE_ENABLED, false);

    void BuildTicker(AtlasResult r)
    {
        ticker.Clear();
        Hud.ResetTicker();
        List<string> lines = [.. Chaos.Leaks];
        // shuffle with Atlas bytes
        for (int i = lines.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (lines[i], lines[j]) = (lines[j], lines[i]); }
        // A live Atlas result can come back without a commit hash or backend (the
        // recorded fallback always has both). The null-conditionals used to collapse
        // to nothing, leaving the headline reading "KEY ... FACTORED ON " with gaps.
        var commit = string.IsNullOrEmpty(r.Commit)
            ? "UNVERIFIED"
            : r.Commit.Substring(0, Math.Min(16, r.Commit.Length)) + "...";
        var backend = string.IsNullOrEmpty(r.Backend) ? "IBM HARDWARE" : r.Backend.ToUpper();
        ticker.Add($"KEY {commit} FACTORED ON {backend}");
        ticker.AddRange(lines);
    }

    // ---- Ending: fade out, credit the hardware, back to Idle -------------------------
    void BeginEnding()
    {
        Screen.FadeOut(3000);
        Log.Write("ending");
    }

    void TickEnding()
    {
        if (Elapsed() > 3.5 && Elapsed() < 4)
        {
            var r = lastResult;
            Hud.BigMessage("POST-QUANTUM CRYPTOGRAPHY", "was available the whole time");
            Notification.Show($"Randomness certified on {r?.Backend ?? "IBM hardware"} via Moth Atlas. Job {r?.JobId}.");
        }
        if (Elapsed() > 12) { Screen.FadeIn(2000); GoTo(Phase.Idle); }
    }
}

// Deterministic RNG seeded from Atlas bytes, so the chaos is literally quantum-chosen.
public class AtlasRandom : Random
{
    readonly List<int> bytes; int pos;
    public AtlasRandom(List<int> b) : base(0) { bytes = b; }
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
    public static void Draw(Phase phase, int chip, int logical, double err, double threshold, AtlasResult last)
    {
        var errText = double.IsNaN(err) ? "--" : (err * 100).ToString("0.00") + "%";
        var color = double.IsNaN(err) ? Color.White : (err <= threshold ? Color.LimeGreen : Color.OrangeRed);
        new ContainerElement(new PointF(940, 140), new SizeF(320, 92), Color.FromArgb(160, 0, 0, 0)).Draw();
        new TextElement("Q-DAY  " + phase.ToString().ToUpper(), new PointF(950, 144), 0.42f, Color.White, Font.ChaletLondon, Alignment.Left).Draw();
        new TextElement($"logical qubits  {logical} / 3", new PointF(950, 168), 0.38f, Color.White, Font.ChaletLondon, Alignment.Left).Draw();
        new TextElement($"error rate  {errText}   threshold {threshold * 100:0}%", new PointF(950, 190), 0.38f, color, Font.ChaletLondon, Alignment.Left).Draw();
        if (phase == Phase.Calibrating || phase == Phase.Threshold)
            new TextElement(phase == Phase.Threshold ? "IBM hardware queue..." : "calibrating on Atlas...", new PointF(950, 212), 0.34f, Color.LightBlue, Font.ChaletLondon, Alignment.Left).Draw();
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
    const float TickerBarH  = 18f;     // thin enough to sit clear of the minimap
    const float TickerScale = 0.28f;   // matched to the bar height
    const float TickerSpeed = 150f;    // canvas pixels per SECOND
    const float TickerGap   = 60f;     // space between messages
    const float NoWrap      = 6000f;   // effectively disables GTA's text wrapping
    const string TickerSep  = "  •  ";
    static readonly Color TickerBarCol  = Color.FromArgb(205, 100, 0, 0);
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

    public static void BigMessage(string title, string sub)
    {
        Screen.ShowSubtitle($"~b~{title}~s~  {sub}", 6000);
        Notification.Show($"~b~{title}~s~ {sub}");
    }
}

public class Locations
{
    public class Chip { public string Key, Label, Line; public Vector3 Pos; public int Wanted; }
    public class Site { public string Name; public Vector3 Pos; public Vector3 TruckSpawn; }
    public Site Start, Lab; public List<Chip> Chips; public Site Ending;

    // Coordinates from community lists, verify each in-game. z is ground height at that x,y.
    public static readonly Locations Default = new()
    {
        // Where the player goes, on foot, to start the mission. Same spot as the lab for now.
        Start = new()
        {
          Name = "Some payphone (mission start)",
          Pos = new(395.24f, -798.65f, 28.78f)
        },
        Lab = new()
        {
            Name = "Lester's house (the machine is built here)",
            Pos = new(1248.18f, -1728.10f, 56.0f),
            TruckSpawn = new(1264.0f, -1730.0f, 55.5f),
        },
        Chips = [
            new() { Key = "brisbane", Label = "Humane Labs cryostat",       Pos = new(3617.23f, 3739.87f, 28.69f), Wanted = 3, Line = "Biotech cryostat. Noisy as hell, but it is cold." },
            new() { Key = "torino",   Label = "FIB Building control plane", Pos = new(135.52f, -749.0f, 45.75f),   Wanted = 4, Line = "Government-grade control electronics. They will notice." },
            new() { Key = "fez",      Label = "Fort Zancudo qubit array",   Pos = new(-2047.0f, 3132.0f, 32.8f),   Wanted = 5, Line = "The real thing. Military. Do not scratch it." },
        ],
        Ending = new()
        {
          Name = "Vinewood sign",
          Pos = new(776.88f, 1175.61f, 345.96f)
        },
    };
}
