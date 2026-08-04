using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Finds and plays Yo-kai Watch character voice clips (snd/product/stream/pv_&lt;model&gt;_NN_en.dspadpcm.bcstm)
    /// so the Dialogue editor can preview a voice before its &lt;PV#voice_model_NN&gt; tag is inserted. Decodes the
    /// 3DS BCSTM to PCM WAV (see <see cref="Bcstm"/>) and plays it with SoundPlayer. The reference (cfg/snd) is
    /// searched first, then the mod (custom voices).
    /// </summary>
    internal static class VoiceAudio
    {
        private static List<string> _dirs;
        private static SoundPlayer _player;

        private static List<string> Dirs(YokaiDatabase db)
        {
            if (_dirs != null) return _dirs;
            _dirs = new List<string>();
            foreach (var root in new[] { db?.ReferenceFolder, db?.ModFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    Path.Combine(root, "snd", "product", "stream"),               // user's reference layout (cfg/snd/…)
                    Path.Combine(root, "include", "data", "snd", "product", "stream"),
                    Path.Combine(root, "data", "snd", "product", "stream") })
                    if (Directory.Exists(cand) && !_dirs.Contains(cand)) { _dirs.Add(cand); break; }
            }
            return _dirs;
        }

        /// <summary>Reset the cached snd folders (call when the loaded mod/reference changes).</summary>
        public static void Reset() { _dirs = null; }

        /// <summary>The voice numbers (NN) available for a model, e.g. "06","10",… (empty if the model has none).</summary>
        public static List<string> List(YokaiDatabase db, string model)
        {
            var nums = new List<string>();
            if (string.IsNullOrEmpty(model)) return nums;
            var seen = new HashSet<string>();
            // Files are voice_<model>_<code>_en.dspadpcm.bcstm; <code> = digits + optional letter/prefix (e.g. 06,
            // 01a, s12). The <PV#voice_<model>_<code>> tag matches this. (Older dumps used pv_… — also accepted.)
            foreach (var dir in Dirs(db))
                foreach (var prefix in new[] { "voice_" + model + "_", "pv_" + model + "_" })
                    foreach (var f in Directory.EnumerateFiles(dir, prefix + "*.bcstm"))
                    {
                        string code = VoiceCode(Path.GetFileName(f), prefix);
                        if (code.Length > 0 && seen.Add(code)) nums.Add(code);
                    }
            nums.Sort(StringComparer.Ordinal);
            return nums;
        }

        // Strip the "<prefix>" and the "_en.dspadpcm.bcstm" (or ".dspadpcm.bcstm"/".bcstm") tail → the voice code.
        private static string VoiceCode(string fileName, string prefix)
        {
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            string code = fileName.Substring(prefix.Length);
            foreach (var suf in new[] { "_en.dspadpcm.bcstm", ".dspadpcm.bcstm", ".bcstm" })
                if (code.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { code = code.Substring(0, code.Length - suf.Length); break; }
            return code;
        }

        /// <summary>Locate the .bcstm for a voice (reference first, then mod), or null.</summary>
        public static string FindFile(YokaiDatabase db, string model, string nn)
        {
            foreach (var dir in Dirs(db))
                foreach (var prefix in new[] { "voice_" + model + "_" + nn, "pv_" + model + "_" + nn })
                {
                    var hit = Directory.EnumerateFiles(dir, prefix + "_en*.bcstm").FirstOrDefault()
                              ?? Directory.EnumerateFiles(dir, prefix + ".bcstm").FirstOrDefault()
                              ?? Directory.EnumerateFiles(dir, prefix + ".dspadpcm.bcstm").FirstOrDefault();
                    if (hit != null) return hit;
                }
            return null;
        }

        /// <summary>Decode and play a voice clip. Throws with a clear message if the file is missing/unsupported.</summary>
        public static void Play(YokaiDatabase db, string model, string nn)
        {
            string file = FindFile(db, model, nn);
            if (file == null) throw new FileNotFoundException($"No voice voice_{model}_{nn} found in snd/product/stream.");
            byte[] wav = Bcstm.ToWav(File.ReadAllBytes(file));
            Stop();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();   // async playback
        }

        public static void Stop()
        {
            try { _player?.Stop(); _player?.Dispose(); } catch { }
            _player = null;
        }
    }
}
