using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;

namespace GlbExport
{
    /// <summary>Fila editable por categoria (la enlaza el DataGrid).</summary>
    public sealed class CatRow
    {
        public bool Include { get; set; } = true;
        public string Name { get; set; }
        public string Ost { get; set; }
        public int Count { get; set; }
        public double Ratio { get; set; } = 100;
        public double ErrorCm { get; set; } = 2.0;
        public bool Lock { get; set; }
    }

    public sealed class General
    {
        public double MinTris { get; set; } = 300;
        public bool UseInstances { get; set; } = true;
        public bool Recenter { get; set; } = true;
        public bool IncludeLinks { get; set; } = true;
        public bool OneSided { get; set; } = true;
        public bool VertexColors { get; set; } = true;
        public double CellM { get; set; }
    }

    public sealed class Profile
    {
        public string Preset { get; set; } = Options.DefaultPreset;
        public General General { get; set; } = new General();
        public Dictionary<string, CatRow> Rows { get; set; } = new Dictionary<string, CatRow>();
    }

    /// <summary>Presets, reglas para build.cjs y perfil guardado. Logica pura (sin Revit ni WPF).</summary>
    public static class Options
    {
        public const string DefaultPreset = "Quest (ligero)";
        public const int MinInstances = 20;   // bajo este numero de copias se funde con la geometria fija

        public static readonly string[] PresetNames = { "Quest (ligero)", "Equilibrado", "Sin optimizar" };

        // Piezas con nombre propio: pisos, terreno, escaleras y rampas (se camina sobre ellos), puertas y ventanas (se atraviesan)
        public static readonly string[] SeparateOst =
            { "OST_Floors", "OST_Toposolid", "OST_Stairs", "OST_Ramps", "OST_Doors", "OST_Windows" };
        // Superficies abiertas o planas: se ven por las dos caras
        public static readonly string[] OpenSurfaceOst =
            { "OST_Topography", "OST_Planting", "OST_Entourage", "OST_Roads", "OST_Site" };

        private sealed class Rule
        {
            public double Ratio, ErrorCm; public bool Lock;
            public Rule(double ratio, double errorCm, bool @lock) { Ratio = ratio; ErrorCm = errorCm; Lock = @lock; }
        }

        private static readonly Dictionary<string, Dictionary<string, Rule>> Presets =
            new Dictionary<string, Dictionary<string, Rule>>
            {
                ["Quest (ligero)"] = new Dictionary<string, Rule>
                {
                    ["OST_Doors"] = new Rule(3, 10, false),
                    ["OST_Entourage"] = new Rule(4, 25, false),
                    ["OST_Windows"] = new Rule(30, 2, true),
                    ["OST_VerticalCirculation"] = new Rule(20, 5, false),
                    ["OST_Floors"] = new Rule(30, 0.5, false),
                    ["OST_Walls"] = new Rule(50, 0.5, false),
                    ["OST_Toposolid"] = new Rule(30, 3, false),
                },
                ["Equilibrado"] = new Dictionary<string, Rule>
                {
                    ["OST_Doors"] = new Rule(10, 5, false),
                    ["OST_Entourage"] = new Rule(15, 10, false),
                    ["OST_Windows"] = new Rule(60, 1, true),
                    ["OST_VerticalCirculation"] = new Rule(50, 3, false),
                },
                ["Sin optimizar"] = new Dictionary<string, Rule>(),
            };

        /// <summary>Filas para las categorias de la vista; `saved` (perfil previo, por ost) pisa al preset.</summary>
        public static List<CatRow> MakeRows(string preset, IEnumerable<(string Name, string Ost, int Count)> categories,
                                            Dictionary<string, CatRow> saved = null)
        {
            Presets.TryGetValue(preset ?? "", out var rules);
            var rows = new List<CatRow>();
            foreach (var (name, ost, count) in categories)
            {
                var row = new CatRow { Name = name, Ost = ost, Count = count };
                if (rules != null && rules.TryGetValue(ost, out var r))
                { row.Ratio = r.Ratio; row.ErrorCm = r.ErrorCm; row.Lock = r.Lock; }
                if (saved != null && saved.TryGetValue(ost, out var s))
                { row.Include = s.Include; row.Ratio = s.Ratio; row.ErrorCm = s.ErrorCm; row.Lock = s.Lock; }
                rows.Add(row);
            }
            return rows.OrderByDescending(x => x.Count).ToList();
        }

        public static double Clamp(string text, double lo, double hi, double fallback)
        {
            if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v))
                return fallback;
            return Math.Max(lo, Math.Min(hi, v));
        }

        public static double Clamp(double v, double lo, double hi) =>
            double.IsNaN(v) || double.IsInfinity(v) ? lo : Math.Max(lo, Math.Min(hi, v));

        /// <summary>Celdas: 0 (no partir) o entre 2 y 500 m; lo menor a 2 m se descarta.</summary>
        public static double MinCell(double value)
        {
            var c = Clamp(value, 0, 500);
            return c >= 2 ? c : 0;
        }

        /// <summary>Reglas para build.cjs (--rules). Solo las categorias que se simplifican.</summary>
        public static Dictionary<string, object> BuildRules(IList<CatRow> rows, General g)
        {
            var byCat = new Dictionary<string, object>();
            foreach (var r in rows)
            {
                var ratio = Clamp(r.Ratio, 1, 100);
                if (ratio >= 100 || !r.Include) continue;
                byCat[r.Name] = new Dictionary<string, object>
                {
                    ["ratio"] = ratio / 100.0,
                    ["errorM"] = Clamp(r.ErrorCm, 0.1, 100) / 100.0,
                    ["lockBorder"] = r.Lock,
                };
            }
            return new Dictionary<string, object>
            {
                ["minTris"] = (int)Clamp(g.MinTris, 0, 100000),
                ["minInstances"] = g.UseInstances ? MinInstances : 1_000_000_000,
                ["mergeStatics"] = true,
                ["keepSeparate"] = rows.Where(r => SeparateOst.Contains(r.Ost)).Select(r => r.Name).ToList(),
                ["recenter"] = g.Recenter,
                ["doubleSidedAll"] = !g.OneSided,
                ["doubleSided"] = rows.Where(r => OpenSurfaceOst.Contains(r.Ost)).Select(r => r.Name).ToList(),
                ["vertexColors"] = g.VertexColors,
                ["cellM"] = MinCell(g.CellM),
                ["byCategory"] = byCat,
            };
        }

        public static HashSet<string> ExcludedNames(IEnumerable<CatRow> rows) =>
            new HashSet<string>(rows.Where(r => !r.Include).Select(r => r.Name));

        public static string ProfilePath()
        {
            var baseDir = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(baseDir, "Archiplan", "GlbExport", "last.profile");
        }

        private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string Bit(bool b) => b ? "1" : "0";

        /// <summary>Perfil en texto plano (clave=valor): sin dependencias de JSON dentro de Revit.</summary>
        public static void SaveProfile(string preset, General g, IEnumerable<CatRow> rows, string path = null)
        {
            path = path ?? ProfilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.AppendLine("preset=" + preset);
            sb.AppendLine("minTris=" + Inv(g.MinTris));
            sb.AppendLine("useInstances=" + Bit(g.UseInstances));
            sb.AppendLine("recenter=" + Bit(g.Recenter));
            sb.AppendLine("includeLinks=" + Bit(g.IncludeLinks));
            sb.AppendLine("oneSided=" + Bit(g.OneSided));
            sb.AppendLine("vertexColors=" + Bit(g.VertexColors));
            sb.AppendLine("cellM=" + Inv(g.CellM));
            foreach (var r in rows)
                sb.AppendLine($"row.{r.Ost}={Bit(r.Include)};{Inv(r.Ratio)};{Inv(r.ErrorCm)};{Bit(r.Lock)}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Perfil previo o valores por defecto si no existe o esta danado.</summary>
        public static Profile LoadProfile(string path = null)
        {
            var p = new Profile();
            string[] lines;
            try { lines = File.ReadAllLines(path ?? ProfilePath()); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return p; }
            var g = p.General;
            foreach (var line in lines)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "preset": if (PresetNames.Contains(val)) p.Preset = val; break;
                    case "minTris": g.MinTris = Clamp(val, 0, 100000, g.MinTris); break;
                    case "useInstances": g.UseInstances = val == "1"; break;
                    case "recenter": g.Recenter = val == "1"; break;
                    case "includeLinks": g.IncludeLinks = val == "1"; break;
                    case "oneSided": g.OneSided = val == "1"; break;
                    case "vertexColors": g.VertexColors = val == "1"; break;
                    case "cellM": g.CellM = Clamp(val, 0, 500, 0); break;
                    default:
                        if (key.StartsWith("row."))
                        {
                            var f = val.Split(';');
                            if (f.Length == 4)
                                p.Rows[key.Substring(4)] = new CatRow
                                {
                                    Include = f[0] == "1", Ratio = Clamp(f[1], 1, 100, 100),
                                    ErrorCm = Clamp(f[2], 0.1, 100, 2), Lock = f[3] == "1",
                                };
                        }
                        break;
                }
            }
            return p;
        }
    }
}
