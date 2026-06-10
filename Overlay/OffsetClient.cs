using System;
using System.Linq;

namespace TerrariaTrainer
{
    /// <summary>
    /// In-process offset/name/spawn resolution. Because the overlay is built x86, ClrMD can attach
    /// directly to 32-bit Terraria — no separate helper exe is needed (single self-contained build).
    /// </summary>
    public static class OffsetClient
    {
        static int FindTerraria()
        {
            var p = System.Diagnostics.Process.GetProcessesByName("Terraria").FirstOrDefault(x => !x.HasExited);
            return p?.Id ?? 0;
        }

        public static Offsets Fetch(out int pid, out string error)
        {
            error = null;
            pid = FindTerraria();
            if (pid == 0) { error = "Terraria.exe is not running."; return null; }
            try { return Resolver.Resolve(pid, out _); }
            catch (Exception ex) { error = ex.Message.Replace('\n', ' ').Replace('\r', ' '); return null; }
        }

        // Resolves entity names from the game's localization tables (Lang caches) in-process.
        public static void FetchNames(System.Collections.Generic.Dictionary<int, string> npc, System.Collections.Generic.Dictionary<int, string> item)
        {
            int pid = FindTerraria();
            if (pid == 0) return;
            string outp;
            try { outp = Resolver.DumpNames(pid); } catch { return; }
            foreach (var line in outp.Split('\n'))
            {
                if (line.Length < 5) continue;
                char t = line[0];
                int s1 = line.IndexOf(' ', 2);
                if (s1 < 0) continue;
                if (!int.TryParse(line.Substring(2, s1 - 2), out int id)) continue;
                string name = line.Substring(s1 + 1).TrimEnd('\r');
                if (t == 'N') npc[id] = name; else if (t == 'I') item[id] = name;
            }
        }

        public static string Spawn(int type, int stack, int slot)
        {
            int pid = FindTerraria();
            if (pid == 0) return "ERR Terraria not running";
            try { return Resolver.Spawn(pid, type, stack, slot); }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }
    }
}
