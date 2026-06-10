using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TerrariaTrainer
{
    /// <summary>
    /// Resolved memory layout for one running Terraria session. Produced (x86, via ClrMD)
    /// by the OffsetDump helper and consumed (x64) by the overlay. Serializes to a tiny
    /// line-based text format so no JSON dependency is needed on .NET Framework.
    ///   S name hexAddr   - absolute address of a Terraria.Main static slot
    ///   P name hexOff    - relative field offset inside a Terraria.Player object
    ///   I name hexOff    - relative field offset inside a Terraria.Item object
    ///   ADO n / AES n    - array data offset / element size (x86)
    /// </summary>
    public sealed class Offsets
    {
        public readonly Dictionary<string, uint> Statics = new Dictionary<string, uint>(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Player = new Dictionary<string, uint>(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Item = new Dictionary<string, uint>(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Npc = new Dictionary<string, uint>(StringComparer.Ordinal);
        public readonly Dictionary<string, uint> Misc = new Dictionary<string, uint>(StringComparer.Ordinal);

        public uint ArrayDataOffset = 8;   // x86 SZArray: [MethodTable(4)][Length(4)][data...]
        public uint ArrayElementSize = 4;  // reference element size on x86

        public bool S(string n, out uint v) => Statics.TryGetValue(n, out v);
        public bool P(string n, out uint v) => Player.TryGetValue(n, out v);
        public bool I(string n, out uint v) => Item.TryGetValue(n, out v);
        public bool N(string n, out uint v) => Npc.TryGetValue(n, out v);
        public bool X(string n, out uint v) => Misc.TryGetValue(n, out v);

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("ADO ").Append(ArrayDataOffset).Append('\n');
            sb.Append("AES ").Append(ArrayElementSize).Append('\n');
            foreach (var kv in Statics) sb.Append("S ").Append(kv.Key).Append(' ').Append(kv.Value.ToString("X")).Append('\n');
            foreach (var kv in Player) sb.Append("P ").Append(kv.Key).Append(' ').Append(kv.Value.ToString("X")).Append('\n');
            foreach (var kv in Item) sb.Append("I ").Append(kv.Key).Append(' ').Append(kv.Value.ToString("X")).Append('\n');
            foreach (var kv in Npc) sb.Append("N ").Append(kv.Key).Append(' ').Append(kv.Value.ToString("X")).Append('\n');
            foreach (var kv in Misc) sb.Append("X ").Append(kv.Key).Append(' ').Append(kv.Value.ToString("X")).Append('\n');
            return sb.ToString();
        }

        public static Offsets Parse(string text)
        {
            var o = new Offsets();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(' ');
                switch (parts[0])
                {
                    case "ADO": o.ArrayDataOffset = uint.Parse(parts[1]); break;
                    case "AES": o.ArrayElementSize = uint.Parse(parts[1]); break;
                    case "S": o.Statics[parts[1]] = uint.Parse(parts[2], NumberStyles.HexNumber); break;
                    case "P": o.Player[parts[1]] = uint.Parse(parts[2], NumberStyles.HexNumber); break;
                    case "I": o.Item[parts[1]] = uint.Parse(parts[2], NumberStyles.HexNumber); break;
                    case "N": o.Npc[parts[1]] = uint.Parse(parts[2], NumberStyles.HexNumber); break;
                    case "X": o.Misc[parts[1]] = uint.Parse(parts[2], NumberStyles.HexNumber); break;
                }
            }
            return o;
        }
    }
}
