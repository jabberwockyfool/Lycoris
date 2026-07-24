using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>One wild yo-kai instance (ENCOUNT_CHARA): a yo-kai ParamID + a level. Referenced by tables via offset.</summary>
    public sealed class EncChara : INotifyPropertyChanged
    {
        internal T2bEntry Entry;
        private int _paramId;
        private int? _level;
        public int ParamId { get => _paramId; set { if (_paramId != value) { _paramId = value; IsDirty = true; Raise(nameof(ParamHex)); } } }
        public int? Level { get => _level; set { if (_level != value) { _level = value; IsDirty = true; Raise(nameof(Level)); } } }
        public bool IsDirty;
        public string YokaiName { get; set; }
        public string IconFile { get; set; }
        public string ParamHex => $"0x{unchecked((uint)_paramId):X8}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>One encounter/"Battle" (ENCOUNT_TABLE): up to 6 offsets into the ENCOUNT_CHARA list (-1 = empty).</summary>
    public sealed class EncTable
    {
        internal T2bEntry Entry;
        public int EncountId;
        public int Index;
        public readonly int[] Offsets = new int[6];
        public string Label => $"Battle {Index}  (0x{unchecked((uint)EncountId):X8})";
        public override string ToString() => Label;
    }

    /// <summary>The parsed encounter file (extracted from a map's .pck) + everything needed to repack it.</summary>
    public sealed class EncounterSet
    {
        public List<XpckFile> PckFiles;
        public string EncFileName;
        public T2bFile EncT2b;
        public List<EncTable> Tables = new List<EncTable>();
        public List<EncChara> Charas = new List<EncChara>();
    }

    /// <summary>Read/edit/repack the wild-encounter file inside a map's .pck (ENCOUNT_TABLE + ENCOUNT_CHARA).</summary>
    public static class Encounters
    {
        public static EncounterSet Load(string pckPath, YokaiDatabase db)
        {
            var pck = Xpck.Read(File.ReadAllBytes(pckPath));
            var encFile = pck.FirstOrDefault(x =>
                x.Name.IndexOf("_enc_", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                x.Name.IndexOf("pos", System.StringComparison.OrdinalIgnoreCase) < 0);
            if (encFile == null) return null;

            var t2b = T2bReader.Read(encFile.Data);
            var set = new EncounterSet { PckFiles = pck, EncFileName = encFile.Name, EncT2b = t2b };

            foreach (var e in t2b.Records("ENCOUNT_CHARA"))
            {
                var c = new EncChara { Entry = e, ParamId = e.GetInt(0) ?? 0, Level = e.GetInt(1), IsDirty = false };
                Resolve(c, db);
                set.Charas.Add(c);
            }
            int idx = 0;
            foreach (var e in t2b.Records("ENCOUNT_TABLE"))
            {
                var t = new EncTable { Entry = e, EncountId = e.GetInt(0) ?? 0, Index = idx++ };
                for (int i = 0; i < 6; i++) t.Offsets[i] = e.GetInt(1 + i) ?? -1;
                set.Tables.Add(t);
            }
            return set;
        }

        public static void Resolve(EncChara c, YokaiDatabase db)
        {
            var y = db.Yokai.FirstOrDefault(k => k.ParamHash == c.ParamId);
            c.YokaiName = y != null ? y.DisplayName : c.ParamHex;
            c.IconFile = y?.IconFile;
        }

        /// <summary>Append a new ENCOUNT_CHARA and return it; its offset is the new last index in Charas.</summary>
        public static EncChara AddChara(EncounterSet set, int paramId, int level, YokaiDatabase db)
        {
            var tpl = set.EncT2b.Records("ENCOUNT_CHARA").FirstOrDefault();
            if (tpl == null) return null;
            var e = tpl.Clone();
            SetVal(e, 0, paramId);
            SetVal(e, 1, level);
            InsertIntoGroup(set.EncT2b, "ENCOUNT_CHARA_BEGIN", "ENCOUNT_CHARA_END", e);
            var c = new EncChara { Entry = e, ParamId = paramId, Level = level };
            Resolve(c, db);
            set.Charas.Add(c);
            return c;
        }

        /// <summary>Write all edits back into the CfgBin, repack the .pck and save it to <paramref name="outPckPath"/>.</summary>
        public static void Save(EncounterSet set, string outPckPath)
        {
            foreach (var c in set.Charas)
            {
                SetVal(c.Entry, 0, c.ParamId);
                SetVal(c.Entry, 1, c.Level ?? 0);
            }
            foreach (var t in set.Tables)
                for (int i = 0; i < 6; i++) SetVal(t.Entry, 1 + i, t.Offsets[i]);

            Xpck.AddOrReplace(set.PckFiles, set.EncFileName, T2bWriter.Write(set.EncT2b));
            byte[] bytes = Xpck.Write(set.PckFiles);
            Directory.CreateDirectory(Path.GetDirectoryName(outPckPath));
            File.WriteAllBytes(outPckPath, bytes);
        }

        private static void SetVal(T2bEntry e, int i, int v)
        {
            if (i < 0 || i >= e.Values.Count) return;
            e.Values[i].Type = VT.Integer;
            e.Values[i].Value = v;
        }

        private static void InsertIntoGroup(T2bFile file, string begin, string end, T2bEntry entry)
        {
            int endIdx = file.Entries.FindIndex(x => x.Name == end);
            if (endIdx < 0) { file.Entries.Add(entry); return; }
            file.Entries.Insert(endIdx, entry);
            var b = file.Entries.FirstOrDefault(x => x.Name == begin);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + 1;
        }
    }
}
