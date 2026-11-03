// Q-DAY: a GTA V mission where the player builds a fault-tolerant quantum computer
// out of stolen hardware, it factors every key on earth, and San Andreas loses its mind.
// Every number on the HUD comes from Moth Atlas via the sidecar (see ../sidecar).
//
// Build: dotnet build   ->   bin/QDay.dll   ->   GTAV/scripts/QDay.dll
// In game: F9 starts the mission, Insert reloads SHVDN scripts.
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

namespace QDay
{
    public enum Phase { Idle, Pitch, Heist, Delivery, Calibrating, Threshold, QDay, Ending }

    public class QDayScript : Script
    {
        // ---- state ---------------------------------------------------------------
        Phase phase = Phase.Idle;
        int chipIndex = 0;                 // which of the three chips we are on
        int logicalQubits = 0;             // deliveries that came in under threshold
        double errorRate = double.NaN;     // live figure from Atlas, NaN = none yet
        double damagePenalty = 0;          // truck damage since pickup, added to errorRate
        const double Threshold = 0.02;     // fez/marrakesh land at ~1.7%, brisbane at 8.5%
        Vehicle truck;
        float truckHealthAtPickup;
        Blip blip;
        DateTime phaseStarted = DateTime.UtcNow;
        AtlasResult lastResult;            // latest sidecar result, written by the bridge thread
        Random rng = new Random();         // replaced by Atlas bytes on Q-Day
        readonly List<string> ticker = new List<string>();
        int tickerIndex = 0; float tickerX = 1.0f;
        readonly Locations loc;
        readonly Bridge bridge;

        public QDayScript()
        {
            Log.Init();
            loc = Locations.Load();
            bridge = new Bridge(Log.Write);
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Interval = 0;
            Log.Write("Q-DAY loaded. F9 to start.");
            Notification.Show("~b~Q-DAY~s~ loaded. F9 start, F10 abort, F5 teleport, F6 skip wait, F7 Q-Day now.");
        }

        // ---- input ---------------------------------------------------------------
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F9 && phase == Phase.Idle) StartMission();
            if (e.KeyCode == Keys.F10) { Log.Write("abort"); Reset(); }
            // Playtest shortcuts. F5 teleports to the current objective, F6 fakes the pending Atlas
            // result from the recorded run, F7 jumps straight to Q-Day. All logged so a recording
            // that used them is obvious.
            if (e.KeyCode == Keys.F5 && phase != Phase.Idle) DebugTeleport();
            if (e.KeyCode == Keys.F6 && phase == Phase.Calibrating) { Log.Write("DEBUG F6 recorded calibration"); errorRate = chipIndex == loc.Chips.Count - 1 ? 0.0173 : 0.085; AfterCalibration(); }
            if (e.KeyCode == Keys.F6 && phase == Phase.Threshold) { Log.Write("DEBUG F6 recorded qday"); BeginQDay(AtlasResult.Recorded()); }
            if (e.KeyCode == Keys.F8) { var pl = Game.Player.Character; pl.Position = new Vector3(195.0f, -933.0f, 30.7f); Log.Write("DEBUG F8 teleport to Legion Square"); }
            if (e.KeyCode == Keys.F7) { Log.Write("DEBUG F7 jump to Q-Day"); if (phase == Phase.Idle) StartMission(); lastResult = AtlasResult.Recorded(); BeginQDay(lastResult); }
        }

        void DebugTeleport()
        {
            var ped = Game.Player.Character;
            Vector3 target;
            if (phase == Phase.Heist) target = loc.Chips[chipIndex].Pos;
            else if (phase == Phase.Delivery) target = loc.Lab.Pos;
            else target = loc.Lab.Pos;
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

        // ---- main loop -----------------------------------------------------------
        void OnTick(object sender, EventArgs e)
        {
            var r = bridge.Take();
            if (r != null) { lastResult = r; OnAtlasResult(r); }

            switch (phase)
            {
                case Phase.Pitch: TickPitch(); break;
                case Phase.Heist: TickHeist(); break;
                case Phase.Delivery: TickDelivery(); break;
                case Phase.Calibrating: TickCalibrating(); break;
                case Phase.Threshold: TickThreshold(); break;
                case Phase.QDay: TickQDay(); break;
                case Phase.Ending: TickEnding(); break;
            }
            if (phase != Phase.Idle) Hud.Draw(phase, chipIndex, logicalQubits, DisplayedError(), Threshold, lastResult);
        }

        double DisplayedError() => double.IsNaN(errorRate) ? double.NaN : errorRate + damagePenalty;

        // ---- phases --------------------------------------------------------------
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

        void StartMission()
        {
            StopStoryMissions();
            Log.Write("mission start");
            SetPhase(Phase.Pitch);
            Screen.ShowSubtitle("~b~Lester:~s~ A machine that only factors numbers. That's all it does.", 6000);
            Hud.BigMessage("Q-DAY", "build a fault-tolerant quantum computer");
        }

        void TickPitch()
        {
            if (Elapsed() > 7) BeginHeist();
        }

        void BeginHeist()
        {
            var chip = loc.Chips[chipIndex];
            SetPhase(Phase.Heist);
            blip = World.CreateBlip(chip.Pos);
            blip.Sprite = BlipSprite.Standard; blip.Color = BlipColor.Blue; blip.ShowRoute = true;
            Screen.ShowSubtitle($"~b~Lester:~s~ {chip.Line}", 6000);
            Log.Write($"heist {chipIndex} -> {chip.Label} at {chip.Pos}");
        }

        void TickHeist()
        {
            var chip = loc.Chips[chipIndex];
            var ped = Game.Player.Character;
            if (ped.Position.DistanceTo(chip.Pos) < 6f)
            {
                // Pick up: the crate is the truck. Spawn it, make the player wanted, send them home.
                truck = World.CreateVehicle(VehicleHash.Mule, ped.Position + ped.ForwardVector * 5f, ped.Heading);
                if (truck == null) { Log.Write("truck spawn failed"); return; }
                truck.PlaceOnGround();
                truckHealthAtPickup = truck.BodyHealth;
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, chip.Wanted, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                blip?.Delete();
                blip = World.CreateBlip(loc.Lab.Pos); blip.Color = BlipColor.Green; blip.ShowRoute = true;
                damagePenalty = 0;
                SetPhase(Phase.Delivery);
                Screen.ShowSubtitle($"~b~Lester:~s~ Got it. Bring it to me. ~r~Every bump is decoherence.~s~", 6000);
                Log.Write($"pickup {chip.Key}, wanted {chip.Wanted}");
            }
        }

        void TickDelivery()
        {
            var ped = Game.Player.Character;
            if (truck != null && truck.Exists())
            {
                // Body damage since pickup -> up to +5% on the error meter. Honest and visible.
                var lost = Math.Max(0, truckHealthAtPickup - truck.BodyHealth);
                damagePenalty = Math.Min(0.05, lost / 1000.0 * 0.05);
            }
            bool inTruck = truck != null && truck.Exists() && ped.IsInVehicle(truck);
            if (inTruck && ped.Position.DistanceTo(loc.Lab.Pos) < 12f)
            {
                var chip = loc.Chips[chipIndex];
                Game.Player.WantedLevel = 0;
                blip?.Delete();
                SetPhase(Phase.Calibrating);
                bridge.Request("cal" + chipIndex, new Dictionary<string, object> { ["kind"] = "calibrate", ["chip"] = chip.Key });
                Screen.ShowSubtitle("~b~Lester:~s~ Installing. Calibrating on the real noise model...", 5000);
                Log.Write($"delivered {chip.Key}, penalty {damagePenalty:P2}, calibrating");
            }
        }

        void TickCalibrating()
        {
            // Waiting for the sidecar. Result handled in OnAtlasResult. Timeout keeps the demo alive.
            if (Elapsed() > 180) { Log.Write("calibration timeout"); errorRate = 0.1; AfterCalibration(); }
        }

        void OnAtlasResult(AtlasResult r)
        {
            Log.Write($"atlas result {r.Name}: {r.Raw}");
            if (r.Status != "done") { if (r.Status == "error") Log.Write("atlas error: " + r.Message); return; }
            if (r.Kind == "calibrate" && phase == Phase.Calibrating)
            {
                errorRate = r.ErrorRate;
                AfterCalibration();
            }
            else if (r.Kind == "qday" && phase == Phase.Threshold)
            {
                if (r.Ints != null && r.Ints.Count > 0) rng = new AtlasRandom(r.Ints);
                BeginQDay(r);
            }
        }

        void AfterCalibration()
        {
            var total = DisplayedError();
            bool clean = total <= Threshold;
            bool lastChip = chipIndex == loc.Chips.Count - 1;
            Log.Write($"calibrated chip {chipIndex}: live {errorRate:P2} + damage {damagePenalty:P2} = {total:P2} -> {(clean ? "LOGICAL QUBIT" : "too noisy")}");
            if (clean) { logicalQubits++; Hud.BigMessage("LOGICAL QUBIT", $"{logicalQubits} / 3  error {total:P1}"); }
            else Hud.BigMessage("TOO NOISY", $"error {total:P1} > threshold {Threshold:P0}. " + (lastChip ? "bring another one, carefully." : "upgrade the chip."));
            truck?.MarkAsNoLongerNeeded(); truck = null;

            // Rule: the machine reaches threshold when the best chip is installed clean.
            // Brisbane and torino will never clear 2% (8.5% and 4.6% measured), they are the story of
            // getting there. Fez lands at 1.7%, so only truck damage can make it fail; then repeat fez.
            if (lastChip && clean) { Wait(4000); BeginThreshold(); return; }
            if (!lastChip) chipIndex++;
            Wait(4000);
            BeginHeist();
        }

        void BeginThreshold()
        {
            SetPhase(Phase.Threshold);
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, false, 0);
            Function.Call(Hash.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.3f);
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "THUNDER");
            bridge.Request("qday", new Dictionary<string, object> { ["kind"] = "qday" });
            Hud.BigMessage("THRESHOLD", "factoring RSA-2048 on IBM hardware...");
            Log.Write("threshold: qpu job requested");
        }

        void TickThreshold()
        {
            // The countdown is the real hardware queue. Nothing to fake here.
            if (Elapsed() > 900) { Log.Write("qpu timeout, using recorded"); BeginQDay(AtlasResult.Recorded()); }
        }

        void BeginQDay(AtlasResult r)
        {
            SetPhase(Phase.QDay);
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
            Function.Call(Hash.ANIMPOSTFX_PLAY, "DeathFailMPIn", 1500, false);
            Function.Call(Hash.SET_RIOT_MODE_ENABLED, true);
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, "THUNDER");
            Function.Call(Hash.SET_CREATE_RANDOM_COPS, false);
            BuildTicker(r);
            Hud.BigMessage("Q-DAY", $"Bell S = {r.S:0.00} > 2    every key on earth is public");
            Log.Write($"Q-DAY. S={r.S} commit={r.Commit} backend={r.Backend} certified={r.Certified}");
        }

        DateTime lastArm = DateTime.MinValue;
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
                var me = Game.Player.Character;
                foreach (var p in World.GetNearbyPeds(me, 60f))
                {
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
                    }
                }
                Chaos.EnsureRelationships();
            }
            Hud.Ticker(ticker, ref tickerIndex, ref tickerX);
            if (Elapsed() > 300) BeginEnding();
        }

        void BuildTicker(AtlasResult r)
        {
            ticker.Clear();
            var lines = new List<string>(Chaos.Leaks);
            // shuffle with Atlas bytes
            for (int i = lines.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = lines[i]; lines[i] = lines[j]; lines[j] = t; }
            ticker.Add($"KEY {r.Commit?.Substring(0, Math.Min(16, (r.Commit ?? "").Length))}... FACTORED ON {r.Backend?.ToUpper()}");
            ticker.AddRange(lines);
        }

        void BeginEnding()
        {
            SetPhase(Phase.Ending);
            Function.Call(Hash.SET_RIOT_MODE_ENABLED, false);
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
            if (Elapsed() > 12) { Screen.FadeIn(2000); Reset(); }
        }

        // ---- helpers -------------------------------------------------------------
        void SetPhase(Phase p) { phase = p; phaseStarted = DateTime.UtcNow; Log.Write("phase -> " + p); }
        double Elapsed() => (DateTime.UtcNow - phaseStarted).TotalSeconds;

        void Reset()
        {
            Function.Call(Hash.SET_RIOT_MODE_ENABLED, false);
            Function.Call(Hash.SET_CREATE_RANDOM_COPS, true);
            Function.Call(Hash.DISPLAY_CASH, true);
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
            Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
            blip?.Delete(); blip = null;
            truck?.MarkAsNoLongerNeeded(); truck = null;
            chipIndex = 0; logicalQubits = 0; errorRate = double.NaN; damagePenalty = 0; lastResult = null;
            phase = Phase.Idle;
        }
    }

    // ---- the Atlas bridge: a folder of JSON files shared with the Python sidecar --------
    public class AtlasResult
    {
        public string Name, Status, Kind, Message, Raw, Commit, Backend, JobId, Grade;
        public double ErrorRate = double.NaN, S = double.NaN;
        public bool Certified;
        public List<int> Ints;

        public static AtlasResult Parse(string name, string json)
        {
            var r = new AtlasResult { Name = name, Raw = json };
            r.Status = Json.Str(json, "status"); r.Kind = Json.Str(json, "kind"); r.Message = Json.Str(json, "message");
            r.Commit = Json.Str(json, "commit"); r.Backend = Json.Str(json, "backend"); r.JobId = Json.Str(json, "job_id"); r.Grade = Json.Str(json, "grade");
            r.ErrorRate = Json.Num(json, "error_rate"); r.S = Json.Num(json, "S"); r.Certified = Json.Str(json, "certified") == "true";
            r.Ints = Json.IntList(json, "ints");
            if (r.Kind == null) r.Kind = name.StartsWith("cal") ? "calibrate" : name;
            return r;
        }

        public static AtlasResult Recorded() => new AtlasResult { Name = "qday", Status = "done", Kind = "qday", S = 2.711, Commit = "40bb90660f941c13f6ccc0d1998adfce33daa061d50459b0719ad999c93352f2", Backend = "ibm_marrakesh", JobId = "6eb8fa80-d13f-4f28-8b1b-f8fb7057f16b", Certified = true, Grade = "hardware-accounted" };
    }

    public class Bridge
    {
        readonly string dir;
        readonly Action<string> log;
        readonly Queue<AtlasResult> inbox = new Queue<AtlasResult>();
        readonly HashSet<string> seen = new HashSet<string>();
        readonly object gate = new object();

        public Bridge(Action<string> log)
        {
            this.log = log;
            dir = Environment.GetEnvironmentVariable("QDAY_BRIDGE");
            if (string.IsNullOrEmpty(dir)) dir = Directory.Exists(@"C:\gta-mod\bridge") ? @"C:\gta-mod\bridge" : Path.Combine(Log.ScriptsDir, "qday_bridge");
            Directory.CreateDirectory(dir);
            var t = new Thread(Poll) { IsBackground = true }; t.Start();
            log("bridge dir " + dir);
        }

        public void Request(string name, Dictionary<string, object> body)
        {
            var path = Path.Combine(dir, "request_" + name + ".json");
            var res = Path.Combine(dir, "result_" + name + ".json");
            if (File.Exists(res)) File.Delete(res);
            lock (gate) seen.Remove(name);
            File.WriteAllText(path + ".tmp", Json.Obj(body)); File.Move(path + ".tmp", path);
            log("request " + name + " " + Json.Obj(body));
        }

        public AtlasResult Take() { lock (gate) return inbox.Count > 0 ? inbox.Dequeue() : null; }

        void Poll()
        {
            while (true)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "result_*.json"))
                    {
                        var name = Path.GetFileNameWithoutExtension(f).Substring("result_".Length);
                        var json = File.ReadAllText(f);
                        var status = Json.Str(json, "status");
                        if (status != "done" && status != "error") continue;
                        lock (gate)
                        {
                            if (seen.Contains(name)) continue;
                            seen.Add(name);
                            inbox.Enqueue(AtlasResult.Parse(name, json));
                        }
                    }
                }
                catch (Exception e) { log("bridge poll error: " + e.Message); }
                Thread.Sleep(500);
            }
        }
    }

    // Minimal JSON helpers so the DLL has no dependencies beyond SHVDN.
    public static class Json
    {
        public static string Str(string json, string key)
        {
            var k = "\"" + key + "\":"; var i = json.IndexOf(k, StringComparison.Ordinal); if (i < 0) return null;
            i += k.Length; while (i < json.Length && json[i] == ' ') i++;
            if (json[i] == '"') { var j = json.IndexOf('"', i + 1); return json.Substring(i + 1, j - i - 1); }
            var e = i; while (e < json.Length && ",}]".IndexOf(json[e]) < 0) e++;
            return json.Substring(i, e - i).Trim();
        }
        public static double Num(string json, string key)
        {
            var s = Str(json, key); double d;
            return s != null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }
        public static List<int> IntList(string json, string key)
        {
            var k = "\"" + key + "\":"; var i = json.IndexOf(k, StringComparison.Ordinal); if (i < 0) return null;
            var a = json.IndexOf('[', i); var b = json.IndexOf(']', a); if (a < 0 || b < 0) return null;
            var list = new List<int>();
            foreach (var p in json.Substring(a + 1, b - a - 1).Split(',')) { int v; if (int.TryParse(p.Trim(), out v)) list.Add(v); }
            return list;
        }
        public static string Obj(Dictionary<string, object> d)
        {
            var parts = new List<string>();
            foreach (var kv in d) parts.Add("\"" + kv.Key + "\": " + (kv.Value is string ? "\"" + kv.Value + "\"" : kv.Value.ToString()));
            return "{" + string.Join(", ", parts) + "}";
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

        public static void Ticker(List<string> lines, ref int index, ref float x)
        {
            if (lines.Count == 0) return;
            // SHVDN UI elements use a fixed 1280x720 canvas, not the real resolution.
            const float w = 1280f, h = 720f;
            new ContainerElement(new PointF(0, h - 48), new SizeF(w, 40), Color.FromArgb(200, 120, 0, 0)).Draw();
            new TextElement(lines[index % lines.Count], new PointF(x * w, h - 44), 0.55f, Color.White, Font.Monospace, Alignment.Left).Draw();
            x -= 0.004f;
            if (x < -0.6f) { x = 1.0f; index++; }
        }

        public static void BigMessage(string title, string sub)
        {
            Screen.ShowSubtitle($"~b~{title}~s~  {sub}", 6000);
            Notification.Show($"~b~{title}~s~ {sub}");
        }
    }

    public static class Log
    {
        public static string ScriptsDir;
        static string path;
        static bool warned;

        /// <summary>
        /// Work out where scripts\ actually is.
        ///
        /// This used to be `Path.GetDirectoryName(typeof(Log).Assembly.Location)`.
        /// **Assembly.Location is the wrong question under SHVDN**, which shadow-copies
        /// script assemblies: for QDay.dll it returns something like
        ///   %LOCALAPPDATA%\assembly\dl3\XN811RXO.2XM\...\QDay.dll
        /// so the log was written into a .NET shadow-copy cache folder nobody would ever
        /// look in. Measured on the test box: nine stale QDay.log files in there, and
        /// none at all in scripts\. Write() swallowed every exception, so nothing said so.
        ///
        /// It is not only the log. ScriptsDir also resolves QDay.locations.json, which is
        /// NOT in the shadow-copy cache -- so the lookup always missed and the mod fell
        /// back to the hardcoded Defaults, silently ignoring every edit to locations.json.
        /// That is confirmed in the recovered log:
        ///   "locations: NO file found next to the script, using hardcoded Defaults"
        ///
        /// So: ask the AppDomain, not the assembly. SHVDN runs scripts in its own
        /// ScriptDomain whose BaseDirectory IS scripts\ (measured:
        /// "C:\...\Grand Theft Auto V\scripts", FriendlyName "SHVDN_ScriptDomain_...").
        /// The candidates below cover that, plus the layouts other SHVDN versions might
        /// use, and prefer whichever actually holds the locations file.
        /// </summary>
        static string ResolveScriptsDir(out string how)
        {
            var candidates = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (label, dir) =>
            {
                if (!string.IsNullOrEmpty(dir)) candidates.Add(new KeyValuePair<string, string>(label, dir));
            };

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;   // SHVDN: this IS scripts\
                add("AppDomain.BaseDirectory", baseDir);
                if (!string.IsNullOrEmpty(baseDir)) add("AppDomain.BaseDirectory + scripts", Path.Combine(baseDir, "scripts"));
            }
            catch { }

            try
            {
                var cwd = Directory.GetCurrentDirectory();             // the GTA V folder
                if (!string.IsNullOrEmpty(cwd)) add("CurrentDirectory + scripts", Path.Combine(cwd, "scripts"));
                add("CurrentDirectory", cwd);
            }
            catch { }

            // First choice: a candidate that actually contains the locations file, since
            // that is the thing whose absence was silently changing the mission.
            foreach (var c in candidates)
            {
                try
                {
                    if (Directory.Exists(c.Value) &&
                        (File.Exists(Path.Combine(c.Value, "QDay.locations.json")) ||
                         File.Exists(Path.Combine(c.Value, "locations.json"))))
                    { how = c.Key + " (has locations file)"; return c.Value; }
                }
                catch { }
            }

            // Otherwise the first one that exists, so the log still lands somewhere sane.
            foreach (var c in candidates)
            {
                try { if (Directory.Exists(c.Value)) { how = c.Key; return c.Value; } }
                catch { }
            }

            how = "temp (no scripts folder found)";
            return Path.GetTempPath();
        }

        public static void Init()
        {
            string how;
            ScriptsDir = ResolveScriptsDir(out how);
            path = Path.Combine(ScriptsDir, "QDay.log");
            // Record how the folder was resolved, and what Assembly.Location actually
            // said, so this is diagnosable from the log itself next time.
            string loc;
            try { loc = typeof(Log).Assembly.Location; } catch (Exception e) { loc = "<threw: " + e.Message + ">"; }
            if (string.IsNullOrEmpty(loc)) loc = "<empty>";
            TryWrite($"\n==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n"
                   + $"scripts dir: {ScriptsDir}  (resolved via {how}; Assembly.Location = {loc})\n");
        }

        public static void Write(string s) { TryWrite($"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }

        /// <summary>
        /// Append, and if it fails say so once in game rather than swallowing it. A mod
        /// whose log silently does not exist costs far more time than a one-off warning.
        /// </summary>
        static void TryWrite(string text)
        {
            try
            {
                File.AppendAllText(path, text);
            }
            catch (Exception e)
            {
                if (warned) return;
                warned = true;
                try { Notification.Show($"~r~Q-DAY:~s~ cannot write log to {path} -- {e.Message}"); } catch { }
            }
        }
    }

    public class Locations
    {
        public class Chip { public string Key, Label, Line; public Vector3 Pos; public int Wanted; }
        public class Site { public Vector3 Pos; public Vector3 TruckSpawn; }
        public Site Lab; public List<Chip> Chips = new List<Chip>(); public Vector3 Ending;

        public static Locations Load()
        {
            var l = new Locations();
            var p = Path.Combine(Log.ScriptsDir, "QDay.locations.json");
            if (!File.Exists(p)) p = Path.Combine(Log.ScriptsDir, "locations.json");
            bool fromFile = File.Exists(p);
            var json = fromFile ? File.ReadAllText(p) : Defaults;
            // Say which source won. Falling back to Defaults silently meant edits to
            // locations.json had no effect and nothing reported it.
            Log.Write(fromFile
                ? "locations loaded from " + p
                : "locations: NO file found next to the script, using hardcoded Defaults (looked in " + Log.ScriptsDir + ")");
            // tiny hand parser: we control the file shape
            l.Lab = new Site { Pos = Vec(json, "\"lab\"", "\"pos\""), TruckSpawn = Vec(json, "\"lab\"", "\"truck_spawn\"") };
            l.Ending = Vec(json, "\"ending\"", "\"pos\"");
            int i = json.IndexOf("\"chips\"", StringComparison.Ordinal);
            while (true)
            {
                int k = json.IndexOf("\"key\"", i + 1, StringComparison.Ordinal); if (k < 0) break;
                int end = json.IndexOf('}', k);
                var chunk = json.Substring(k, end - k);
                l.Chips.Add(new Chip { Key = Json.Str(chunk, "key"), Label = Json.Str(chunk, "label"), Line = Json.Str(chunk, "line"), Wanted = (int)Json.Num(chunk, "wanted"), Pos = Vec(chunk, "", "\"pos\"") });
                i = end;
            }
            Log.Write($"locations: {l.Chips.Count} chips, lab {l.Lab.Pos}");
            return l;
        }

        static Vector3 Vec(string json, string section, string key)
        {
            int s = section.Length > 0 ? json.IndexOf(section, StringComparison.Ordinal) : 0; if (s < 0) s = 0;
            int k = json.IndexOf(key, s, StringComparison.Ordinal); if (k < 0) return Vector3.Zero;
            int a = json.IndexOf('[', k), b = json.IndexOf(']', a);
            var parts = json.Substring(a + 1, b - a - 1).Split(',');
            return new Vector3(F(parts[0]), F(parts[1]), F(parts[2]));
        }
        static float F(string s) => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture);

        const string Defaults = "{\"lab\":{\"pos\":[1248.18,-1728.10,56.0],\"truck_spawn\":[1264.0,-1730.0,55.5]},\"chips\":[{\"key\":\"brisbane\",\"label\":\"Humane Labs cryostat\",\"pos\":[3617.23,3739.87,28.69],\"wanted\":3,\"line\":\"Noisy, but cold.\"},{\"key\":\"torino\",\"label\":\"FIB control plane\",\"pos\":[135.52,-749.0,45.75],\"wanted\":4,\"line\":\"They will notice.\"},{\"key\":\"fez\",\"label\":\"Zancudo qubit array\",\"pos\":[-2047.0,3132.0,32.8],\"wanted\":5,\"line\":\"Do not scratch it.\"}],\"ending\":{\"pos\":[776.88,1175.61,345.96]}}";
    }
}
