using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Lycoris
{
    /// <summary>
    /// Reads/writes a YWML mod manifest (ywml.json: Name / Author / Version). The manifest sits at the mod
    /// root, beside the "include" folder, so it's searched in the loaded folder and a couple of parents.
    /// </summary>
    public static class Ywml
    {
        /// <summary>The mod's display name from its ywml.json, or null if there's no manifest.</summary>
        public static string FindName(string modFolder) => Field(FindManifest(modFolder), "Name");

        /// <summary>Path to the mod's ywml.json (searching the folder and up to two parents), or null.</summary>
        public static string FindManifest(string modFolder)
        {
            foreach (var dir in Candidates(modFolder))
            {
                if (dir == null) continue;
                string p = Path.Combine(dir, "ywml.json");
                if (File.Exists(p)) return p;   // Windows FS is case-insensitive (matches YWML.json too)
            }
            return null;
        }

        /// <summary>Create/overwrite a ywml.json in <paramref name="folder"/> with the given metadata.</summary>
        public static void Write(string folder, string name, string author, string version)
        {
            var sb = new StringBuilder();
            sb.Append("{\n")
              .Append("    \"Name\": \"").Append(Esc(name)).Append("\",\n")
              .Append("    \"Author\": \"").Append(Esc(author ?? "")).Append("\",\n")
              .Append("    \"Version\": \"").Append(Esc(string.IsNullOrEmpty(version) ? "v0.0.0" : version)).Append("\"\n")
              .Append("}\n");
            File.WriteAllText(Path.Combine(folder, "ywml.json"), sb.ToString(), new UTF8Encoding(false));
        }

        private static IEnumerable<string> Candidates(string modFolder)
        {
            if (string.IsNullOrEmpty(modFolder)) yield break;
            string a = modFolder.TrimEnd('\\', '/');
            yield return a;
            string b = Path.GetDirectoryName(a);
            yield return b;
            yield return b != null ? Path.GetDirectoryName(b) : null;
        }

        private static string Field(string manifestPath, string key)
        {
            if (manifestPath == null) return null;
            try
            {
                string json = File.ReadAllText(manifestPath);
                var m = System.Text.RegularExpressions.Regex.Match(
                    json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!m.Success) return null;
                string v = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch { return null; }
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>One Lycoris project: a named mod folder (created or imported) + its optional reference.</summary>
    public sealed class Project
    {
        public string Name;
        public string ModFolder;
        public string ReferenceFolder;   // null => use the app's auto-detected reference
        public long LastOpenedTicks;

        public DateTime LastOpened => new DateTime(LastOpenedTicks, DateTimeKind.Utc);
        public bool Exists => !string.IsNullOrEmpty(ModFolder) && Directory.Exists(ModFolder);
        public override string ToString() => Name;
    }

    /// <summary>
    /// Persists the list of recent projects as a small TSV under %LocalAppData%\Lycoris so the launcher can
    /// restore them across sessions. Fields are tab-separated (paths never contain tabs); most-recent first.
    /// </summary>
    public static class ProjectStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lycoris");
        private static string RegistryPath => Path.Combine(Dir, "projects.tsv");

        public static List<Project> Load()
        {
            var list = new List<Project>();
            try
            {
                if (!File.Exists(RegistryPath)) return list;
                foreach (var line in File.ReadAllLines(RegistryPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = line.Split('\t');
                    if (f.Length < 2) continue;
                    list.Add(new Project
                    {
                        Name = f[0],
                        ModFolder = f[1],
                        ReferenceFolder = f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                        LastOpenedTicks = f.Length > 3 && long.TryParse(f[3], out long t) ? t : 0,
                    });
                }
            }
            catch { /* corrupt / unreadable registry: start empty */ }
            return list.OrderByDescending(p => p.LastOpenedTicks).ToList();
        }

        public static void Save(IEnumerable<Project> projects)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var p in projects.OrderByDescending(p => p.LastOpenedTicks))
                    sb.Append(Clean(p.Name)).Append('\t')
                      .Append(p.ModFolder ?? "").Append('\t')
                      .Append(p.ReferenceFolder ?? "").Append('\t')
                      .Append(p.LastOpenedTicks).Append('\n');
                File.WriteAllText(RegistryPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* best-effort persistence */ }
        }

        /// <summary>Insert or update a project by mod folder, stamp it as just-opened, and persist the list.</summary>
        public static List<Project> Touch(string name, string modFolder, string referenceFolder)
        {
            var list = Load();
            var existing = list.FirstOrDefault(p =>
                string.Equals(p.ModFolder, modFolder, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new Project { ModFolder = modFolder };
                list.Add(existing);
            }
            existing.Name = string.IsNullOrWhiteSpace(name) ? DefaultName(modFolder) : name.Trim();
            existing.ReferenceFolder = referenceFolder;
            existing.LastOpenedTicks = DateTime.UtcNow.Ticks;
            Save(list);
            return list.OrderByDescending(p => p.LastOpenedTicks).ToList();
        }

        public static List<Project> Remove(string modFolder)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p.ModFolder, modFolder, StringComparison.OrdinalIgnoreCase));
            Save(list);
            return list;
        }

        public static string DefaultName(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "Mod";
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            // A bare "include" folder isn't a helpful name — use its parent.
            if (name.Equals("include", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? "");
            return string.IsNullOrEmpty(name) ? "Mod" : name;
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
