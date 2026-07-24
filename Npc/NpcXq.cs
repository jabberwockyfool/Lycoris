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

        /// <summary>Decompile the XQ and return the body of RunCmd_Map{funcId} (the NPC's OnTalk), or "" if absent.</summary>
        public static string ExtractFunction(byte[] xq, int funcId)
        {
            string work = Path.Combine(Path.GetTempPath(), "lycoris_xq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string inXq = Path.Combine(work, "in.xq");
                File.WriteAllBytes(inXq, xq);
                RunOrThrow($"-o e -f \"{inXq}\"", work, new StringBuilder());
                string txt = File.Exists(inXq + ".txt") ? inXq + ".txt" : Directory.GetFiles(work, "*.txt").FirstOrDefault();
                if (txt == null) return "";
                return BodyOf(File.ReadAllText(txt), funcId);
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        /// <summary>Decompile the XQ, replace RunCmd_Map{funcId}'s body with <paramref name="newBody"/>, recompile.</summary>
        public static byte[] ReplaceFunction(byte[] xq, int funcId, string newBody, out string log)
        {
            var sb = new StringBuilder();
            string work = Path.Combine(Path.GetTempPath(), "lycoris_xq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string inXq = Path.Combine(work, "in.xq");
                File.WriteAllBytes(inXq, xq);
                RunOrThrow($"-o e -f \"{inXq}\"", work, sb);
                string txt = File.Exists(inXq + ".txt") ? inXq + ".txt" : Directory.GetFiles(work, "*.txt").FirstOrDefault();
                if (txt == null) throw new InvalidOperationException("Décompilation XQ échouée.");

                string code = File.ReadAllText(txt);
                string replaced = ReplaceBody(code, funcId, newBody);
                if (replaced == null) throw new InvalidOperationException($"Fonction RunCmd_Map{funcId} introuvable dans le script.");
                File.WriteAllText(txt, replaced);

                RunOrThrow($"-o c -t xq32 -f \"{txt}\"", work, sb);
                string outXq = File.Exists(txt + ".xq") ? txt + ".xq" : Directory.GetFiles(work, "*.xq").FirstOrDefault(f => !string.Equals(f, inXq, StringComparison.OrdinalIgnoreCase));
                if (outXq == null) throw new InvalidOperationException("Recompilation XQ échouée.");
                log = sb.ToString();
                return File.ReadAllBytes(outXq);
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        // Find "RunCmd_Map{id}(...) { ... }" and return the text between its matching braces.
        private static string BodyOf(string code, int funcId)
        {
            int open = FindBraceAfter(code, funcId, out int _);
            if (open < 0) return "";
            int close = MatchBrace(code, open);
            return close < 0 ? "" : code.Substring(open + 1, close - open - 1).Trim('\r', '\n');
        }

        private static string ReplaceBody(string code, int funcId, string newBody)
        {
            int open = FindBraceAfter(code, funcId, out int _);
            if (open < 0) return null;
            int close = MatchBrace(code, open);
            if (close < 0) return null;
            return code.Substring(0, open + 1) + "\n" + newBody + "\n" + code.Substring(close);
        }

        private static int FindBraceAfter(string code, int funcId, out int defPos)
        {
            defPos = -1;
            foreach (Match m in Regex.Matches(code, $@"RunCmd_Map{funcId}\s*\("))
            {
                // a definition is followed by ") { ... }" (not a "$...()" call-site)
                int brace = code.IndexOf('{', m.Index);
                int semic = code.IndexOf(';', m.Index);
                if (brace >= 0 && (semic < 0 || brace < semic)) { defPos = m.Index; return brace; }
            }
            return -1;
        }

        private static int MatchBrace(string code, int open)
        {
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
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
