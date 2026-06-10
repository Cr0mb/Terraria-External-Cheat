using System;
using System.Linq;
using TerrariaTrainer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Headless self-test: verifies in-process ClrMD resolution works inside the single-file
        // bundle. Writes result to stdout and a temp file, then exits. (GVoid.exe --selftest)
        if (args.Length > 0 && args[0] == "--selftest")
        {
            string res;
            try
            {
                var proc = System.Diagnostics.Process.GetProcessesByName("Terraria").FirstOrDefault(p => !p.HasExited);
                if (proc == null) res = "NO TERRARIA";
                else { var o = Resolver.Resolve(proc.Id, out _); res = $"OK pid={proc.Id} statics={o.Statics.Count} player={o.Player.Count} npc={o.Npc.Count}"; }
            }
            catch (Exception ex) { res = "FAIL " + ex.GetType().Name + ": " + ex.Message; }
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gvoid_selftest.txt"), res); } catch { }
            Console.WriteLine(res);
            return;
        }

        using (var overlay = new MenuOverlay())
        {
            overlay.Run().GetAwaiter().GetResult();
        }
    }
}
