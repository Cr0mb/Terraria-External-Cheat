using System;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace TerrariaTrainer
{
    public static class Resolver
    {
        static readonly (string key, string field)[] WantedStatics =
        {
            ("player", "player"), ("myPlayer", "myPlayer"), ("gameMenu", "gameMenu"),
            ("time", "time"), ("dayTime", "dayTime"), ("moonPhase", "moonPhase"),
            ("fastDawn", "fastForwardTimeToDawn"), ("fastDusk", "fastForwardTimeToDusk"),
            ("spawnTileX", "spawnTileX"), ("spawnTileY", "spawnTileY"),
            ("npc", "npc"), ("maxNPCs", "maxNPCs"),
            ("screenPosition", "screenPosition"), ("screenWidth", "screenWidth"),
            ("screenHeight", "screenHeight"), ("gameViewMatrix", "GameViewMatrix"),
            ("raining", "raining"), ("maxRaining", "maxRaining"), ("cloudAlpha", "cloudAlpha"),
            ("bloodMoon", "bloodMoon"), ("eclipse", "eclipse"),
            ("item", "item"), ("maxItems", "maxItems"),
        };

        static readonly string[] WantedPlayerFields =
        {
            "statLife", "statLifeMax", "statLifeMax2",
            "statMana", "statManaMax", "statManaMax2",
            "statDefense", "creativeGodMode", "noKnockback",
            "wingTime", "wingTimeMax", "rocketTime", "rocketTimeMax",
            "breath", "breathMax", "respawnTimer", "inventory",
            "position", "velocity", "itemAnimation", "itemTime",
            "width", "height", "dead", "gravDir",
            "immune", "immuneTime", "immuneNoBlink", "hurtCooldowns", "forcedGravity",
            "meleeDamage", "magicDamage", "rangedDamage", "minionDamage",
            "selectedItem", "inventory", "active",
            "maxRunSpeed", "accRunSpeed", "runAcceleration", "jumpSpeedBoost",
            "noFallDmg", "lavaImmune", "fireWalk", "waterWalk", "waterWalk2", "wet", "manaCost",
            "buffType", "buffTime", "dontConsumeWand",
        };

        static readonly string[] WantedItemFields = { "type", "stack", "maxStack", "prefix", "autoReuse", "useTime", "useAnimation", "damage", "pick", "axe", "hammer" };
        static readonly string[] WantedNpcFields = { "active", "life", "lifeMax", "friendly", "townNPC", "dontTakeDamage", "position", "velocity", "width", "height", "playerInteraction", "boss", "type", "netID", "defense", "_givenName" };

        // ---- normal attach (read-only, non-suspending) for the overlay ----
        public static Offsets Resolve(int pid, out string log)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: false))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var o = ResolveCore(runtime, out log);
                if (!o.Statics.ContainsKey("player") || !o.Statics.ContainsKey("myPlayer"))
                    throw new InvalidOperationException("Could not resolve Main statics (load a world first).\n" + log);
                return o;
            }
        }

        static Offsets ResolveCore(ClrRuntime runtime, out string log)
        {
            var sb = new StringBuilder();
            var o = new Offsets();

            ClrType mainType = FindType(runtime, "Terraria.Main");
            ClrType playerType = FindType(runtime, "Terraria.Player");
            ClrType itemType = FindType(runtime, "Terraria.Item");
            ClrType npcType = FindType(runtime, "Terraria.NPC");
            if (mainType == null || playerType == null) throw new InvalidOperationException("Core Terraria types not found.");

            foreach (var (key, field) in WantedStatics)
            {
                uint a = StaticAddr(runtime, mainType, field);
                if (a != 0) o.Statics[key] = a;
            }
            // Player static reach + jump fields (stored in Statics with a "pl_" prefix).
            foreach (var field in new[] { "tileRangeX", "tileRangeY", "jumpHeight" })
            {
                uint a = StaticAddr(runtime, playerType, field);
                if (a != 0) o.Statics["pl_" + field] = a;
            }
            foreach (var name in WantedPlayerFields)
            {
                var f = playerType.GetFieldByName(name);
                if (f != null) o.Player[name] = (uint)f.GetAddress(0);
            }
            // selectedItem is a property (=> selectedItemState.selected). Resolve the nested
            // struct field: Player.selectedItemState offset + the struct's 'selected' field offset.
            var sisF = playerType.GetFieldByName("selectedItemState");
            if (sisF != null)
            {
                uint sisOff = (uint)sisF.GetAddress(0);
                var selF = sisF.Type?.GetFieldByName("selected");
                o.Player["selectedItem"] = sisOff + (selF != null ? (uint)selF.Offset : 0u);
            }
            if (itemType != null)
                foreach (var name in WantedItemFields)
                { var f = itemType.GetFieldByName(name); if (f != null) o.Item[name] = (uint)f.GetAddress(0); }
            if (npcType != null)
                foreach (var name in WantedNpcFields)
                { var f = npcType.GetFieldByName(name); if (f != null) o.Npc[name] = (uint)f.GetAddress(0); }

            // SpriteViewMatrix._zoom (Vector2) for world->screen projection
            var svmType = FindType(runtime, "Terraria.Graphics.SpriteViewMatrix");
            var zoomF = svmType?.GetFieldByName("_zoom");
            if (zoomF != null) o.Misc["svmZoom"] = (uint)zoomF.GetAddress(0);

            // WorldItem (Main.item[] element type) : Entity. Used by the item-vacuum cheat.
            var wiType = FindType(runtime, "Terraria.WorldItem");
            if (wiType != null)
                foreach (var (key, fld) in new[] { ("wiPos", "position"), ("wiVel", "velocity"), ("wiNoGrab", "noGrabDelay"), ("wiInner", "inner") })
                { var f = wiType.GetFieldByName(fld); if (f != null) o.Misc[key] = (uint)f.GetAddress(0); }

            // screenPosition is a Vector2 value-type static: ClrStaticField.GetAddress() resolves
            // it WRONG (GC region). ReadStruct(domain).Address gives the true storage address.
            var spF = mainType.GetStaticFieldByName("screenPosition");
            if (spF != null)
                foreach (var dom in runtime.AppDomains)
                {
                    try { var cv = spF.ReadStruct(dom); if (cv.Address != 0) { o.Statics["screenPosition"] = (uint)cv.Address; break; } }
                    catch { }
                }

            sb.AppendLine($"statics:{o.Statics.Count} player:{o.Player.Count} item:{o.Item.Count} npc:{o.Npc.Count} misc:{o.Misc.Count}");
            log = sb.ToString();
            return o;
        }

        // ---- spawn: copy a fully-initialized template Item into an inventory slot ----
        public static string Spawn(int pid, int type, int stack, int slot)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true)) // freeze: no GC moves mid-copy
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var o = ResolveCore(runtime, out _);

                if (!TryGetItemTemplate(runtime, type, out ulong tmplAddr, out uint itemSize))
                    return "ERR template not found for item type " + type;

                using (var mem = new GameMemory())
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (!mem.Open(proc)) return "ERR OpenProcess failed";

                    uint pPtr = LocalPlayer(mem, o);
                    if (pPtr == 0) return "ERR no local player (load a world)";
                    if (!o.P("inventory", out uint invOff) || !o.I("stack", out uint stackOff))
                        return "ERR missing inventory/stack offset";

                    uint invArr = mem.ReadU32(pPtr + invOff);
                    if (invArr == 0) return "ERR inventory null";
                    uint invLen = mem.ReadU32(invArr + 4); // SZArray length at +4 on x86
                    if (slot < 0 || slot >= invLen) return $"ERR slot {slot} out of range (inv len {invLen}, arr@0x{invArr:X})";
                    uint elemAddr = unchecked(invArr + o.ArrayDataOffset + (uint)slot * o.ArrayElementSize);
                    uint slotItem = mem.ReadU32(elemAddr);
                    if (slotItem == 0) return $"ERR slot item null (elem@0x{elemAddr:X})";

                    // copy field region [4 .. itemSize) from template into the slot's Item, then set stack
                    int len = (int)itemSize - 4;
                    var buf = new byte[len];
                    if (!mem.ReadBytes(unchecked((uint)tmplAddr) + 4, buf)) return "ERR read template failed";
                    if (!mem.WriteBytes(slotItem + 4, buf)) return "ERR write item failed";
                    mem.WriteI32(slotItem + stackOff, stack);
                    o.I("type", out uint tOff); o.I("maxStack", out uint msOff);
                    int rbType = mem.ReadI32(slotItem + tOff), rbMax = mem.ReadI32(slotItem + msOff);
                    return $"OK spawned type {type} x{stack} slot {slot} (readback type={rbType} maxStack={rbMax})";
                }
            }
        }

        // diagnostic: authoritative ClrMD field offsets + template values for one item type
        public static string ItemInfo(int pid, int type)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var itemType = FindType(runtime, "Terraria.Item");
                var sb = new StringBuilder();
                foreach (var fn in new[] { "type", "stack", "maxStack", "damage", "useStyle", "value" })
                {
                    var f = itemType.GetFieldByName(fn);
                    sb.Append($"{fn}: off=0x{(uint)f.GetAddress(0):X} ");
                }
                sb.AppendLine();
                if (TryGetItemTemplate(runtime, type, out ulong addr, out uint size))
                {
                    sb.AppendLine($"template@0x{addr:X} size={size}");
                    var cs = FindType(runtime, "Terraria.ID.ContentSamples");
                    var f = cs.GetStaticFieldByName("ItemsByType");
                    foreach (var dom in runtime.AppDomains)
                    {
                        var dict = f.ReadObject(dom);
                        if (dict.IsNull) continue;
                        var entries = dict.ReadObjectField("entries"); var arr = entries.AsArray();
                        int count = dict.ReadField<int>("count");
                        for (int i = 0; i < count; i++)
                        { var e = arr.GetStructValue(i); if (e.ReadField<int>("key") != type) continue;
                          var v = e.ReadObjectField("value");
                          sb.AppendLine($"ClrMD values: type={v.ReadField<int>("type")} maxStack={v.ReadField<int>("maxStack")} damage={v.ReadField<int>("damage")}");
                          break; }
                        break;
                    }
                }
                return sb.ToString();
            }
        }

        // diagnostic: dump screen/zoom + project active NPCs, report how many land on-screen
        public static string EspTest(int pid)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var o = ResolveCore(runtime, out _);
                using (var mem = new GameMemory())
                {
                    mem.Open(System.Diagnostics.Process.GetProcessById(pid));
                    float zoom = 1f;
                    if (o.S("gameViewMatrix", out uint gvm)) { uint svm = mem.ReadU32(gvm); if (svm != 0 && o.X("svmZoom", out uint zo)) zoom = mem.ReadF32(svm + zo); }
                    o.S("screenWidth", out uint swo); o.S("screenHeight", out uint sho);
                    int sw = mem.ReadI32(swo), sh = mem.ReadI32(sho);
                    float cx = sw / 2f, cy = sh / 2f, k = zoom - 1f;
                    var sb = new StringBuilder();
                    uint pp = LocalPlayer(mem, o);
                    float plx = 0, ply = 0, spx = 0, spy = 0;
                    if (pp != 0 && o.P("position", out uint ppos) && o.P("width", out uint ppw) && o.P("height", out uint pph))
                    {
                        plx = mem.ReadF32(pp + ppos); ply = mem.ReadF32(pp + ppos + 4);
                        spx = plx + mem.ReadI32(pp + ppw) / 2f - sw / 2f;
                        spy = ply + mem.ReadI32(pp + pph) / 2f - sh / 2f;
                    }
                    float rspx = 0, rspy = 0;
                    if (o.S("screenPosition", out uint rsp)) { rspx = mem.ReadF32(rsp); rspy = mem.ReadF32(rsp + 4); }
                    sb.AppendLine($"REAL screenPos=({rspx:0.0},{rspy:0.0})  DERIVED=({spx:0.0},{spy:0.0})  diff=({rspx - spx:0.0},{rspy - spy:0.0})");
                    spx = rspx; spy = rspy; // use real for projection test
                    o.S("npc", out uint npcAddr); uint npcArr = mem.ReadU32(npcAddr);
                    o.S("maxNPCs", out uint mn); int max = mem.ReadI32(mn); if (max <= 0 || max > 1000) max = 200;
                    o.N("active", out uint act); o.N("position", out uint np); o.N("width", out uint nw); o.N("height", out uint nh);
                    int active = 0, onscreen = 0; string first = "";
                    for (int i = 0; i < max; i++)
                    {
                        uint n = mem.ReadU32(npcArr + o.ArrayDataOffset + (uint)i * o.ArrayElementSize);
                        if (n == 0 || (mem.ReadU32(n + act) & 0xFF) == 0) continue;
                        active++;
                        float wx = mem.ReadF32(n + np), wy = mem.ReadF32(n + np + 4);
                        int w = mem.ReadI32(n + nw), h = mem.ReadI32(n + nh);
                        float bx = zoom * (wx - spx) - cx * k, by = zoom * (wy - spy) - cy * k;
                        if (bx > -200 && by > -200 && bx < sw + 200 && by < sh + 200) onscreen++;
                        if (first == "") first = $"npc[{i}] world=({wx:0},{wy:0}) size={w}x{h} -> screen=({bx:0},{by:0})";
                    }
                    sb.AppendLine($"active NPCs={active} on-screen={onscreen}");
                    sb.AppendLine(first);
                    return sb.ToString();
                }
            }
        }

        // diagnostic: verify playerInteraction array deref + WorldItem vacuum fields read sanely
        public static string AuraTest(int pid)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var o = ResolveCore(runtime, out _);
                using (var mem = new GameMemory())
                {
                    mem.Open(System.Diagnostics.Process.GetProcessById(pid));
                    var sb = new StringBuilder();
                    uint npcArr = mem.ReadU32(o.Statics["npc"]);
                    int max = mem.ReadI32(o.Statics["maxNPCs"]); if (max <= 0 || max > 1000) max = 200;
                    o.N("active", out uint act); o.N("friendly", out uint fri); o.N("townNPC", out uint town);
                    o.N("life", out uint life); o.N("playerInteraction", out uint pInter);
                    int hostiles = 0, withArr = 0;
                    for (int i = 0; i < max; i++)
                    {
                        uint n = mem.ReadU32(npcArr + o.ArrayDataOffset + (uint)i * o.ArrayElementSize);
                        if (n == 0 || (mem.ReadU32(n + act) & 0xFF) == 0) continue;
                        if ((mem.ReadU32(n + fri) & 0xFF) != 0 || (mem.ReadU32(n + town) & 0xFF) != 0) continue;
                        hostiles++;
                        uint arr = mem.ReadU32(n + pInter);
                        if (arr != 0) { withArr++; if (hostiles == 1) sb.AppendLine($"hostile npc[{i}] life={mem.ReadI32(n + life)} pInterArr@0x{arr:X} len={mem.ReadU32(arr + 4)}"); }
                    }
                    sb.AppendLine($"hostiles={hostiles} withInteractionArr={withArr}");
                    // WorldItem vacuum check
                    uint itemArr = mem.ReadU32(o.Statics["item"]);
                    o.X("wiInner", out uint wInner); o.X("wiPos", out uint wPos); o.X("wiNoGrab", out uint wNoGrab); o.I("type", out uint iType);
                    int liveItems = 0; string firstItem = "";
                    int imax = o.Statics.ContainsKey("maxItems") ? mem.ReadI32(o.Statics["maxItems"]) : 400; if (imax <= 0 || imax > 1000) imax = 400;
                    for (int i = 0; i < imax; i++)
                    {
                        uint wi = mem.ReadU32(itemArr + o.ArrayDataOffset + (uint)i * o.ArrayElementSize);
                        if (wi == 0) continue;
                        uint inner = mem.ReadU32(wi + wInner);
                        if (inner == 0 || mem.ReadI32(inner + iType) == 0) continue;
                        liveItems++;
                        if (firstItem == "") firstItem = $"item[{i}] innerType={mem.ReadI32(inner + iType)} pos=({mem.ReadF32(wi + wPos):0},{mem.ReadF32(wi + wPos + 4):0}) noGrab={mem.ReadI32(wi + wNoGrab)}";
                    }
                    sb.AppendLine($"liveWorldItems={liveItems}  {firstItem}");
                    return sb.ToString();
                }
            }
        }

        // diagnostic: verify godmode/rapid-attack field paths read sanely
        public static string CombatTest(int pid)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var o = ResolveCore(runtime, out _);
                using (var mem = new GameMemory())
                {
                    mem.Open(System.Diagnostics.Process.GetProcessById(pid));
                    uint p = LocalPlayer(mem, o);
                    var sb = new StringBuilder();
                    if (p == 0) return "no local player";
                    o.P("immune", out uint im); o.P("immuneTime", out uint imt); o.P("hurtCooldowns", out uint hc);
                    uint hcArr = mem.ReadU32(p + hc);
                    sb.AppendLine($"immune={mem.ReadU32(p + im) & 0xFF} immuneTime={mem.ReadI32(p + imt)} hurtCooldowns@0x{hcArr:X} len={(hcArr != 0 ? mem.ReadU32(hcArr + 4) : 0)}");
                    o.P("forcedGravity", out uint fg); o.P("meleeDamage", out uint md);
                    sb.AppendLine($"forcedGravity={mem.ReadI32(p + fg)} meleeDamage={mem.ReadF32(p + md):0.00}");
                    // held item
                    o.P("inventory", out uint inv); o.P("selectedItem", out uint sel);
                    uint invArr = mem.ReadU32(p + inv); int s = mem.ReadI32(p + sel);
                    uint held = (invArr != 0 && s >= 0 && s < 58) ? mem.ReadU32(invArr + o.ArrayDataOffset + (uint)s * o.ArrayElementSize) : 0;
                    o.I("type", out uint it); o.I("autoReuse", out uint ar); o.I("useTime", out uint ut); o.I("damage", out uint dmg);
                    if (held != 0)
                        sb.AppendLine($"selectedItem={s} heldType={mem.ReadI32(held + it)} autoReuse={mem.ReadU32(held + ar) & 0xFF} useTime={mem.ReadI32(held + ut)} damage={mem.ReadI32(held + dmg)}");
                    else sb.AppendLine($"selectedItem={s} held=null");
                    return sb.ToString();
                }
            }
        }

        // Dump the static name caches (Lang._npcNameCache / _itemNameCache, both LocalizedText[])
        // to stdout: "N <id> <name>" / "I <id> <name>". Read once; names never change.
        public static string DumpNames(int pid)
        {
            using (var dt = DataTarget.AttachToProcess(pid, suspend: true))
            {
                var runtime = dt.ClrVersions.First().CreateRuntime();
                var lang = FindType(runtime, "Terraria.Lang");
                var sb = new StringBuilder();
                DumpCache(runtime, lang, "_npcNameCache", 'N', sb);
                DumpCache(runtime, lang, "_itemNameCache", 'I', sb);
                return sb.ToString();
            }
        }

        static void DumpCache(ClrRuntime runtime, ClrType lang, string field, char tag, StringBuilder sb)
        {
            var f = lang?.GetStaticFieldByName(field);
            if (f == null) return;
            foreach (var dom in runtime.AppDomains)
            {
                ClrObject arr;
                try { arr = f.ReadObject(dom); } catch { continue; }
                if (arr.IsNull) continue;
                var a = arr.AsArray();
                int len = a.Length;
                for (int i = 0; i < len; i++)
                {
                    ClrObject lt;
                    try { lt = a.GetObjectValue(i); } catch { continue; }
                    if (lt.IsNull) continue;
                    ClrObject val;
                    try { val = lt.ReadObjectField("_value"); } catch { continue; }
                    if (val.IsNull || !val.Type.IsString) continue;
                    string s = val.AsString();
                    if (string.IsNullOrEmpty(s)) continue;
                    sb.Append(tag).Append(' ').Append(i).Append(' ').Append(s.Replace('\n', ' ')).Append('\n');
                }
                break;
            }
        }

        // Empirical: with the game RUNNING (no suspend), read natural maxRunSpeed, then hammer-write
        // it and sample read-backs to see if the write sticks / how fast the game resets it.
        public static string MoveTest(int pid)
        {
            var o = Resolve(pid, out _);
            using (var mem = new GameMemory())
            {
                mem.Open(System.Diagnostics.Process.GetProcessById(pid));
                var sb = new StringBuilder();
                bool menu = o.S("gameMenu", out uint gm) && (mem.ReadU32(gm) & 0xFF) != 0;
                uint p = LocalPlayer(mem, o);
                sb.AppendLine($"gameMenu={menu} player=0x{p:X}");
                if (p == 0) return sb.ToString() + "no player (load a world)";
                o.P("maxRunSpeed", out uint mrs); o.P("jumpSpeedBoost", out uint jsb); o.P("waterWalk", out uint ww);
                o.P("accRunSpeed", out uint ars); o.P("runAcceleration", out uint racc); o.P("statLife", out uint sl);
                sb.AppendLine($"offsets: maxRunSpeed=0x{mrs:X} jumpSpeedBoost=0x{jsb:X} waterWalk=0x{ww:X}");
                sb.AppendLine($"sanity: statLife={mem.ReadI32(p + sl)} maxRunSpeed={mem.ReadF32(p + mrs):0.00} accRunSpeed={mem.ReadF32(p + ars):0.00} runAcceleration={mem.ReadF32(p + racc):0.000} jumpSpeedBoost={mem.ReadF32(p + jsb):0.00} waterWalk={mem.ReadU32(p + ww) & 0xFF}");
                // natural values (no writing) over ~200ms
                var nat = new System.Collections.Generic.List<float>();
                for (int i = 0; i < 20; i++) { nat.Add(mem.ReadF32(p + mrs)); System.Threading.Thread.Sleep(10); }
                sb.AppendLine($"natural maxRunSpeed: min={nat.Min():0.00} max={nat.Max():0.00}");
                // hammer-write 15 for ~1s, sample read-back
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int writes = 0, readBack15 = 0, readBackOther = 0; float minSeen = 999, maxSeen = -999;
                while (sw.ElapsedMilliseconds < 1000)
                {
                    mem.WriteF32(p + mrs, 15f); writes++;
                    float v = mem.ReadF32(p + mrs);
                    if (Math.Abs(v - 15f) < 0.5f) readBack15++; else readBackOther++;
                    if (v < minSeen) minSeen = v; if (v > maxSeen) maxSeen = v;
                }
                sb.AppendLine($"hammer: writes={writes} readBack==15:{readBack15} other:{readBackOther} seen[{minSeen:0.0}..{maxSeen:0.0}]");
                mem.WriteF32(p + mrs, nat.Count > 0 ? nat[0] : 3f); // restore
                return sb.ToString();
            }
        }

        static uint LocalPlayer(GameMemory mem, Offsets o)
        {
            if (!o.S("player", out uint pa) || !o.S("myPlayer", out uint ma)) return 0;
            uint arr = mem.ReadU32(pa);
            if (arr == 0) return 0;
            int my = mem.ReadI32(ma);
            if (my < 0 || my > 255) return 0;
            return mem.ReadU32(arr + o.ArrayDataOffset + (uint)my * o.ArrayElementSize);
        }

        static bool TryGetItemTemplate(ClrRuntime runtime, int type, out ulong addr, out uint size)
        {
            addr = 0; size = 0;
            var cs = FindType(runtime, "Terraria.ID.ContentSamples");
            var f = cs?.GetStaticFieldByName("ItemsByType");
            if (f == null) return false;
            foreach (var dom in runtime.AppDomains)
            {
                ClrObject dict;
                try { dict = f.ReadObject(dom); } catch { continue; }
                if (dict.IsNull) continue;
                ClrObject entries = dict.ReadObjectField("entries");
                int count = dict.ReadField<int>("count");
                if (entries.IsNull) continue;
                var arr = entries.AsArray();
                for (int i = 0; i < count; i++)
                {
                    var e = arr.GetStructValue(i);
                    if (e.ReadField<int>("key") != type) continue;
                    var val = e.ReadObjectField("value");
                    if (val.IsNull) return false;
                    addr = val.Address; size = (uint)val.Size; return true;
                }
            }
            return false;
        }

        static ClrType FindType(ClrRuntime runtime, string fullName)
        {
            foreach (var module in runtime.EnumerateModules())
            {
                ClrType t = null;
                try { t = module.GetTypeByName(fullName); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        static uint StaticAddr(ClrRuntime runtime, ClrType type, string field)
        {
            var sf = type.GetStaticFieldByName(field);
            if (sf == null) return 0;
            foreach (var domain in runtime.AppDomains)
            {
                ulong addr = 0;
                try { addr = sf.GetAddress(domain); } catch { }
                if (addr != 0) return (uint)addr;
            }
            return 0;
        }
    }
}
