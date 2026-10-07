using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GlbExport
{
    /// <summary>Busca Node.js y las dependencias, y ejecuta el conversor GlbExport/src/build.cjs.</summary>
    public static class NodeRunner
    {
        public static string DepsDir =>
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "ArchiGlb", "node_modules");

        public static string FindNode()
        {
            var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (var v in new[] { "ProgramFiles", "ProgramW6432" })
            {
                var b = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(b)) dirs.Add(Path.Combine(b, "nodejs"));
            }
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local)) dirs.Add(Path.Combine(local, "Programs", "nodejs"));
            foreach (var d in dirs)
            {
                try
                {
                    var exe = Path.Combine(d.Trim('"'), "node.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>build.cjs vive en GlbExport/ junto a la carpeta del add-in (o un nivel arriba).</summary>
        public static string FindBuildScript()
        {
            var dir = Path.GetDirectoryName(typeof(NodeRunner).Assembly.Location);
            for (int i = 0; i < 4 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                var p = Path.Combine(dir, "GlbExport", "src", "build.cjs");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string Convert(string node, string build, string sceneDir, string outGlb,
                                     Dictionary<string, object> rules)
        {
            var rulesPath = Path.Combine(sceneDir, "rules.json");
            File.WriteAllText(rulesPath, JsonWriter.Serialize(rules));
            var psi = new ProcessStartInfo(node, $"\"{build}\" \"{sceneDir}\" \"{outGlb}\" --rules \"{rulesPath}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();   // evita bloqueo por buffer lleno
                var so = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var se = errTask.Result;
                if (p.ExitCode != 0)
                {
                    var msg = string.IsNullOrWhiteSpace(se) ? so : se;
                    throw new InvalidOperationException("Fallo la conversion a GLB:\n" +
                        (msg.Length > 800 ? msg.Substring(msg.Length - 800) : msg));
                }
                return so.Trim();
            }
        }
    }
}
