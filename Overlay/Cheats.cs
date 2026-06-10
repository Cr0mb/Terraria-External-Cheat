using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TerrariaTrainer
{
    public sealed class Cheats
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;
        const float FlySpeed = 4.5f;

        readonly GameMemory _mem = new GameMemory();      // cheat loop thread
        readonly GameMemory _espMem = new GameMemory();   // render thread (separate buffers, no race)
        readonly GameMemory _moveMem = new GameMemory();  // movement tight-loop thread
        Offsets _o;
        Thread _thread, _moveThread;
        volatile bool _running;

        public string Status = "Not attached.";
        public bool Attached { get; private set; }
        public string LastSpawn = "";

        // Toggles
        public volatile bool GodMode, InfHealth, InfMana, InfFlight, InfBreath, NoKnockback, InstantRespawn, FreezeTime, Fly, RapidAttack;
        public volatile bool GravityFlip, FreezeEnemies, WeakenEnemies, KillAura, ItemVacuum;
        public volatile bool DamageBoost, GhostHit;
        public volatile bool SuperSpeed, HighJump, NoFallDamage, LavaImmune, WaterWalk, ExtendedReach, FreeCast, InfItems, InstantBreak;
        // Instant-break restore state (held tool's original pick/axe/hammer power).
        uint _brkPtr; int _brkType = -1, _brkPick, _brkAxe, _brkHammer;
        public volatile float AuraRange = 30f;   // tiles
        public volatile float DamageMult = 5f;   // damage multiplier
        public volatile float SpeedMult = 2.5f;  // run-speed multiplier (x default 3.0)
        volatile int _reqBuffId = -1, _reqBuffDur;
        public void ApplyBuff(int id, int durSeconds) { _reqBuffDur = durSeconds * 60; _reqBuffId = id; }
        int[] _invStacks;                        // last-seen inventory stack counts (InfItems)
        volatile bool _reqGather, _reqItemTp;
        uint _raItemPtr; int _raType = -1, _raUseTime, _raUseAnim; bool _raAuto; // Rapid Attack restore state
        public void RequestGather() => _reqGather = true;
        public void RequestItemTeleport() => _reqItemTp = true;

        // Damage-boost state: track the held item we've modified so we can restore its base
        // damage on weapon switch / toggle-off and never compound the multiplier.
        uint _dmgItemPtr; int _dmgItemBase = -1; int _dmgItemType = -1;

        // One-shot requests
        volatile bool _reqMaxHp, _reqMaxMana, _reqFullHeal, _reqNoon, _reqMidnight, _reqTpSpawn, _reqKillAll;
        volatile bool _reqRain, _reqClear, _reqBloodMoon, _reqEclipse;
        volatile int _reqSetTime = -1; volatile bool _reqSetTimeDay;
        public int SpawnType = 9, SpawnStack = 99, SpawnSlot = 0;

        public void RequestMaxHp() => _reqMaxHp = true;
        public void RequestMaxMana() => _reqMaxMana = true;
        public void RequestFullHeal() => _reqFullHeal = true;
        public void RequestNoon() => _reqNoon = true;
        public void RequestMidnight() => _reqMidnight = true;
        public void RequestTpSpawn() => _reqTpSpawn = true;
        public void RequestKillAll() => _reqKillAll = true;
        public void RequestRain() => _reqRain = true;
        public void RequestClearWeather() => _reqClear = true;
        public void RequestBloodMoon() => _reqBloodMoon = true;
        public void RequestEclipse() => _reqEclipse = true;
        public void RequestSetTime(int ticks, bool day) { _reqSetTimeDay = day; _reqSetTime = ticks; }

        // Item spawn runs through the x86 helper (ContentSamples template copy).
        public void RequestSpawn()
        {
            int t = SpawnType, s = SpawnStack, sl = SpawnSlot;
            Task.Run(() => { LastSpawn = OffsetClient.Spawn(t, s, sl); });
        }

        // ESP — boxes are read + projected on the RENDER thread (see GetEspBoxes) so they
        // match the exact frame being drawn (no cross-thread staleness / snapping).
        public struct EspBox { public float X, Y, W, H; public uint Col; public string Tag; }
        public volatile bool EspEnabled, EspNames = true, EspLines;
        public volatile bool EspLocal = true, EspOtherPlayers = true, EspTown = true, EspHostile = true, EspBossRainbow = true, EspItems = true, EspWaypoints = true;
        readonly System.Collections.Generic.List<EspBox> _espList = new System.Collections.Generic.List<EspBox>(64);
        // Static name tables (loaded once on attach). int id -> display name.
        readonly System.Collections.Generic.Dictionary<int, string> _npcNames = new System.Collections.Generic.Dictionary<int, string>();
        readonly System.Collections.Generic.Dictionary<int, string> _itemNames = new System.Collections.Generic.Dictionary<int, string>();
        public int NameCount => _npcNames.Count + _itemNames.Count;
        public void LoadNames() { try { OffsetClient.FetchNames(_npcNames, _itemNames); } catch { } }
        // Reusable buffers for batched reads (render thread only).
        byte[] _objBuf = new byte[0x220];
        byte[] _ptrBuf = new byte[256 * 4];
        static int BI32(byte[] b, uint o) => BitConverter.ToInt32(b, (int)o);
        static float BF32(byte[] b, uint o) => BitConverter.ToSingle(b, (int)o);
        static uint Col(byte r, byte g, byte b, byte a = 255) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
        static readonly uint RED = Col(255, 45, 45), CYAN = Col(60, 220, 255), GREEN = Col(60, 255, 120), GOLD = Col(255, 210, 60), ORANGE = Col(255, 150, 40), MAGENTA = Col(235, 70, 235);

        // HSV->packed RGBA for rainbow bosses (h in [0,1))
        static uint Rainbow(double h)
        {
            h = (h - Math.Floor(h)) * 6.0; int i = (int)h; double f = h - i;
            byte v = 255, p = 0, q = (byte)(255 * (1 - f)), t = (byte)(255 * f);
            switch (i) { case 0: return Col(v, t, p); case 1: return Col(q, v, p); case 2: return Col(p, v, t);
                         case 3: return Col(p, q, v); case 4: return Col(t, p, v); default: return Col(v, p, q); }
        }

        // Live snapshot
        public volatile bool InWorld;
        public volatile int Life, LifeMax, Mana, ManaMax;
        public float PosX, PosY;

        double _frozenTime; bool _frozenDay, _wasFreezing;

        // Waypoints (world pixel coords). Guarded by _wpLock for cross-thread access.
        public sealed class Waypoint { public string Name; public float X, Y; }
        readonly object _wpLock = new object();
        readonly System.Collections.Generic.List<Waypoint> _waypoints = new System.Collections.Generic.List<Waypoint>();
        volatile int _reqTpWp = -1;
        public Waypoint[] Waypoints { get { lock (_wpLock) return _waypoints.ToArray(); } }

        public void AddWaypoint(string name)
        {
            lock (_wpLock) _waypoints.Add(new Waypoint { Name = string.IsNullOrWhiteSpace(name) ? $"WP{_waypoints.Count + 1}" : name, X = PosX, Y = PosY });
        }
        public void RemoveWaypoint(int i) { lock (_wpLock) { if (i >= 0 && i < _waypoints.Count) _waypoints.RemoveAt(i); } }
        public void TeleportWaypoint(int i) => _reqTpWp = i;

        // ---- Config persistence (simple key=value text + waypoint lines) ----
        // Config lives in %LOCALAPPDATA%\GVoid so the single exe leaves nothing beside itself.
        public static string ConfigPath
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVoid");
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
                return System.IO.Path.Combine(dir, "gvoid_config.txt");
            }
        }

        public string SaveConfig()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in BoolMap()) sb.Append(kv.Key).Append('=').Append(kv.Value() ? 1 : 0).Append('\n');
                sb.Append("AuraRange=").Append(AuraRange.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("DamageMult=").Append(DamageMult.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                lock (_wpLock)
                    foreach (var w in _waypoints)
                        sb.Append("WP=").Append(w.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                          .Append(w.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(w.Name).Append('\n');
                System.IO.File.WriteAllText(ConfigPath, sb.ToString());
                return "Saved " + ConfigPath;
            }
            catch (Exception ex) { return "Save failed: " + ex.Message; }
        }

        public string LoadConfig()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigPath)) return "No config file yet.";
                var setters = BoolSetters();
                lock (_wpLock) _waypoints.Clear();
                foreach (var raw in System.IO.File.ReadAllLines(ConfigPath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq), val = raw.Substring(eq + 1);
                    if (key == "WP")
                    {
                        var parts = val.Split(new[] { ',' }, 3);
                        if (parts.Length == 3 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wx)
                            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wy))
                            lock (_wpLock) _waypoints.Add(new Waypoint { X = wx, Y = wy, Name = parts[2] });
                    }
                    else if (key == "AuraRange" && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ar)) AuraRange = ar;
                    else if (key == "DamageMult" && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dm)) DamageMult = dm;
                    else if (setters.TryGetValue(key, out var set)) set(val == "1");
                }
                return "Loaded " + ConfigPath;
            }
            catch (Exception ex) { return "Load failed: " + ex.Message; }
        }

        System.Collections.Generic.Dictionary<string, Func<bool>> BoolMap() => new System.Collections.Generic.Dictionary<string, Func<bool>>
        {
            {"GodMode",()=>GodMode},{"InfHealth",()=>InfHealth},{"InfMana",()=>InfMana},{"InfFlight",()=>InfFlight},
            {"InfBreath",()=>InfBreath},{"NoKnockback",()=>NoKnockback},{"InstantRespawn",()=>InstantRespawn},
            {"FreezeTime",()=>FreezeTime},{"Fly",()=>Fly},{"RapidAttack",()=>RapidAttack},{"GravityFlip",()=>GravityFlip},
            {"FreezeEnemies",()=>FreezeEnemies},{"WeakenEnemies",()=>WeakenEnemies},{"KillAura",()=>KillAura},
            {"ItemVacuum",()=>ItemVacuum},{"DamageBoost",()=>DamageBoost},{"GhostHit",()=>GhostHit},
            {"EspEnabled",()=>EspEnabled},{"EspNames",()=>EspNames},{"EspLines",()=>EspLines},{"EspLocal",()=>EspLocal},
            {"EspOtherPlayers",()=>EspOtherPlayers},{"EspTown",()=>EspTown},{"EspHostile",()=>EspHostile},
            {"EspBossRainbow",()=>EspBossRainbow},{"EspItems",()=>EspItems},{"EspWaypoints",()=>EspWaypoints},
            {"SuperSpeed",()=>SuperSpeed},{"HighJump",()=>HighJump},{"NoFallDamage",()=>NoFallDamage},
            {"LavaImmune",()=>LavaImmune},{"WaterWalk",()=>WaterWalk},{"ExtendedReach",()=>ExtendedReach},
            {"FreeCast",()=>FreeCast},{"InfItems",()=>InfItems},{"InstantBreak",()=>InstantBreak},
        };

        System.Collections.Generic.Dictionary<string, Action<bool>> BoolSetters() => new System.Collections.Generic.Dictionary<string, Action<bool>>
        {
            {"GodMode",v=>GodMode=v},{"InfHealth",v=>InfHealth=v},{"InfMana",v=>InfMana=v},{"InfFlight",v=>InfFlight=v},
            {"InfBreath",v=>InfBreath=v},{"NoKnockback",v=>NoKnockback=v},{"InstantRespawn",v=>InstantRespawn=v},
            {"FreezeTime",v=>FreezeTime=v},{"Fly",v=>Fly=v},{"RapidAttack",v=>RapidAttack=v},{"GravityFlip",v=>GravityFlip=v},
            {"FreezeEnemies",v=>FreezeEnemies=v},{"WeakenEnemies",v=>WeakenEnemies=v},{"KillAura",v=>KillAura=v},
            {"ItemVacuum",v=>ItemVacuum=v},{"DamageBoost",v=>DamageBoost=v},{"GhostHit",v=>GhostHit=v},
            {"EspEnabled",v=>EspEnabled=v},{"EspNames",v=>EspNames=v},{"EspLines",v=>EspLines=v},{"EspLocal",v=>EspLocal=v},
            {"EspOtherPlayers",v=>EspOtherPlayers=v},{"EspTown",v=>EspTown=v},{"EspHostile",v=>EspHostile=v},
            {"EspBossRainbow",v=>EspBossRainbow=v},{"EspItems",v=>EspItems=v},{"EspWaypoints",v=>EspWaypoints=v},
            {"SuperSpeed",v=>SuperSpeed=v},{"HighJump",v=>HighJump=v},{"NoFallDamage",v=>NoFallDamage=v},
            {"LavaImmune",v=>LavaImmune=v},{"WaterWalk",v=>WaterWalk=v},{"ExtendedReach",v=>ExtendedReach=v},
            {"FreeCast",v=>FreeCast=v},{"InfItems",v=>InfItems=v},{"InstantBreak",v=>InstantBreak=v},
        };

        public int TargetPid { get; private set; }
        bool _threadsStarted;

        // Re-entrant: safe to call again to (re)attach to a new Terraria instance. Worker threads
        // are started once and persist; they idle while !Attached.
        public bool Attach(out string log)
        {
            log = "";
            var o = OffsetClient.Fetch(out int pid, out string err);
            if (o == null) { Status = "Waiting for Terraria... (" + err + ")"; return false; }
            var proc = Process.GetProcessesByName("Terraria").FirstOrDefault(p => !p.HasExited);
            if (proc == null) { Status = "Waiting for Terraria..."; return false; }
            if (!_mem.Open(proc)) { Status = "OpenProcess failed (run as admin)."; return false; }
            _espMem.Open(proc);
            _moveMem.Open(proc);

            _o = o; TargetPid = pid; Status = $"Attached to Terraria (PID {pid}).";
            log = $"statics:{o.Statics.Count} player:{o.Player.Count} item:{o.Item.Count} npc:{o.Npc.Count}";
            Attached = true;

            _running = true;
            if (!_threadsStarted)
            {
                _threadsStarted = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "CheatLoop" };
                _thread.Start();
                _moveThread = new Thread(MovementLoop) { IsBackground = true, Name = "MovementLoop" };
                _moveThread.Start();
            }
            return true;
        }

        // Detach without killing threads (e.g. Terraria closed). Auto-attach can re-Attach later.
        public void MarkDetached() { Attached = false; TargetPid = 0; Status = "Waiting for Terraria..."; }

        // True once Terraria has gone away (so the auto-attach monitor can drop the stale handle).
        public bool TargetGone()
        {
            if (TargetPid == 0) return true;
            try { var p = Process.GetProcessById(TargetPid); return p.HasExited; }
            catch { return true; }
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(400);
            _moveThread?.Join(400);
            // Graceful restore of cheats that modify persistent item fields (damage/use-speed),
            // so weapons aren't left buffed after the tool closes. (Per-tick field cheats like
            // God Mode auto-heal on their own once we stop writing.)
            try { if (_mem.IsOpen) { RestoreDamageItem(); RestoreRapidItem(); RestoreBreakItem(); } } catch { }
            _mem.Dispose(); _espMem.Dispose(); _moveMem.Dispose(); Attached = false;
        }

        uint G(string s) => _o.Statics.TryGetValue(s, out uint v) ? v : 0;

        uint LocalPlayerPtr() => LocalPlayerPtr(_mem);
        uint LocalPlayerPtr(GameMemory m)
        {
            uint arr = m.ReadU32(G("player"));
            if (arr == 0) return 0;
            int my = m.ReadI32(G("myPlayer"));
            if (my < 0 || my > 255) return 0;
            return m.ReadU32(arr + _o.ArrayDataOffset + (uint)my * _o.ArrayElementSize);
        }

        void Loop()
        {
            // Lightweight player-field writes run at ~1000Hz so fields that ResetEffects() zeroes
            // and the same frame consumes (maxRunSpeed, jumpSpeedBoost, waterWalk, noKnockback,
            // immune, ...) get overwritten inside the reset->consume window each frame. Heavy work
            // (NPC/item array walks, UI snapshot) runs every 8th tick (~125Hz) to keep CPU sane.
            timeBeginPeriod(1);
            try
            {
                int tick = 0;
                while (_running && _mem.IsOpen)
                {
                    try
                    {
                        if (!Attached) { InWorld = false; Thread.Sleep(50); continue; }
                        InWorld = (_mem.ReadU32(G("gameMenu")) & 0xFF) == 0;
                        HandleWorld();
                        if (!InWorld) { Thread.Sleep(30); continue; }

                        uint p = LocalPlayerPtr();
                        if (p == 0) { Thread.Sleep(5); continue; }

                        ApplyToggles(p);          // fast: per-tick player field writes
                        ApplyOneShots(p);         // cheap: gated by request flags

                        if ((tick & 7) == 0)      // heavy work ~125Hz
                        {
                            Snapshot(p);
                            if (InfItems) FreezeInventory(p);
                            bool gather = _reqGather; _reqGather = false;
                            if (FreezeEnemies || WeakenEnemies || KillAura || GhostHit || gather) ApplyCombat(p, gather);
                            if (ItemVacuum || _reqItemTp) { DoItemVacuum(p); _reqItemTp = false; }
                        }
                        tick++;
                    }
                    catch { }
                    Thread.Sleep(1);
                }
            }
            finally { timeEndPeriod(1); }
        }

        void Snapshot(uint p)
        {
            if (_o.P("statLife", out uint a)) Life = _mem.ReadI32(p + a);
            if (_o.P("statLifeMax2", out a)) LifeMax = _mem.ReadI32(p + a);
            if (_o.P("statMana", out a)) Mana = _mem.ReadI32(p + a);
            if (_o.P("statManaMax2", out a)) ManaMax = _mem.ReadI32(p + a);
            if (_o.P("position", out a)) { PosX = _mem.ReadF32(p + a); PosY = _mem.ReadF32(p + a + 4); }
        }

        void ApplyToggles(uint p)
        {
            uint a, b;
            // God Mode: immunity-based so it works in ALL world types. creativeGodMode alone is
            // wiped every frame by ResetEffects() outside Journey mode, so we also force the
            // immunity system. We write a SMALL immuneTime (re-applied at 100Hz while running)
            // rather than a huge one, so if the tool exits/crashes the immunity expires within a
            // fraction of a second instead of lingering — God Mode auto-heals on exit.
            const int GOD_TICKS = 30; // ~0.5s of immunity, refreshed every loop iteration
            if (GodMode)
            {
                if (_o.P("creativeGodMode", out a)) _mem.WriteBool(p + a, true);
                if (_o.P("immune", out a)) _mem.WriteBool(p + a, true);
                if (_o.P("immuneNoBlink", out a)) _mem.WriteBool(p + a, true);
                if (_o.P("immuneTime", out a)) _mem.WriteI32(p + a, GOD_TICKS);
                if (_o.P("hurtCooldowns", out a))
                {
                    uint arr = _mem.ReadU32(p + a);
                    if (arr != 0)
                    {
                        int len = (int)_mem.ReadU32(arr + 4); if (len < 0 || len > 64) len = 16;
                        for (int i = 0; i < len; i++) _mem.WriteI32(arr + _o.ArrayDataOffset + (uint)i * 4, GOD_TICKS);
                    }
                }
            }
            if (InfHealth && _o.P("statLife", out a) && _o.P("statLifeMax2", out b)) _mem.WriteI32(p + a, _mem.ReadI32(p + b));
            if (InfMana && _o.P("statMana", out a) && _o.P("statManaMax2", out b)) _mem.WriteI32(p + a, _mem.ReadI32(p + b));
            if (InfBreath && _o.P("breath", out a) && _o.P("breathMax", out b)) _mem.WriteI32(p + a, _mem.ReadI32(p + b));
            if (InstantRespawn && _o.P("respawnTimer", out a) && _mem.ReadI32(p + a) > 1) _mem.WriteI32(p + a, 1);
            // Damage multiplier: boost the held item's damage field directly. The player's
            // per-class float modifiers (meleeDamage etc.) get reset to 1.0 every frame right
            // before the swing reads them, so writing them externally is unreliable. Item.damage
            // feeds GetWeaponDamage and is NOT reset per frame -> reliable.
            if (DamageBoost) ApplyDamageBoost(p); else RestoreDamageItem();
            if (RapidAttack) ApplyRapidAttack(p); else RestoreRapidItem();
            if (InstantBreak) ApplyInstantBreak(p); else RestoreBreakItem();
            if (InfFlight && _o.P("wingTime", out a) && _o.P("wingTimeMax", out b))
            {
                float max = _mem.ReadF32(p + b);
                if (max > 0f) _mem.WriteF32(p + a, max);
                if (_o.P("rocketTime", out a)) _mem.WriteI32(p + a, 0);
            }
            if (Fly) DoFly(p);
            // Movement physics fields (maxRunSpeed, jumpSpeedBoost, waterWalk, ...) are handled by
            // the dedicated high-frequency MovementLoop — see WriteMovementFields. They're reset
            // and re-consumed within a single Player.Update, so 1kHz isn't enough; the tight loop
            // lands a write inside that window almost every frame.
            // Inf Items handled in the heavy section (not racy, 58-slot walk too costly at 1kHz).
            if (!InfItems) _invStacks = null;
        }

        // Infinite items: each tick keep every inventory slot's stack from decreasing
        // (covers blocks, potions, ammo, throwables — anything consumed from inventory).
        void FreezeInventory(uint p)
        {
            if (!_o.P("inventory", out uint inv) || !_o.I("stack", out uint stOff) || !_o.I("type", out uint tyOff)) return;
            uint invArr = _mem.ReadU32(p + inv);
            if (invArr == 0) return;
            const int slots = 58;
            if (_invStacks == null || _invStacks.Length != slots) _invStacks = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                uint it = _mem.ReadU32(invArr + _o.ArrayDataOffset + (uint)i * _o.ArrayElementSize);
                if (it == 0 || _mem.ReadI32(it + tyOff) == 0) { _invStacks[i] = 0; continue; }
                int cur = _mem.ReadI32(it + stOff);
                int last = _invStacks[i];
                if (last > 1 && cur < last && cur >= 0) { _mem.WriteI32(it + stOff, last); }  // restore decrease
                else _invStacks[i] = cur;                                                       // track new baseline
            }
        }

        bool AnyMovementCheat => SuperSpeed || HighJump || NoFallDamage || LavaImmune || WaterWalk
            || NoKnockback || FreeCast || ExtendedReach || GravityFlip;

        // Dedicated tight loop: these fields are reset AND consumed inside one Player.Update, so a
        // write must land in that narrow window every frame. We spin (yielding) while any of them
        // is enabled — only costs CPU when actually in use.
        public long MoveWrites;   // diagnostic: increments per movement write batch (Interlocked)

        void MovementLoop()
        {
            var m = _moveMem;
            // Hardened: nothing here may throw out of the loop, so the thread can never die.
            // IMPORTANT: re-resolve the player pointer EVERY iteration. The Player object can move
            // (GC), and a cached pointer goes stale -> writes hit dead memory and the cheat silently
            // stops (this caused the Super Speed regression). Re-reading is cheap vs. correctness.
            while (_running)
            {
                try
                {
                    if (!Attached || !InWorld || !AnyMovementCheat || !m.IsOpen) { Thread.Sleep(15); continue; }
                    uint p = LocalPlayerPtr(m);
                    if (p != 0) { WriteMovementFields(m, p); System.Threading.Interlocked.Increment(ref MoveWrites); }
                }
                catch { Thread.Sleep(2); }
                Thread.Sleep(0); // yield to same-priority threads, but loop very fast
            }
        }

        void WriteMovementFields(GameMemory m, uint p)
        {
            uint a;
            // Super Speed: write velocity.X DIRECTLY (authoritative for the end-of-frame position
            // update) instead of maxRunSpeed (which the game's clamp resets each frame and which
            // gets diluted when other movement cheats share the loop). Velocity-direct tolerates a
            // low write rate, so it no longer breaks when High Jump / Water Walk are also on.
            if (SuperSpeed && !Fly && _o.P("velocity", out uint sv))
            {
                bool dRight = (GetAsyncKeyState(VK_D) & 0x8000) != 0;
                bool dLeft = (GetAsyncKeyState(VK_A) & 0x8000) != 0;
                if (dRight ^ dLeft)
                {
                    float target = 3f * SpeedMult * (dRight ? 1f : -1f);
                    float vx = m.ReadF32(p + sv);
                    // only push toward target; don't fight a faster dash/launch
                    if (dRight ? vx < target : vx > target) m.WriteF32(p + sv, target);
                }
                // keep maxRunSpeed raised too so the game's own accel feels right at this speed
                if (_o.P("maxRunSpeed", out a)) m.WriteF32(p + a, 3f * SpeedMult);
            }
            if (HighJump)
            {
                if (_o.P("jumpSpeedBoost", out a)) m.WriteF32(p + a, 9f);   // jump velocity boost
                if (_o.S("pl_jumpHeight", out a)) m.WriteI32(a, 45);        // jump duration (static)
            }
            if (NoFallDamage && _o.P("noFallDmg", out a)) m.WriteBool(p + a, true);
            if (LavaImmune)
            {
                if (_o.P("lavaImmune", out a)) m.WriteBool(p + a, true);
                if (_o.P("fireWalk", out a)) m.WriteBool(p + a, true);
            }
            if (WaterWalk)
            {
                if (_o.P("waterWalk", out a)) m.WriteBool(p + a, true);
                if (_o.P("waterWalk2", out a)) m.WriteBool(p + a, true);
                // Backup: if actually in water and sinking, kill the downward velocity so you stay
                // on the surface regardless of how the flag is processed that frame.
                if (_o.P("wet", out uint wo) && (m.ReadU32(p + wo) & 0xFF) != 0 &&
                    _o.P("velocity", out uint vo) && _o.P("gravDir", out uint go))
                {
                    float vy = m.ReadF32(p + vo + 4), gd = m.ReadF32(p + go);
                    if (vy * gd > 0f) m.WriteF32(p + vo + 4, 0f); // moving "down" relative to gravity
                }
            }
            if (NoKnockback && _o.P("noKnockback", out a)) m.WriteBool(p + a, true);
            if (GravityFlip && _o.P("forcedGravity", out a)) m.WriteI32(p + a, 10);
            if (FreeCast && _o.P("manaCost", out a)) m.WriteF32(p + a, 0f);
            if (ExtendedReach)
            {
                if (_o.S("pl_tileRangeX", out a)) m.WriteI32(a, 35);
                if (_o.S("pl_tileRangeY", out a)) m.WriteI32(a, 30);
            }
        }

        uint HeldItem(uint p)
        {
            if (!_o.P("inventory", out uint inv) || !_o.P("selectedItem", out uint sel)) return 0;
            uint invArr = _mem.ReadU32(p + inv);
            if (invArr == 0) return 0;
            int s = _mem.ReadI32(p + sel);
            if (s < 0 || s > 58) return 0;
            return _mem.ReadU32(invArr + _o.ArrayDataOffset + (uint)s * _o.ArrayElementSize);
        }

        void ApplyDamageBoost(uint p)
        {
            uint it = HeldItem(p);
            if (it == 0) { RestoreDamageItem(); return; }
            if (!_o.I("damage", out uint dmgOff) || !_o.I("type", out uint typeOff)) return;
            int type = _mem.ReadI32(it + typeOff);

            // New weapon selected -> restore the previous one, then capture this one's true base.
            if (it != _dmgItemPtr || type != _dmgItemType)
            {
                RestoreDamageItem();
                _dmgItemPtr = it; _dmgItemType = type; _dmgItemBase = _mem.ReadI32(it + dmgOff);
            }
            if (_dmgItemBase <= 0) return;
            int target = (int)(_dmgItemBase * DamageMult);
            if (_mem.ReadI32(it + dmgOff) != target) _mem.WriteI32(it + dmgOff, target);
        }

        void RestoreDamageItem()
        {
            if (_dmgItemPtr != 0 && _dmgItemBase > 0 && _o.I("damage", out uint dmgOff) && _o.I("type", out uint typeOff))
            {
                // only restore if it's still the same item (type matches) to avoid clobbering
                if (_mem.ReadI32(_dmgItemPtr + typeOff) == _dmgItemType)
                    _mem.WriteI32(_dmgItemPtr + dmgOff, _dmgItemBase);
            }
            _dmgItemPtr = 0; _dmgItemBase = -1; _dmgItemType = -1;
        }

        // Autofire: make the held weapon auto-reuse with a fast use speed, so holding mouse1
        // produces continuous hits regardless of the weapon's normal behavior.
        void ApplyRapidAttack(uint p)
        {
            uint it = HeldItem(p);
            if (it == 0) { RestoreRapidItem(); return; }
            if (!_o.I("type", out uint typeOff) || !_o.I("autoReuse", out uint ar) ||
                !_o.I("useTime", out uint ut) || !_o.I("useAnimation", out uint ua)) return;
            int type = _mem.ReadI32(it + typeOff);
            // New weapon selected -> restore previous, capture this one's true base speed/autoReuse.
            if (it != _raItemPtr || type != _raType)
            {
                RestoreRapidItem();
                _raItemPtr = it; _raType = type;
                _raUseTime = _mem.ReadI32(it + ut); _raUseAnim = _mem.ReadI32(it + ua);
                _raAuto = (_mem.ReadU32(it + ar) & 0xFF) != 0;
            }
            _mem.WriteBool(it + ar, true);
            if (_mem.ReadI32(it + ut) > 4) _mem.WriteI32(it + ut, 4);
            if (_mem.ReadI32(it + ua) > 6) _mem.WriteI32(it + ua, 6);
        }

        void RestoreRapidItem()
        {
            if (_raItemPtr != 0 && _o.I("type", out uint typeOff) && _o.I("autoReuse", out uint ar) &&
                _o.I("useTime", out uint ut) && _o.I("useAnimation", out uint ua) &&
                _mem.ReadI32(_raItemPtr + typeOff) == _raType)
            {
                _mem.WriteI32(_raItemPtr + ut, _raUseTime);
                _mem.WriteI32(_raItemPtr + ua, _raUseAnim);
                _mem.WriteBool(_raItemPtr + ar, _raAuto);
            }
            _raItemPtr = 0; _raType = -1;
        }

        // Instant break: boost the held tool's pick/axe/hammer power so each hit deals >=100 tile
        // damage (PickTile breaks at 100). Item stat, not reset per frame -> reliable. Restored on
        // tool switch / toggle-off / exit so we don't permanently alter the saved item.
        void ApplyInstantBreak(uint p)
        {
            uint it = HeldItem(p);
            if (it == 0) { RestoreBreakItem(); return; }
            if (!_o.I("type", out uint to) || !_o.I("pick", out uint pk) || !_o.I("axe", out uint ax) || !_o.I("hammer", out uint hm)) return;
            int type = _mem.ReadI32(it + to);
            if (it != _brkPtr || type != _brkType)
            {
                RestoreBreakItem();
                _brkPtr = it; _brkType = type;
                _brkPick = _mem.ReadI32(it + pk); _brkAxe = _mem.ReadI32(it + ax); _brkHammer = _mem.ReadI32(it + hm);
            }
            // only boost stats the tool actually has (so we don't turn a sword into a pickaxe)
            if (_brkPick > 0 && _mem.ReadI32(it + pk) != 9999) _mem.WriteI32(it + pk, 9999);
            if (_brkAxe > 0 && _mem.ReadI32(it + ax) != 9999) _mem.WriteI32(it + ax, 9999);
            if (_brkHammer > 0 && _mem.ReadI32(it + hm) != 9999) _mem.WriteI32(it + hm, 9999);
        }

        void RestoreBreakItem()
        {
            if (_brkPtr != 0 && _o.I("type", out uint to) && _o.I("pick", out uint pk) && _o.I("axe", out uint ax) && _o.I("hammer", out uint hm) &&
                _mem.ReadI32(_brkPtr + to) == _brkType)
            {
                _mem.WriteI32(_brkPtr + pk, _brkPick);
                _mem.WriteI32(_brkPtr + ax, _brkAxe);
                _mem.WriteI32(_brkPtr + hm, _brkHammer);
            }
            _brkPtr = 0; _brkType = -1;
        }

        void DoFly(uint p)
        {
            if (!_o.P("position", out uint pos) || !_o.P("velocity", out uint vel)) return;
            float x = _mem.ReadF32(p + pos), y = _mem.ReadF32(p + pos + 4);
            if ((GetAsyncKeyState(VK_W) & 0x8000) != 0) y -= FlySpeed;
            if ((GetAsyncKeyState(VK_S) & 0x8000) != 0) y += FlySpeed;
            if ((GetAsyncKeyState(VK_A) & 0x8000) != 0) x -= FlySpeed;
            if ((GetAsyncKeyState(VK_D) & 0x8000) != 0) x += FlySpeed;
            _mem.WriteF32(p + pos, x); _mem.WriteF32(p + pos + 4, y);
            _mem.WriteF32(p + vel, 0f); _mem.WriteF32(p + vel + 4, 0f); // kill gravity/momentum
        }

        void HandleWorld()
        {
            if (FreezeTime)
            {
                if (!_wasFreezing)
                {
                    _frozenTime = _mem.ReadF64(G("time"));
                    _frozenDay = (_mem.ReadU32(G("dayTime")) & 0xFF) != 0;
                    _wasFreezing = true;
                }
                _mem.WriteF64(G("time"), _frozenTime);
                _mem.WriteBool(G("dayTime"), _frozenDay);
            }
            else _wasFreezing = false;

            if (_reqNoon) { _reqNoon = false; _mem.WriteBool(G("dayTime"), true); _mem.WriteF64(G("time"), 27000.0); }
            if (_reqMidnight) { _reqMidnight = false; _mem.WriteBool(G("dayTime"), false); _mem.WriteF64(G("time"), 16200.0); }
            if (_reqSetTime >= 0) { _mem.WriteBool(G("dayTime"), _reqSetTimeDay); _mem.WriteF64(G("time"), _reqSetTime); _reqSetTime = -1; }
            if (_reqKillAll) { _reqKillAll = false; KillAllEnemies(); }

            // Weather
            if (_reqRain) { _reqRain = false; _mem.WriteBool(G("raining"), true); _mem.WriteF32(G("maxRaining"), 0.9f); _mem.WriteF32(G("cloudAlpha"), 0.9f); }
            if (_reqClear) { _reqClear = false; _mem.WriteBool(G("raining"), false); _mem.WriteF32(G("maxRaining"), 0f); _mem.WriteF32(G("cloudAlpha"), 0f); }
            if (_reqBloodMoon) { _reqBloodMoon = false; bool on = (_mem.ReadU32(G("bloodMoon")) & 0xFF) == 0; _mem.WriteBool(G("bloodMoon"), on); }
            if (_reqEclipse) { _reqEclipse = false; bool on = (_mem.ReadU32(G("eclipse")) & 0xFF) == 0; _mem.WriteBool(G("eclipse"), on); }

            // Waypoint teleport
            int wp = _reqTpWp;
            if (wp >= 0)
            {
                _reqTpWp = -1;
                Waypoint w = null;
                lock (_wpLock) if (wp < _waypoints.Count) w = _waypoints[wp];
                uint lp = LocalPlayerPtr();
                if (w != null && lp != 0 && _o.P("position", out uint pos))
                {
                    _mem.WriteF32(lp + pos, w.X); _mem.WriteF32(lp + pos + 4, w.Y);
                    if (_o.P("velocity", out uint v)) { _mem.WriteF32(lp + v, 0f); _mem.WriteF32(lp + v + 4, 0f); }
                }
            }
        }

        // Per-tick combat toggles over Main.npc[]. Skips friendly/town NPCs.
        // Combined combat pass over Main.npc[] (single walk). Skips friendly/town NPCs.
        //
        // The loot problem: external writes can't call checkDead()/NPCLoot(), and a forced
        // despawn drops nothing. SOLUTION: Kill Aura/Gather TELEPORT hostiles onto you so your
        // REAL weapon swing hits them — that runs the game's own StrikeNPC -> NPCLoot path, so
        // full loot+coins drop normally. Pair with Rapid Attack (autofire), Damage Multiplier
        // (one-shot), God Mode (ignore the swarm) and Item Vacuum (auto-collect).
        void ApplyCombat(uint p, bool gather)
        {
            uint npcArr = _mem.ReadU32(G("npc"));
            if (npcArr == 0) return;
            int max = _mem.ReadI32(G("maxNPCs")); if (max <= 0 || max > 1000) max = 200;
            if (!_o.N("active", out uint act) || !_o.N("friendly", out uint fri) || !_o.N("townNPC", out uint town)) return;
            _o.N("velocity", out uint vel); _o.N("life", out uint life);
            _o.N("position", out uint nPos); _o.N("width", out uint nW); _o.N("height", out uint nH);
            _o.N("playerInteraction", out uint pInter);
            int myPlayer = _mem.ReadI32(G("myPlayer"));

            float pcx = 0, pcy = 0;
            if (_o.P("position", out uint pp) && _o.P("width", out uint pw) && _o.P("height", out uint ph))
            { pcx = _mem.ReadF32(p + pp) + _mem.ReadI32(p + pw) / 2f; pcy = _mem.ReadF32(p + pp + 4) + _mem.ReadI32(p + ph) / 2f; }
            float rangePx = AuraRange * 16f; float rangeSq = rangePx * rangePx;

            bool swinging = _o.P("itemAnimation", out uint ia) && _mem.ReadI32(p + ia) != 0;
            uint closest = 0; float closestSq = float.MaxValue; uint closestW = 0, closestH = 0;

            for (int i = 0; i < max; i++)
            {
                uint n = _mem.ReadU32(npcArr + _o.ArrayDataOffset + (uint)i * _o.ArrayElementSize);
                if (n == 0 || (_mem.ReadU32(n + act) & 0xFF) == 0) continue;
                if ((_mem.ReadU32(n + fri) & 0xFF) != 0 || (_mem.ReadU32(n + town) & 0xFF) != 0) continue;

                int w = _mem.ReadI32(n + nW), h = _mem.ReadI32(n + nH);
                float ncx = _mem.ReadF32(n + nPos) + w / 2f, ncy = _mem.ReadF32(n + nPos + 4) + h / 2f;
                float dx = ncx - pcx, dy = ncy - pcy, distSq = dx * dx + dy * dy;
                bool inRange = distSq <= rangeSq;

                bool pullHere = gather || (KillAura && inRange);
                if (pullHere)
                {
                    // teleport so the NPC center sits on the player center -> in weapon range
                    _mem.WriteF32(n + nPos, pcx - w / 2f);
                    _mem.WriteF32(n + nPos + 4, pcy - h / 2f);
                    if (vel != 0) { _mem.WriteF32(n + vel, 0f); _mem.WriteF32(n + vel + 4, 0f); }
                    CreditKill(n, pInter, myPlayer);
                }
                else if (FreezeEnemies && vel != 0) { _mem.WriteF32(n + vel, 0f); _mem.WriteF32(n + vel + 4, 0f); }

                if (WeakenEnemies && life != 0 && _mem.ReadI32(n + life) > 1) _mem.WriteI32(n + life, 1);

                if (GhostHit && swinging && distSq < closestSq)
                { closestSq = distSq; closest = n; closestW = (uint)w; closestH = (uint)h; }
            }

            // Ghost Hit: snap the single closest hostile into your swing so attacks never miss.
            if (closest != 0)
            {
                _mem.WriteF32(closest + nPos, pcx - closestW / 2f);
                _mem.WriteF32(closest + nPos + 4, pcy - closestH / 2f);
                CreditKill(closest, pInter, myPlayer);
            }
        }

        void CreditKill(uint n, uint pInter, int myPlayer)
        {
            if (pInter == 0 || myPlayer < 0 || myPlayer >= 256) return;
            uint arr = _mem.ReadU32(n + pInter);
            if (arr != 0) _mem.WriteBool(arr + _o.ArrayDataOffset + (uint)myPlayer, true);
        }

        // Pulls nearby dropped items to the player so the game's grab logic collects them.
        void DoItemVacuum(uint p)
        {
            if (!_o.S("item", out uint itemArrAddr)) return;
            if (!_o.X("wiPos", out uint wPos) || !_o.X("wiInner", out uint wInner) || !_o.X("wiNoGrab", out uint wNoGrab)) return;
            _o.I("type", out uint iType);
            uint itemArr = _mem.ReadU32(itemArrAddr);
            if (itemArr == 0) return;
            int max = _o.S("maxItems", out uint mi) ? _mem.ReadI32(mi) : 400;
            if (max <= 0 || max > 1000) max = 400;

            float pcx = 0, pcy = 0;
            if (_o.P("position", out uint pp) && _o.P("width", out uint pw) && _o.P("height", out uint ph))
            { pcx = _mem.ReadF32(p + pp) + _mem.ReadI32(p + pw) / 2f; pcy = _mem.ReadF32(p + pp + 4) + _mem.ReadI32(p + ph) / 2f; }

            for (int i = 0; i < max; i++)
            {
                uint wi = _mem.ReadU32(itemArr + _o.ArrayDataOffset + (uint)i * _o.ArrayElementSize);
                if (wi == 0) continue;
                uint inner = _mem.ReadU32(wi + wInner);
                if (inner == 0 || _mem.ReadI32(inner + iType) == 0) continue; // empty slot (type 0)
                _mem.WriteI32(wi + wNoGrab, 0);                  // clear grab delay
                _mem.WriteF32(wi + wPos, pcx - 10f);             // snap onto the player
                _mem.WriteF32(wi + wPos + 4, pcy - 10f);
            }
        }

        void ApplyOneShots(uint p)
        {
            uint a, b;
            if (_reqMaxHp) { _reqMaxHp = false; if (_o.P("statLifeMax", out a)) _mem.WriteI32(p + a, 500); }
            if (_reqMaxMana) { _reqMaxMana = false; if (_o.P("statManaMax", out a)) _mem.WriteI32(p + a, 200); }
            if (_reqFullHeal)
            {
                _reqFullHeal = false;
                if (_o.P("statLife", out a) && _o.P("statLifeMax2", out b)) _mem.WriteI32(p + a, _mem.ReadI32(p + b));
                if (_o.P("statMana", out a) && _o.P("statManaMax2", out b)) _mem.WriteI32(p + a, _mem.ReadI32(p + b));
            }
            if (_reqTpSpawn)
            {
                _reqTpSpawn = false;
                int sx = _mem.ReadI32(G("spawnTileX")), sy = _mem.ReadI32(G("spawnTileY"));
                if (sx > 0 && _o.P("position", out a))
                {
                    _mem.WriteF32(p + a, sx * 16f); _mem.WriteF32(p + a + 4, sy * 16f - 48f);
                    if (_o.P("velocity", out b)) { _mem.WriteF32(p + b, 0f); _mem.WriteF32(p + b + 4, 0f); }
                }
            }
            // Buff applier: write into the first empty buffType[] slot (mimics Player.AddBuff).
            if (_reqBuffId >= 0)
            {
                int id = _reqBuffId; _reqBuffId = -1;
                if (_o.P("buffType", out a) && _o.P("buffTime", out b))
                {
                    uint typeArr = _mem.ReadU32(p + a), timeArr = _mem.ReadU32(p + b);
                    if (typeArr != 0 && timeArr != 0)
                    {
                        int len = (int)_mem.ReadU32(typeArr + 4); if (len <= 0 || len > 64) len = 22;
                        for (int i = 0; i < len; i++)
                        {
                            uint tAddr = typeArr + _o.ArrayDataOffset + (uint)i * 4;
                            int existing = _mem.ReadI32(tAddr);
                            if (existing == id) { _mem.WriteI32(timeArr + _o.ArrayDataOffset + (uint)i * 4, _reqBuffDur); break; }
                            if (existing == 0)
                            {
                                _mem.WriteI32(tAddr, id);
                                _mem.WriteI32(timeArr + _o.ArrayDataOffset + (uint)i * 4, _reqBuffDur);
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Live debug readout (render thread, own handle). Shows the ACTUAL field values the game
        // has right now, so we can see exactly what's working vs not while playing.
        long _dbgLastWrites; double _dbgT; int _dbgRate;
        public string DebugReadout()
        {
            if (!Attached) return "not attached";
            var m = _espMem;
            if (!m.IsOpen) return "handle closed";
            if (!InWorld) return "at menu (load a world)";
            uint p = LocalPlayerPtr(m);
            if (p == 0) return "player ptr = 0";
            try
            {
                float Read(string f) => _o.P(f, out uint o2) ? m.ReadF32(p + o2) : float.NaN;
                int ReadB(string f) => _o.P(f, out uint o2) ? (int)(m.ReadU32(p + o2) & 0xFF) : -1;
                int RS(string f) => _o.S(f, out uint o2) ? m.ReadI32(o2) : -1;
                var sb = new System.Text.StringBuilder();
                sb.Append("player=0x").Append(p.ToString("X")).Append('\n');
                sb.Append("maxRunSpeed=").Append(Read("maxRunSpeed").ToString("0.0"))
                  .Append("  accRun=").Append(Read("accRunSpeed").ToString("0.0"))
                  .Append("  runAcc=").Append(Read("runAcceleration").ToString("0.00")).Append('\n');
                sb.Append("velX=").Append(Read("velocity").ToString("0.0"))
                  .Append("  jumpBoost=").Append(Read("jumpSpeedBoost").ToString("0.0"))
                  .Append("  jumpHeight(stat)=").Append(RS("pl_jumpHeight")).Append('\n');
                sb.Append("waterWalk=").Append(ReadB("waterWalk"))
                  .Append("  ww2=").Append(ReadB("waterWalk2"))
                  .Append("  wet=").Append(ReadB("wet"))
                  .Append("  noFall=").Append(ReadB("noFallDmg")).Append('\n');
                sb.Append("tileRangeX(stat)=").Append(RS("pl_tileRangeX"))
                  .Append("  tileRangeY=").Append(RS("pl_tileRangeY")).Append('\n');
                sb.Append("toggles: spd=").Append(SuperSpeed ? 1 : 0)
                  .Append(" jmp=").Append(HighJump ? 1 : 0)
                  .Append(" water=").Append(WaterWalk ? 1 : 0)
                  .Append(" reach=").Append(ExtendedReach ? 1 : 0).Append('\n');
                double now = System.Environment.TickCount / 1000.0;
                if (now - _dbgT >= 0.5) { long c = MoveWrites; _dbgRate = (int)((c - _dbgLastWrites) / (now - _dbgT)); _dbgLastWrites = c; _dbgT = now; }
                sb.Append("MovementLoop writes/s=").Append(_dbgRate);
                return sb.ToString();
            }
            catch (Exception ex) { return "read error: " + ex.Message; }
        }

        // Called on the RENDER thread each frame: reads + projects entities from a dedicated
        // memory handle so boxes match the exact frame being drawn (no inter-thread lag/snap).
        // Reuses one List to avoid per-frame allocation. Returns null if nothing to draw.
        public System.Collections.Generic.List<EspBox> GetEspBoxes()
        {
            if (!Attached || !EspEnabled || !InWorld || !_espMem.IsOpen) return null;
            try { return BuildEspBoxes(); } catch { return _espList; } // never throw on the render thread
        }

        System.Collections.Generic.List<EspBox> BuildEspBoxes()
        {
            var m = _espMem;
            uint localP = LocalPlayerPtr(m);
            if (localP == 0) return null;

            // world -> screen:  s = zoom*(world - screenPos) - (screenSize/2)*(zoom-1)
            float zoom = 1f;
            if (_o.S("gameViewMatrix", out uint gvm))
            {
                uint svm = m.ReadU32(gvm);
                if (svm != 0 && _o.X("svmZoom", out uint zo))
                { float z = m.ReadF32(svm + zo); if (z > 0.05f) zoom = z; }
            }
            int sw = _o.S("screenWidth", out uint swo) ? m.ReadI32(swo) : 1920;
            int sh = _o.S("screenHeight", out uint sho) ? m.ReadI32(sho) : 1080;
            // Prefer the real Main.screenPosition (resolved via ReadStruct.Address; also handles
            // world-edge camera clamping). Fall back to deriving from the player if unavailable.
            float spx = 0, spy = 0; bool gotCam = false;
            if (_o.S("screenPosition", out uint sp))
            {
                spx = m.ReadF32(sp); spy = m.ReadF32(sp + 4);
                gotCam = spx != 0f || spy != 0f;
            }
            if (!gotCam && _o.P("position", out uint pcp) && _o.P("width", out uint pcw) && _o.P("height", out uint pch))
            {
                spx = m.ReadF32(localP + pcp) + m.ReadI32(localP + pcw) / 2f - sw / 2f;
                spy = m.ReadF32(localP + pcp + 4) + m.ReadI32(localP + pch) / 2f - sh / 2f;
            }
            // Game client-area origin in screen coords (0,0 for borderless; title-bar offset if windowed).
            float ox = 0, oy = 0;
            IntPtr hwnd = m.Process != null ? m.Process.MainWindowHandle : IntPtr.Zero;
            if (hwnd != IntPtr.Zero) { var pt = new POINT(); if (ClientToScreen(hwnd, ref pt)) { ox = pt.X; oy = pt.Y; } }
            float cx = sw / 2f, cy = sh / 2f, k = zoom - 1f;

            var list = _espList;
            list.Clear();

            // player world center (for distance readouts, in tiles)
            float pwx = 0, pwy = 0;
            if (_o.P("position", out uint lpp) && _o.P("width", out uint lpw) && _o.P("height", out uint lph))
            { pwx = m.ReadF32(localP + lpp) + m.ReadI32(localP + lpw) / 2f; pwy = m.ReadF32(localP + lpp + 4) + m.ReadI32(localP + lph) / 2f; }
            double rainbowHue = (Environment.TickCount % 3000) / 3000.0; // ~3s cycle

            // ---- NPCs (batched: read pointer array once, then one block read per object) ----
            if ((EspHostile || EspTown || EspBossRainbow) && _o.S("npc", out uint npcArrAddr) &&
                _o.N("position", out uint nPos) && _o.N("width", out uint nW) && _o.N("height", out uint nH) &&
                _o.N("active", out uint nAct))
            {
                uint npcArr = m.ReadU32(npcArrAddr);
                int max = _o.S("maxNPCs", out uint mn) ? m.ReadI32(mn) : 200;
                if (max <= 0 || max > 256) max = 200;
                _o.N("friendly", out uint nFri); _o.N("townNPC", out uint nTown);
                _o.N("life", out uint nLife); _o.N("lifeMax", out uint nLifeMax);
                _o.N("boss", out uint nBoss); _o.N("type", out uint nType); _o.N("defense", out uint nDef);
                if (npcArr != 0 && m.ReadBytes(npcArr + _o.ArrayDataOffset, _ptrBuf, max * 4))
                    for (int i = 0; i < max; i++)
                    {
                        uint n = BitConverter.ToUInt32(_ptrBuf, i * 4);
                        if (n == 0 || !m.ReadBytes(n, _objBuf, 0x1D4)) continue; // covers all NPC fields used
                        if (_objBuf[nAct] == 0) continue;

                        bool boss = nBoss != 0 && _objBuf[nBoss] != 0;
                        bool friendly = _objBuf[nFri] != 0 || _objBuf[nTown] != 0;
                        if (boss) { if (!EspBossRainbow) continue; }
                        else if (friendly) { if (!EspTown) continue; }
                        else { if (!EspHostile) continue; }

                        float wx = BF32(_objBuf, nPos), wy = BF32(_objBuf, nPos + 4);
                        int w = BI32(_objBuf, nW), h = BI32(_objBuf, nH);
                        float bx = zoom * (wx - spx) - cx * k + ox, by = zoom * (wy - spy) - cy * k + oy;
                        float bw = w * zoom, bh = h * zoom;
                        if (bx > sw || by > sh || bx + bw < 0 || by + bh < 0) continue;

                        uint col = boss ? Rainbow(rainbowHue) : (friendly ? CYAN : RED);
                        string tag = null;
                        if (EspNames)
                        {
                            int type = nType != 0 ? BI32(_objBuf, nType) : 0;
                            int life = BI32(_objBuf, nLife), lifeMax = nLifeMax != 0 ? BI32(_objBuf, nLifeMax) : 0;
                            int def = nDef != 0 ? BI32(_objBuf, nDef) : 0;
                            int dist = (int)(Math.Sqrt((wx + w / 2f - pwx) * (wx + w / 2f - pwx) + (wy + h / 2f - pwy) * (wy + h / 2f - pwy)) / 16f);
                            _npcNames.TryGetValue(type, out string nm);
                            string hp = life > 0 ? (lifeMax > 0 ? $"{life}/{lifeMax}hp " : $"{life}hp ") : "";
                            string df = def > 0 ? $"def{def} " : "";
                            tag = $"{(boss ? "[BOSS] " : "")}{nm}\n{hp}{df}{dist}m";
                        }
                        list.Add(new EspBox { X = bx, Y = by, W = bw, H = bh, Col = col, Tag = tag });
                    }
            }

            // ---- Other players (Main.player[], skip local) ----
            if (EspOtherPlayers && _o.S("player", out uint plArrAddr) && _o.P("position", out uint opp) &&
                _o.P("width", out uint opw) && _o.P("height", out uint oph) && _o.P("active", out uint opa))
            {
                uint plArr = m.ReadU32(plArrAddr);
                int myIdx = _o.S("myPlayer", out uint mpo) ? m.ReadI32(mpo) : 0;
                _o.P("statLife", out uint pLife); _o.P("statLifeMax2", out uint pLifeMax);
                // Player.active is deep in the object (0x6C6); check it with a small read first and
                // only project active players (there are none in single-player).
                if (plArr != 0 && m.ReadBytes(plArr + _o.ArrayDataOffset, _ptrBuf, 255 * 4))
                    for (int i = 0; i < 255; i++)
                    {
                        if (i == myIdx) continue;
                        uint pl = BitConverter.ToUInt32(_ptrBuf, i * 4);
                        if (pl == 0 || (m.ReadU32(pl + opa) & 0xFF) == 0) continue;
                        float wx = m.ReadF32(pl + opp), wy = m.ReadF32(pl + opp + 4);
                        int w = m.ReadI32(pl + opw), h = m.ReadI32(pl + oph);
                        float bx = zoom * (wx - spx) - cx * k + ox, by = zoom * (wy - spy) - cy * k + oy;
                        if (bx > sw || by > sh || bx + w < 0 || by + h < 0) continue;
                        int dist = (int)(Math.Sqrt((wx - pwx) * (wx - pwx) + (wy - pwy) * (wy - pwy)) / 16f);
                        int life = pLife != 0 ? m.ReadI32(pl + pLife) : 0, lm = pLifeMax != 0 ? m.ReadI32(pl + pLifeMax) : 0;
                        list.Add(new EspBox { X = bx, Y = by, W = w * zoom, H = h * zoom, Col = ORANGE, Tag = EspNames ? $"PLAYER\n{life}/{lm}hp {dist}m" : null });
                    }
            }

            // ---- Dropped items ----
            if (EspItems && _o.S("item", out uint itemArrAddr) && _o.X("wiPos", out uint wPos) &&
                _o.X("wiInner", out uint wInner) && _o.I("type", out uint iType) && _o.I("stack", out uint iStack))
            {
                uint itemArr = m.ReadU32(itemArrAddr);
                int imax = _o.S("maxItems", out uint mi) ? m.ReadI32(mi) : 400;
                if (imax <= 0 || imax > 256) imax = 256;
                if (itemArr != 0 && m.ReadBytes(itemArr + _o.ArrayDataOffset, _ptrBuf, imax * 4))
                    for (int i = 0; i < imax; i++)
                    {
                        uint wi = BitConverter.ToUInt32(_ptrBuf, i * 4);
                        if (wi == 0 || !m.ReadBytes(wi, _objBuf, 0x48)) continue; // wiPos(0x20)+wiInner(0x40)
                        uint inner = BitConverter.ToUInt32(_objBuf, (int)wInner);
                        if (inner == 0) continue;
                        int type = m.ReadI32(inner + iType);
                        if (type == 0) continue;
                        float wx = BF32(_objBuf, wPos), wy = BF32(_objBuf, wPos + 4);
                        float bx = zoom * (wx - spx) - cx * k + ox, by = zoom * (wy - spy) - cy * k + oy;
                        if (bx > sw || by > sh || bx + 20 < 0 || by + 20 < 0) continue;
                        string tag = null;
                        if (EspNames) { _itemNames.TryGetValue(type, out string nm); int st = m.ReadI32(inner + iStack); tag = st > 1 ? $"{nm} x{st}" : nm; }
                        list.Add(new EspBox { X = bx, Y = by, W = 20 * zoom, H = 20 * zoom, Col = GOLD, Tag = tag });
                    }
            }

            // ---- Waypoints ----
            if (EspWaypoints)
                foreach (var wpt in Waypoints)
                {
                    float bx = zoom * (wpt.X - spx) - cx * k + ox, by = zoom * (wpt.Y - spy) - cy * k + oy;
                    if (bx < -50 || by < -50 || bx > sw + 50 || by > sh + 50) continue;
                    int dist = (int)(Math.Sqrt((wpt.X - pwx) * (wpt.X - pwx) + (wpt.Y - pwy) * (wpt.Y - pwy)) / 16f);
                    list.Add(new EspBox { X = bx - 3, Y = by - 3, W = 6, H = 6, Col = MAGENTA, Tag = $"{wpt.Name}\n{dist}m" });
                }

            // ---- Local player ----
            if (EspLocal && _o.P("position", out uint pp) && _o.P("width", out uint pw) && _o.P("height", out uint ph))
            {
                float wx = m.ReadF32(localP + pp), wy = m.ReadF32(localP + pp + 4);
                int w = m.ReadI32(localP + pw), h = m.ReadI32(localP + ph);
                float bx = zoom * (wx - spx) - cx * k + ox, by = zoom * (wy - spy) - cy * k + oy;
                string tag = EspNames ? $"YOU\n{Life}/{LifeMax}hp {Mana}/{ManaMax}mp" : null;
                list.Add(new EspBox { X = bx, Y = by, W = w * zoom, H = h * zoom, Col = GREEN, Tag = tag });
            }

            return list;
        }

        void KillAllEnemies()
        {
            uint npcArr = _mem.ReadU32(G("npc"));
            if (npcArr == 0) return;
            int max = _mem.ReadI32(G("maxNPCs")); if (max <= 0 || max > 1000) max = 200;
            _o.N("active", out uint actOff); _o.N("life", out uint lifeOff);
            _o.N("friendly", out uint friOff); _o.N("townNPC", out uint townOff);
            for (int i = 0; i < max; i++)
            {
                uint n = _mem.ReadU32(npcArr + _o.ArrayDataOffset + (uint)i * _o.ArrayElementSize);
                if (n == 0) continue;
                if ((_mem.ReadU32(n + actOff) & 0xFF) == 0) continue;            // not active
                if ((_mem.ReadU32(n + friOff) & 0xFF) != 0) continue;            // friendly
                if ((_mem.ReadU32(n + townOff) & 0xFF) != 0) continue;           // town NPC
                _mem.WriteI32(n + lifeOff, 0);
                _mem.WriteBool(n + actOff, false);
            }
        }
    }
}
