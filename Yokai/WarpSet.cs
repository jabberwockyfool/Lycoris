using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;
using Lycoris.Npc;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>
    /// The Mirapo/warp table. The shipping file is <b>warp_config_0.01b.cfg.bin</b> (57 warps, one per
    /// warp_&lt;mapid&gt;.xi preview); an older warp_config.cfg.bin (28) uses a slightly different field order.
    /// A WARP_INFO in the WARP_INFO_LIST group is one warp destination. Two field layouts, auto-detected:
    /// <list type="bullet">
    /// <item>NEW (field[0] is a STRING "warp_&lt;mapid&gt;"): [1]=CRC32(mapid), [2]=hash/0, [3..5]=spawn X/Y/Z,
    /// [6]=rotation, [7]=blob carrying CRC32("warp_"+mapid) at offset 19, [8]=constant blob, [9]=condition/0.</item>
    /// <item>OLD (field[0] is the CRC32("warp_"+mapid) int): [1]=CRC32(mapid), [2]=idx, [3]=mapid str, [4..6]=X/Y/Z,
    /// [7]=rotation, [8]=blob, [9]=0.</item>
    /// </list>
    /// Adding clones a record, sets the hashes/coords, and remaps the id blob with <see cref="YwCond"/>.
    /// </summary>
    public sealed class WarpEntry : INotifyPropertyChanged
    {
        public T2bEntry Entry;
        public int Field0;                 // CRC32("warp_"+mapid) (the warp-point id)
        public int MapHash;                // CRC32(mapid) = field[1]
        public string MapId;               // destination map id
        public string MapName;             // display name from system_text

        private double _x, _y, _z; private int _rot;
        public double X { get => _x; set { _x = value; Raise(nameof(X)); } }
        public double Y { get => _y; set { _y = value; Raise(nameof(Y)); } }
        public double Z { get => _z; set { _z = value; Raise(nameof(Z)); } }
        public int Rotation { get => _rot; set { _rot = value; Raise(nameof(Rotation)); } }

        public string HashHex => $"0x{unchecked((uint)MapHash):X8}";
        public string Display => (MapName ?? MapId ?? HashHex) + (MapId != null ? $"   ({MapId})" : "");
        public override string ToString() => Display;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class WarpSet
    {
        public T2bFile File;
        public string ConfigPath;
        public readonly List<WarpEntry> Warps = new List<WarpEntry>();
        public IReadOnlyDictionary<int, string> NamesByHash => _names;

        private Dictionary<int, string> _names = new Dictionary<int, string>();
        private Dictionary<int, string> _mapIdByHash = new Dictionary<int, string>();

        // Field indices for the detected layout.
        private bool _newFmt;               // field[0] is a "warp_<mapid>" string
        private int _fx, _fy, _fz, _frot, _fBlob;

        private const string Rec = "WARP_INFO", Beg = "WARP_INFO_LIST_BEG", End = "WARP_INFO_LIST_END";

        public static int WarpId(string mapId) => unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes("warp_" + mapId)));
        public static int MapIdHash(string mapId) => unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(mapId)));

        /// <summary>Strip a warp-point suffix (_02, _03…) to the real map id: several warps can share one map,
        /// distinguished only by field[0]/the warp id — field[1] (the map to load) is always the BASE map hash.
        /// e.g. "t402g00_02" → "t402g00" (which has a real map folder; the suffixed id does not).</summary>
        public static string BaseMapId(string mapId) =>
            System.Text.RegularExpressions.Regex.Replace(mapId ?? "", @"_\d+$", "");

        /// <param name="mapIdSources">directories to reverse-resolve a map hash → its id (warp_img/*.xi names and
        /// map/&lt;mapid&gt;/ folders) — only needed for the OLD layout.</param>
        public static WarpSet Load(string configPath, string systemTextPath, IEnumerable<string> mapIdSources)
        {
            var ws = new WarpSet { ConfigPath = configPath, File = T2bReader.ReadFile(configPath) };
            ws.BuildNameMap(systemTextPath);
            ws.BuildMapIdMap(mapIdSources);

            var tpl = ws.File.Records(Rec).FirstOrDefault();
            ws.DetectLayout(tpl);

            foreach (var e in ws.File.Records(Rec))
            {
                int mapHash = e.GetInt(1) ?? 0;
                string mapId = ws.ReadMapId(e, mapHash);
                ws.Warps.Add(new WarpEntry
                {
                    Entry = e,
                    Field0 = mapId != null ? WarpId(mapId) : (e.GetInt(0) ?? 0),
                    MapHash = mapHash,
                    MapId = mapId,
                    MapName = ws._names.TryGetValue(mapHash, out var nm) ? nm : null,
                    X = Num(e, ws._fx), Y = Num(e, ws._fy), Z = Num(e, ws._fz), Rotation = (int)Num(e, ws._frot),
                });
            }
            return ws;
        }

        private void DetectLayout(T2bEntry tpl)
        {
            _newFmt = tpl != null && tpl.Values.Count > 0 && tpl.Values[0].Type == VT.String;
            if (_newFmt) { _fx = 3; _fy = 4; _fz = 5; _frot = 6; _fBlob = 7; }
            else { _fx = 4; _fy = 5; _fz = 6; _frot = 7; _fBlob = 8; }
        }

        private string ReadMapId(T2bEntry e, int mapHash)
        {
            if (_newFmt && e.Values.Count > 0 && e.Values[0].Type == VT.String)
            {
                string s = (string)e.Values[0].Value;
                if (!string.IsNullOrEmpty(s) && s.StartsWith("warp_")) return s.Substring("warp_".Length);
            }
            // OLD layout: an explicit mapid string in [3], else reverse-resolve the hash.
            if (!_newFmt && e.Values.Count > 3 && e.Values[3].Type == VT.String)
            {
                string s = (string)e.Values[3].Value;
                if (!string.IsNullOrEmpty(s) && s != "0") return s;
            }
            return _mapIdByHash.TryGetValue(mapHash, out var mid) ? mid : null;
        }

        /// <summary>Add a warp to <paramref name="mapId"/> at spawn (x,y,z) facing <paramref name="rot"/>.</summary>
        public WarpEntry AddWarp(string mapId, double x, double y, double z, int rot)
        {
            var tpl = File.Records(Rec).FirstOrDefault()
                      ?? throw new InvalidOperationException("warp_config has no WARP_INFO to clone.");
            if (Warps.Any(ex => string.Equals(ex.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A warp for \"{mapId}\" already exists. For another warp point in the same map, " +
                    $"use a suffixed id like \"{BaseMapId(mapId)}_02\" (\"{BaseMapId(mapId)}_03\", …).");
            var e = tpl.Clone();
            // field[0]/warp id use the FULL (possibly-suffixed) id; field[1] = the BASE map to load.
            int newId = WarpId(mapId), mapHash = MapIdHash(BaseMapId(mapId));
            int oldId = _newFmt && tpl.Values[0].Type == VT.String
                ? WarpId(((string)tpl.Values[0].Value).StartsWith("warp_") ? ((string)tpl.Values[0].Value).Substring(5) : "")
                : (tpl.GetInt(0) ?? 0);
            string oldBlob = (tpl.Values.Count > _fBlob && tpl.Values[_fBlob].Type == VT.String) ? (string)tpl.Values[_fBlob].Value : null;

            if (_newFmt)
            {
                SetStr(e, 0, "warp_" + mapId);
                SetInt(e, 1, mapHash);
                SetInt(e, 2, 0);
                SetInt(e, 9, 0);                                    // no availability condition (always on)
            }
            else
            {
                SetInt(e, 0, newId);
                SetInt(e, 1, mapHash);
                SetInt(e, 2, 0);
                SetStr(e, 3, "0");
                SetInt(e, 9, 0);
            }
            SetNum(e, _fx, x); SetNum(e, _fy, y); SetNum(e, _fz, z);
            SetInt(e, _frot, rot);
            if (oldBlob != null) SetStr(e, _fBlob, YwCond.RemapBase64(oldBlob, oldId, newId));

            InsertBeforeEnd(e);
            Bump(1);

            var w = new WarpEntry
            {
                Entry = e, Field0 = newId, MapHash = mapHash, MapId = mapId,
                MapName = _names.TryGetValue(mapHash, out var nm) ? nm : null,
                X = x, Y = y, Z = z, Rotation = rot,
            };
            Warps.Add(w);
            return w;
        }

        public void RemoveWarp(WarpEntry w)
        {
            File.Entries.Remove(w.Entry);
            Warps.Remove(w);
            Bump(-1);
        }

        /// <summary>Flush the edited spawn coords/rotation back into the underlying records before saving.</summary>
        public void CommitEdits()
        {
            foreach (var w in Warps)
            {
                SetNum(w.Entry, _fx, w.X); SetNum(w.Entry, _fy, w.Y); SetNum(w.Entry, _fz, w.Z);
                SetInt(w.Entry, _frot, w.Rotation);
            }
        }

        public void Save(string path)
        {
            CommitEdits();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllBytes(path, T2bWriter.Write(File));
        }

        /// <summary>Locate the warp table, preferring the versioned shipping name (warp_config_0.01b.cfg.bin).</summary>
        public static string FindConfig(string mapDir)
        {
            if (string.IsNullOrEmpty(mapDir) || !Directory.Exists(mapDir)) return null;
            var files = Directory.EnumerateFiles(mapDir, "warp_config*.cfg.bin").ToList();
            return files.FirstOrDefault(f => Path.GetFileName(f).IndexOf("_0.", StringComparison.Ordinal) >= 0)   // versioned
                ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();                        // else the biggest
        }

        public string MapName(int mapHash) => _names.TryGetValue(mapHash, out var n) ? n : null;

        // ---- name / mapid resolution ----
        private void BuildNameMap(string systemTextPath)
        {
            if (string.IsNullOrEmpty(systemTextPath) || !System.IO.File.Exists(systemTextPath)) return;
            var st = T2bReader.ReadFile(systemTextPath);
            foreach (var e in st.Records("TEXT_INFO"))
                if (e.Values.Count >= 3 && e.Values[0].Type == VT.Integer && e.Values[2].Type == VT.String)
                {
                    int h = Convert.ToInt32(e.Values[0].Value);
                    if (!_names.ContainsKey(h)) _names[h] = (string)e.Values[2].Value;
                }
        }

        private void BuildMapIdMap(IEnumerable<string> sources)
        {
            if (sources == null) return;
            foreach (var dir in sources.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "warp_*.xi"))
                {
                    string mid = Path.GetFileNameWithoutExtension(f).Substring("warp_".Length);
                    int h = MapIdHash(mid);
                    if (!_mapIdByHash.ContainsKey(h)) _mapIdByHash[h] = mid;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    string mid = Path.GetFileName(sub);
                    int h = MapIdHash(mid);
                    if (!_mapIdByHash.ContainsKey(h)) _mapIdByHash[h] = mid;
                }
            }
        }

        // ---- T2b helpers (mirror EventSet) ----
        private static double Num(T2bEntry e, int i)
        {
            if (i >= e.Values.Count) return 0;
            var v = e.Values[i];
            return v.Type == VT.FloatingPoint ? Convert.ToDouble(v.Value)
                 : v.Type == VT.Integer ? Convert.ToInt32(v.Value) : 0;
        }
        private static void SetInt(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private static void SetStr(T2bEntry e, int i, string v) { if (i < e.Values.Count) { e.Values[i].Type = VT.String; e.Values[i].Value = v ?? ""; } }
        // Whole numbers stay Integer, fractional become FloatingPoint — matching vanilla's mixed spawn coords.
        private static void SetNum(T2bEntry e, int i, double v)
        {
            if (i >= e.Values.Count) return;
            if (v == Math.Floor(v) && !double.IsInfinity(v)) { e.Values[i].Type = VT.Integer; e.Values[i].Value = (int)v; }
            else { e.Values[i].Type = VT.FloatingPoint; e.Values[i].Value = (float)v; }
        }
        private void InsertBeforeEnd(T2bEntry e)
        {
            int idx = File.Entries.FindIndex(x => x.Name == End);
            if (idx < 0) File.Entries.Add(e); else File.Entries.Insert(idx, e);
        }
        private void Bump(int d)
        {
            var b = File.Entries.FirstOrDefault(x => x.Name == Beg);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + d;
        }
    }
}
