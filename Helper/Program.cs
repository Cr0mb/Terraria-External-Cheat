using System;
using System.Diagnostics;
using System.Linq;
using TerrariaTrainer;

// x86 helper.
//   (no args)                 -> resolve offsets, print "OK <pid>" + serialized table
//   spawn <type> <stack> <slot> -> copy a template Item into an inventory slot (prints OK/ERR)
class Program
{
    static int Main(string[] args)
    {
        try
        {
            var proc = Process.GetProcessesByName("Terraria").FirstOrDefault(p => !p.HasExited);
            if (proc == null) { Console.WriteLine("ERR Terraria.exe is not running"); return 1; }

            if (args.Length >= 1 && args[0] == "movetest")
            {
                Console.WriteLine(Resolver.MoveTest(proc.Id));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "names")
            {
                Console.Write(Resolver.DumpNames(proc.Id));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "combattest")
            {
                Console.WriteLine(Resolver.CombatTest(proc.Id));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "auratest")
            {
                Console.WriteLine(Resolver.AuraTest(proc.Id));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "esptest")
            {
                Console.WriteLine(Resolver.EspTest(proc.Id));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "info")
            {
                Console.WriteLine(Resolver.ItemInfo(proc.Id, int.Parse(args[1])));
                return 0;
            }

            if (args.Length >= 1 && args[0] == "spawn")
            {
                int type = int.Parse(args[1]), stack = int.Parse(args[2]), slot = int.Parse(args[3]);
                string r = Resolver.Spawn(proc.Id, type, stack, slot);
                Console.WriteLine(r);
                return r.StartsWith("OK") ? 0 : 2;
            }

            var o = Resolver.Resolve(proc.Id, out _);
            Console.WriteLine("OK " + proc.Id);
            Console.Write(o.Serialize());
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }
}
