using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClickableTransparentOverlay;
using ImGuiNET;

namespace TerrariaTrainer
{
    public sealed class MenuOverlay : Overlay
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int n);
        const int VK_INSERT = 0x2D;

        readonly Cheats _c = new Cheats();
        bool _open = true, _prevInsert, _attaching, _styled;
        string _log = "";

        bool _god, _hp, _mana, _infFlight, _breath, _nokb, _respawn, _freeze, _fly, _rapid;
        bool _esp, _espNames = true, _espLines;
        bool _espLocal = true, _espOther = true, _espTown = true, _espHostile = true, _espBoss = true, _espItems = true, _espWaypoints = true;
        int _spawnType = 9, _spawnStack = 99, _spawnSlot = 0;
        int _tab;
        static readonly string[] Tabs = { "PLAYER", "MOVEMENT", "COMBAT", "ITEMS", "VISUALS", "WORLD" };
        bool _gravFlip, _freezeEn, _weaken, _killAura, _vacuum, _dmgBoost, _ghostHit;
        float _auraRange = 30f, _dmgMult = 5f;
        int _timeOfDay = 12; // hours, 0-24 for the time slider
        byte[] _wpName = new byte[32];
        string _configMsg = "";
        bool _superSpeed, _highJump, _noFall, _lavaImm, _waterWalk, _reach, _freeCast, _infItems, _instaBreak;
        bool _debug;
        float _speedMult = 2.5f;
        int _buffId = 5, _buffDur = 600; // default: Ironskin, 10 min

        volatile bool _alive = true;
        volatile bool _needSync;

        public MenuOverlay() : base("GVoid Terraria")
        {
            // Restore game state on any exit path (window close, Ctrl-C, process exit).
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { _alive = false; try { _c.Stop(); } catch { } };
            StartAutoAttach();
        }

        // Continuously attach to Terraria when it's available; re-attach if it restarts.
        void StartAutoAttach()
        {
            Task.Run(() =>
            {
                while (_alive)
                {
                    try
                    {
                        if (!_c.Attached)
                        {
                            if (_c.Attach(out _log)) { _c.LoadNames(); _c.LoadConfig(); _needSync = true; }
                        }
                        else if (_c.TargetGone())
                        {
                            _c.MarkDetached();
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(1200);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            _alive = false;
            try { _c.Stop(); } catch { }
            base.Dispose(disposing);
        }

        protected override Task PostInitialized()
        {
            // Go fullscreen so the menu drags anywhere and ESP covers the whole screen.
            Position = new Point(0, 0);
            Size = new Size(GetSystemMetrics(0), GetSystemMetrics(1));
            return Task.CompletedTask;
        }

        protected override void Render()
        {
            if (!_styled) { ApplyStyle(); _styled = true; }

            bool insDown = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
            if (insDown && !_prevInsert) _open = !_open;
            _prevInsert = insDown;

            if (_needSync) { SyncFromCheats(); _needSync = false; }   // after a (re)attach
            DrawEsp();              // draws over the whole screen regardless of menu state
            if (_debug && _c.Attached) DrawDebugHud();
            if (!_open) return;

            ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(60, 60), ImGuiCond.FirstUseEver);
            ImGui.Begin("##ghax", ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar);

            DrawBanner();
            DrawTabBar();
            ImGui.Spacing();

            // Content panel
            ImGui.BeginChild("##content", new Vector2(0, -34), ImGuiChildFlags.Border);
            switch (_tab)
            {
                case 0: TabPlayer(); break;
                case 1: TabMovement(); break;
                case 2: TabCombat(); break;
                case 3: TabItems(); break;
                case 4: TabVisuals(); break;
                case 5: TabWorld(); break;
            }
            ImGui.EndChild();

            DrawFooter();
            ImGui.End();
        }

        void DrawBanner()
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            // crimson underline glow
            dl.AddRectFilledMultiColor(p, new Vector2(p.X + w, p.Y + 30),
                0x00000000, 0x00000000, 0x33282CC8, 0x33282CC8);
            ImGui.PushStyleColor(ImGuiCol.Text, RedHi);
            ImGui.SetWindowFontScale(1.5f); ImGui.Text("GVOID"); ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();
            // live attach status (auto-attach runs in the background)
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            bool on = _c.Attached;
            ImGui.TextColored(on ? new Vector4(0.30f, 0.95f, 0.45f, 1f) : new Vector4(0.95f, 0.65f, 0.25f, 1f),
                on ? "  ● attached" : "  ○ waiting for Terraria");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 52);
            ImGui.TextColored(new Vector4(0.50f, 0.51f, 0.54f, 1f), "[Insert]");
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.Separator();
            ImGui.Spacing();
        }

        void DrawTabBar()
        {
            var s = ImGui.GetStyle();
            float avail = ImGui.GetContentRegionAvail().X;
            float gap = s.ItemSpacing.X;
            float tw = (avail - gap * (Tabs.Length - 1)) / Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                bool active = _tab == i;
                ImGui.PushStyleColor(ImGuiCol.Button, active ? Red : new Vector4(0.14f, 0.145f, 0.155f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? RedHi : new Vector4(0.40f, 0.13f, 0.15f, 0.9f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, RedHi);
                ImGui.PushStyleColor(ImGuiCol.Text, active ? new Vector4(1f, 0.95f, 0.95f, 1f) : new Vector4(0.62f, 0.63f, 0.66f, 1f));
                if (ImGui.Button(Tabs[i], new Vector2(tw, 30))) _tab = i;
                ImGui.PopStyleColor(4);
                if (i < Tabs.Length - 1) ImGui.SameLine();
            }
        }

        void DrawDebugHud()
        {
            string text = _c.DebugReadout();
            var dl = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            const float sz = 14f, lh = 16f, pad = 8f;
            var lines = text.Split('\n');
            float w = 0; foreach (var ln in lines) w = Math.Max(w, font.CalcTextSizeA(sz, float.MaxValue, 0, ln).X);
            Vector2 origin = new Vector2(14, 14);
            dl.AddRectFilled(origin, new Vector2(origin.X + w + pad * 2, origin.Y + lines.Length * lh + pad * 2), 0xCC101010, 3f);
            dl.AddRect(origin, new Vector2(origin.X + w + pad * 2, origin.Y + lines.Length * lh + pad * 2), 0xFF2828C8, 3f);
            for (int i = 0; i < lines.Length; i++)
                dl.AddText(font, sz, new Vector2(origin.X + pad, origin.Y + pad + i * lh), 0xFFE0E0E0, lines[i]);
        }

        long _lastMoveWrites; double _moveRateT; int _moveRate;
        void DrawFooter()
        {
            ImGui.Separator();
            if (!_c.InWorld) { ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1), "At main menu — load a world."); return; }

            // movement-loop write rate (proves the tight-loop thread is alive and writing)
            double now = ImGui.GetTime();
            if (now - _moveRateT >= 0.5)
            {
                long cur = _c.MoveWrites;
                _moveRate = (int)((cur - _lastMoveWrites) / (now - _moveRateT));
                _lastMoveWrites = cur; _moveRateT = now;
            }
            ImGui.TextColored(new Vector4(0.45f, 0.46f, 0.49f, 1f), $"attached  |  move writes/s: {_moveRate}");
        }

        // small section header
        void Section(string label)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.82f, 0.30f, 0.32f, 1f), label.ToUpperInvariant());
            ImGui.Separator();
            ImGui.Spacing();
        }

        void TabPlayer()
        {
            Section("Survival");
            if (ImGui.Checkbox("God Mode (no damage/death)", ref _god)) _c.GodMode = _god;
            if (ImGui.Checkbox("Infinite Health", ref _hp)) _c.InfHealth = _hp;
            if (ImGui.Checkbox("Infinite Mana", ref _mana)) _c.InfMana = _mana;
            if (ImGui.Checkbox("Infinite Breath", ref _breath)) _c.InfBreath = _breath;
            if (ImGui.Checkbox("No Fall Damage", ref _noFall)) _c.NoFallDamage = _noFall;
            if (ImGui.Checkbox("Lava / Fire Immunity", ref _lavaImm)) _c.LavaImmune = _lavaImm;
            if (ImGui.Checkbox("No Knockback", ref _nokb)) _c.NoKnockback = _nokb;
            if (ImGui.Checkbox("Instant Respawn", ref _respawn)) _c.InstantRespawn = _respawn;

            Section("Quick Actions");
            if (ImGui.Button("Full Heal")) _c.RequestFullHeal(); ImGui.SameLine();
            if (ImGui.Button("Max HP 500")) _c.RequestMaxHp(); ImGui.SameLine();
            if (ImGui.Button("Max Mana 200")) _c.RequestMaxMana();

            Section("Buff Applier");
            ImGui.SetNextItemWidth(90); ImGui.InputInt("ID", ref _buffId); ImGui.SameLine();
            ImGui.SetNextItemWidth(110); ImGui.InputInt("Sec", ref _buffDur); ImGui.SameLine();
            if (ImGui.Button("Apply")) _c.ApplyBuff(_buffId, _buffDur);
            ImGui.TextDisabled("5=Ironskin 8=Regen 11=Swift 1=ObsSkin 112=Endurance");
        }

        void TabMovement()
        {
            Section("Movement");
            if (ImGui.Checkbox("Fly / Noclip  (hold WASD)", ref _fly)) _c.Fly = _fly;
            if (ImGui.Checkbox("Infinite Flight (wings)", ref _infFlight)) _c.InfFlight = _infFlight;
            if (ImGui.Checkbox("Super Speed", ref _superSpeed)) _c.SuperSpeed = _superSpeed;
            if (_superSpeed)
            {
                ImGui.SetNextItemWidth(-70);
                if (ImGui.SliderFloat("speed", ref _speedMult, 1.5f, 12f, "%.1fx")) _c.SpeedMult = _speedMult;
            }
            if (ImGui.Checkbox("High Jump", ref _highJump)) _c.HighJump = _highJump;
            if (ImGui.Checkbox("Water Walking", ref _waterWalk)) _c.WaterWalk = _waterWalk;
            if (ImGui.Checkbox("Gravity Flip (walk on ceiling)", ref _gravFlip)) _c.GravityFlip = _gravFlip;
            ImGui.Spacing();
            if (ImGui.Button("Teleport to Spawn", new Vector2(-1, 0))) _c.RequestTpSpawn();

            Section("Waypoints");
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##wpname", _wpName, (uint)_wpName.Length);
            ImGui.SameLine();
            if (ImGui.Button("Add Here", new Vector2(-1, 0))) { _c.AddWaypoint(NameStr()); Array.Clear(_wpName, 0, _wpName.Length); }
            ImGui.Spacing();
            var wps = _c.Waypoints;
            if (wps.Length == 0) ImGui.TextDisabled("None yet. Stand somewhere and Add Here.");
            for (int i = 0; i < wps.Length; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Button("TP")) _c.TeleportWaypoint(i);
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.10f, 0.10f, 0.9f));
                if (ImGui.Button("X")) _c.RemoveWaypoint(i);
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.Text($"{wps[i].Name}  ({wps[i].X / 16:0}, {wps[i].Y / 16:0})");
                ImGui.PopID();
            }
        }

        void TabCombat()
        {
            Section("Weapon");
            if (ImGui.Checkbox("Rapid Attack (hold M1 autofire)", ref _rapid)) _c.RapidAttack = _rapid;
            if (ImGui.Checkbox("Damage Multiplier", ref _dmgBoost)) _c.DamageBoost = _dmgBoost;
            if (_dmgBoost)
            {
                ImGui.SetNextItemWidth(-70);
                if (ImGui.SliderFloat("x mult", ref _dmgMult, 1f, 50f, "%.0fx")) _c.DamageMult = _dmgMult;
            }
            if (ImGui.Checkbox("Free Casting (0 mana cost)", ref _freeCast)) _c.FreeCast = _freeCast;

            Section("Enemies");
            ImGui.TextDisabled("Hostile NPCs only (skips town/friendly).");
            ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(1f, 0.35f, 0.35f, 1f));
            if (ImGui.Checkbox("Kill Aura (pulls enemies to you)", ref _killAura)) _c.KillAura = _killAura;
            ImGui.PopStyleColor();
            if (ImGui.Checkbox("Ghost Hit (swing auto-hits closest)", ref _ghostHit)) _c.GhostHit = _ghostHit;
            if (ImGui.Checkbox("Freeze Enemies (lock in place)", ref _freezeEn)) _c.FreezeEnemies = _freezeEn;
            if (ImGui.Checkbox("Weaken Enemies (set to 1 HP)", ref _weaken)) _c.WeakenEnemies = _weaken;
            ImGui.SetNextItemWidth(-70);
            if (ImGui.SliderFloat("Range", ref _auraRange, 8f, 120f, "%.0f tiles")) _c.AuraRange = _auraRange;
            ImGui.Spacing();
            if (ImGui.Button("Gather Enemies to Me", new Vector2(-1, 0))) _c.RequestGather();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.10f, 0.10f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Kill All Enemies (once, no loot)", new Vector2(-1, 0))) _c.RequestKillAll();
            ImGui.PopStyleColor(2);
            if (_killAura)
                ImGui.TextWrapped("Teleports hostiles in range onto you so your REAL swings hit them -> full loot. Pair with Rapid Attack + Damage Multiplier.");
        }

        void TabItems()
        {
            Section("Spawner");
            ImGui.TextWrapped("Copies a full item template into a slot — instant, no drop/pickup.");
            ImGui.InputInt("Item Type ID", ref _spawnType);
            ImGui.InputInt("Stack", ref _spawnStack);
            ImGui.SliderInt("Inventory Slot", ref _spawnSlot, 0, 49);
            if (ImGui.Button("Spawn", new Vector2(-1, 28)))
            { _c.SpawnType = _spawnType; _c.SpawnStack = _spawnStack; _c.SpawnSlot = _spawnSlot; _c.RequestSpawn(); }
            ImGui.TextDisabled("9=Wood 74=Platinum 29=Life Crystal 188=Cloud");
            if (_c.LastSpawn.Length > 0)
                ImGui.TextColored(_c.LastSpawn.StartsWith("OK") ? new Vector4(0.4f, 1f, 0.6f, 1) : new Vector4(1f, 0.4f, 0.4f, 1), _c.LastSpawn);

            Section("Mining & Building");
            if (ImGui.Checkbox("Instant Break (one-hit mine)", ref _instaBreak)) _c.InstantBreak = _instaBreak;
            if (ImGui.Checkbox("Extended Reach (mine/build far)", ref _reach)) _c.ExtendedReach = _reach;
            if (ImGui.Checkbox("Infinite Items (no consume)", ref _infItems)) _c.InfItems = _infItems;
            if (ImGui.Checkbox("Item Vacuum (pull drops to you)", ref _vacuum)) _c.ItemVacuum = _vacuum;
            ImGui.Spacing();
            if (ImGui.Button("Item Teleport (drops -> you)", new Vector2(-1, 0))) _c.RequestItemTeleport();
        }

        void TabVisuals()
        {
            Section("ESP");
            if (ImGui.Checkbox("2D Box ESP (master)", ref _esp)) _c.EspEnabled = _esp;
            if (ImGui.Checkbox("Snaplines", ref _espLines)) _c.EspLines = _espLines;
            if (ImGui.Checkbox("Entity Info Text", ref _espNames)) _c.EspNames = _espNames;

            Section("Targets");
            if (ImGui.Checkbox("Local Player", ref _espLocal)) _c.EspLocal = _espLocal;
            if (ImGui.Checkbox("Other Players", ref _espOther)) _c.EspOtherPlayers = _espOther;
            if (ImGui.Checkbox("Town / Friendly", ref _espTown)) _c.EspTown = _espTown;
            if (ImGui.Checkbox("Hostile", ref _espHostile)) _c.EspHostile = _espHostile;
            if (ImGui.Checkbox("Bosses (rainbow)", ref _espBoss)) _c.EspBossRainbow = _espBoss;
            if (ImGui.Checkbox("Dropped Items", ref _espItems)) _c.EspItems = _espItems;
            if (ImGui.Checkbox("Waypoints", ref _espWaypoints)) _c.EspWaypoints = _espWaypoints;

            Section("Legend");
            ImGui.TextColored(new Vector4(1f, 0.30f, 0.30f, 1f), "■"); ImGui.SameLine(); ImGui.TextDisabled("hostile");
            ImGui.TextColored(new Vector4(0.24f, 0.86f, 1f, 1f), "■"); ImGui.SameLine(); ImGui.TextDisabled("town");
            ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.59f, 0.16f, 1f), "■"); ImGui.SameLine(); ImGui.TextDisabled("player");
            ImGui.TextColored(new Vector4(0.24f, 1f, 0.47f, 1f), "■"); ImGui.SameLine(); ImGui.TextDisabled("you");
            ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.82f, 0.24f, 1f), "■"); ImGui.SameLine(); ImGui.TextDisabled("item");
        }

        void TabWorld()
        {
            Section("Time");
            if (ImGui.Checkbox("Freeze Time", ref _freeze)) _c.FreezeTime = _freeze;
            ImGui.SetNextItemWidth(-60);
            ImGui.SliderInt("##time", ref _timeOfDay, 0, 24, _timeOfDay.ToString("00") + ":00");
            ImGui.SameLine();
            if (ImGui.Button("Set##time")) ApplyTimeOfDay();
            if (ImGui.Button("Noon")) _c.RequestNoon(); ImGui.SameLine();
            if (ImGui.Button("Midnight")) _c.RequestMidnight();

            Section("Weather & Events");
            if (ImGui.Button("Make it Rain")) _c.RequestRain(); ImGui.SameLine();
            if (ImGui.Button("Clear Sky")) _c.RequestClearWeather();
            if (ImGui.Button("Toggle Blood Moon")) _c.RequestBloodMoon(); ImGui.SameLine();
            if (ImGui.Button("Toggle Eclipse")) _c.RequestEclipse();

            Section("Config");
            if (ImGui.Button("Save Settings")) _configMsg = _c.SaveConfig(); ImGui.SameLine();
            if (ImGui.Button("Load Settings")) { _configMsg = _c.LoadConfig(); SyncFromCheats(); }
            if (_configMsg.Length > 0) ImGui.TextWrapped(_configMsg);
            ImGui.TextDisabled("Settings auto-load on attach.");

            Section("Debug");
            if (ImGui.Checkbox("Live Debug HUD (top-left)", ref _debug)) { }
            ImGui.TextDisabled("Shows real field values + write rate while you play.");
        }

        // Terraria time: day starts 04:30 (t=0..54000 over 15h), night 19:30 (t=0..32400 over 9h).
        void ApplyTimeOfDay()
        {
            double h = _timeOfDay % 24;
            if (h >= 4.5 && h < 19.5) _c.RequestSetTime((int)((h - 4.5) * 3600), true);
            else { double nh = h < 4.5 ? h + 24 : h; _c.RequestSetTime((int)((nh - 19.5) * 3600), false); }
        }

        string NameStr()
        {
            int n = Array.IndexOf(_wpName, (byte)0); if (n < 0) n = _wpName.Length;
            return System.Text.Encoding.UTF8.GetString(_wpName, 0, n);
        }

        // Pull authoritative state from Cheats into the UI mirror bools (after config load).
        void SyncFromCheats()
        {
            _god = _c.GodMode; _hp = _c.InfHealth; _mana = _c.InfMana; _infFlight = _c.InfFlight;
            _breath = _c.InfBreath; _nokb = _c.NoKnockback; _respawn = _c.InstantRespawn; _freeze = _c.FreezeTime;
            _fly = _c.Fly; _rapid = _c.RapidAttack; _gravFlip = _c.GravityFlip; _freezeEn = _c.FreezeEnemies;
            _weaken = _c.WeakenEnemies; _killAura = _c.KillAura; _vacuum = _c.ItemVacuum; _dmgBoost = _c.DamageBoost;
            _ghostHit = _c.GhostHit; _auraRange = _c.AuraRange; _dmgMult = _c.DamageMult;
            _superSpeed = _c.SuperSpeed; _highJump = _c.HighJump; _noFall = _c.NoFallDamage; _lavaImm = _c.LavaImmune;
            _waterWalk = _c.WaterWalk; _reach = _c.ExtendedReach; _freeCast = _c.FreeCast; _infItems = _c.InfItems; _instaBreak = _c.InstantBreak; _speedMult = _c.SpeedMult;
            _esp = _c.EspEnabled; _espNames = _c.EspNames; _espLines = _c.EspLines; _espLocal = _c.EspLocal;
            _espOther = _c.EspOtherPlayers; _espTown = _c.EspTown; _espHostile = _c.EspHostile;
            _espBoss = _c.EspBossRainbow; _espItems = _c.EspItems; _espWaypoints = _c.EspWaypoints;
        }

        void DrawEsp()
        {
            var boxes = _c.GetEspBoxes();
            if (boxes == null || boxes.Count == 0) return;
            var dl = ImGui.GetForegroundDrawList();
            Vector2 disp = ImGui.GetIO().DisplaySize;
            Vector2 origin = new Vector2(disp.X / 2f, disp.Y); // bottom-center snapline anchor
            bool lines = _c.EspLines;
            var font = ImGui.GetFont();
            const float fontSz = 13f, lineH = 13f;
            foreach (var b in boxes)
            {
                if (lines)
                {
                    Vector2 c = new Vector2(b.X + b.W / 2f, b.Y + b.H / 2f);
                    uint lineCol = (b.Col & 0x00FFFFFF) | 0x66000000; // box color, ~40% alpha
                    dl.AddLine(origin, c, lineCol, 1.2f);
                }
                dl.AddRect(new Vector2(b.X, b.Y), new Vector2(b.X + b.W, b.Y + b.H), b.Col, 0f, ImDrawFlags.None, 1.4f);
                if (b.Tag != null)
                {
                    // multi-line label centered above the box, small font, with shadow
                    string[] linesArr = b.Tag.Split('\n');
                    float topY = b.Y - 3 - linesArr.Length * lineH;
                    for (int li = 0; li < linesArr.Length; li++)
                    {
                        string ln = linesArr[li];
                        if (ln.Length == 0) continue;
                        float w = font.CalcTextSizeA(fontSz, float.MaxValue, 0, ln).X;
                        float tx = b.X + b.W / 2f - w / 2f, ty = topY + li * lineH;
                        // first line (name) in box color, rest in light gray
                        uint col = li == 0 ? b.Col : 0xFFCfCfCf;
                        dl.AddText(font, fontSz, new Vector2(tx + 1, ty + 1), 0xE0000000, ln);
                        dl.AddText(font, fontSz, new Vector2(tx, ty), col, ln);
                    }
                }
            }
        }

        // Red/gray "forum threading" theme: dark slate panels, crimson accents, boxy edges.
        static readonly Vector4 Red = new Vector4(0.78f, 0.13f, 0.16f, 1f);
        static readonly Vector4 RedHi = new Vector4(0.92f, 0.22f, 0.24f, 1f);

        static void ApplyStyle()
        {
            var s = ImGui.GetStyle();
            s.WindowRounding = 3; s.FrameRounding = 2; s.GrabRounding = 2; s.PopupRounding = 2;
            s.ScrollbarRounding = 2; s.ChildRounding = 3; s.TabRounding = 2;
            s.WindowBorderSize = 1; s.FrameBorderSize = 1; s.ChildBorderSize = 1;
            s.WindowPadding = new Vector2(10, 10); s.ItemSpacing = new Vector2(8, 6);
            s.FramePadding = new Vector2(8, 5); s.IndentSpacing = 14;

            Vector4 bg = new Vector4(0.085f, 0.088f, 0.095f, 0.98f);   // near-black slate
            Vector4 panel = new Vector4(0.13f, 0.135f, 0.145f, 1f);    // gray thread row
            Vector4 panelHi = new Vector4(0.17f, 0.175f, 0.185f, 1f);
            var c = s.Colors;
            c[(int)ImGuiCol.WindowBg] = bg;
            c[(int)ImGuiCol.ChildBg] = new Vector4(0.10f, 0.105f, 0.115f, 1f);
            c[(int)ImGuiCol.PopupBg] = bg;
            c[(int)ImGuiCol.Border] = new Vector4(0.30f, 0.10f, 0.11f, 0.55f);
            c[(int)ImGuiCol.Text] = new Vector4(0.85f, 0.86f, 0.88f, 1f);
            c[(int)ImGuiCol.TextDisabled] = new Vector4(0.48f, 0.49f, 0.52f, 1f);
            c[(int)ImGuiCol.FrameBg] = panel;
            c[(int)ImGuiCol.FrameBgHovered] = panelHi;
            c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.22f, 0.10f, 0.11f, 1f);
            c[(int)ImGuiCol.Header] = new Vector4(0.30f, 0.10f, 0.11f, 0.75f);
            c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.45f, 0.13f, 0.15f, 0.85f);
            c[(int)ImGuiCol.HeaderActive] = Red;
            c[(int)ImGuiCol.Button] = panel;
            c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.45f, 0.14f, 0.16f, 1f);
            c[(int)ImGuiCol.ButtonActive] = Red;
            c[(int)ImGuiCol.CheckMark] = RedHi;
            c[(int)ImGuiCol.SliderGrab] = Red;
            c[(int)ImGuiCol.SliderGrabActive] = RedHi;
            c[(int)ImGuiCol.ScrollbarBg] = new Vector4(0, 0, 0, 0.25f);
            c[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.30f, 0.30f, 0.32f, 1f);
            c[(int)ImGuiCol.ScrollbarGrabHovered] = Red;
            c[(int)ImGuiCol.Separator] = new Vector4(0.30f, 0.10f, 0.11f, 0.55f);
            c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.22f, 0.09f, 0.10f, 1f);
        }
    }
}
