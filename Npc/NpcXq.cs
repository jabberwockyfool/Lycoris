using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lycoris.Npc
{
    /// <summary>
    /// Wraps the external `xtractquery` tool (must be on PATH) to inject an OnTalk function into a map .xq.
    /// Decompiles the XQ32 script, appends a fresh RunCmd_Map function, and recompiles — exactly the
    /// contract NPCMake uses. Only this XQ step is external; everything else is done in-process.
    /// </summary>
    public static class NpcXq
    {
        public static bool IsAvailable()
        {
            try
            {
                var p = Start("-h", null, out _);
                if (p == null) return false;
                p.WaitForExit(8000);
                return p.HasExited && p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Append <paramref name="onTalk"/> as a new RunCmd_Map function to the given XQ32 bytes and return
        /// the recompiled bytes. <paramref name="funcId"/> is the id of the new function (to link in the trigger).
        /// </summary>
        public static byte[] AddOnTalkFunction(byte[] xq, string onTalk, out int funcId, out string log)
        {
            funcId = 0;
            var sb = new StringBuilder();
            string work = Path.Combine(Path.GetTempPath(), "lycoris_xq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string inXq = Path.Combine(work, "in.xq");
                File.WriteAllBytes(inXq, xq);

                RunOrThrow($"-o e -f \"{inXq}\"", work, sb);
                string txt = File.Exists(inXq + ".txt") ? inXq + ".txt"
                    : Directory.GetFiles(work, "*.txt").FirstOrDefault();
                if (txt == null || !File.Exists(txt))
                    throw new InvalidOperationException("xtractquery n'a pas produit de fichier décompilé (.txt).");

                string code = File.ReadAllText(txt);
                funcId = NextRunCmdId(code);
                code += $"\n\nRunCmd_Map{funcId}()\n{{\n{onTalk}\n}}\n";
                File.WriteAllText(txt, code);

                RunOrThrow($"-o c -t xq32 -f \"{txt}\"", work, sb);
                string outXq = File.Exists(txt + ".xq") ? txt + ".xq"
                    : Directory.GetFiles(work, "*.xq").FirstOrDefault(f => !string.Equals(f, inXq, StringComparison.OrdinalIgnoreCase));
                if (outXq == null || !File.Exists(outXq))
                    throw new InvalidOperationException("xtractquery n'a pas produit de fichier recompilé (.xq).");

                log = sb.ToString();
                return File.ReadAllBytes(outXq);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { /* best effort */ }
            }
        }

        private static int NextRunCmdId(string code)
        {
            int max = -1;
            foreach (Match m in Regex.Matches(code, @"RunCmd_Map(\d+)"))
                if (int.TryParse(m.Groups[1].Value, out int id) && id > max) max = id;
            return max + 1;
        }

        private static Process Start(string args, string workdir, out Process proc)
        {
            var psi = new ProcessStartInfo("xtractquery", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (workdir != null) psi.WorkingDirectory = workdir;
            proc = Process.Start(psi);
            return proc;
        }

        private static void RunOrThrow(string args, string workdir, StringBuilder sb)
        {
            var p = Start(args, workdir, out _);
            if (p == null) throw new InvalidOperationException("Impossible de lancer xtractquery (absent du PATH ?).");
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            sb.Append("$ xtractquery ").Append(args).Append('\n');
            if (so.Length > 0) sb.Append(so).Append('\n');
            if (se.Length > 0) sb.Append(se).Append('\n');
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"xtractquery a échoué (code {p.ExitCode}).\n{so}\n{se}");
        }
    }
}
