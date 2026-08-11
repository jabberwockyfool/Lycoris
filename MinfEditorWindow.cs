using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>
    /// Minf Editor — opens a YW3 .xc (XPCK) archive and edits its .mtninf entries: the action slot id,
    /// the frame start/end, and the playback speed. Standalone (no loaded project). This is the Lycoris
    /// integration of MinfPatcher (Kirasnuggets) — but archive-aware: it reads/repacks the .xc directly
    /// instead of loose .mtninf files. mtninf field offsets: slot @0x1C, frame start @0x4C, end @0x50,
    /// speed @0x54 (per Mtninf2TXT / the community docs).
    /// </summary>
    public sealed class MinfEditorWindow : Window
    {
        private const int OFF_SLOT = 0x1C;   // 4-byte action hash
        private const int OFF_START = 0x4C;  // int32 frame start
        private const int OFF_END = 0x50;    // int32 frame end
        private const int OFF_SPEED = 0x54;  // float speed

        private List<XpckFile> _files;       // full archive contents (repacked on save)
        private string _path;
        private readonly ObservableCollection<MinfRow> _rows = new ObservableCollection<MinfRow>();
        private readonly DataGrid _grid;
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button _saveBtn;
        private readonly Button _addBtn;
        private readonly Button _txtBtn;
        private readonly Button _importBtn;

        public MinfEditorWindow(Window owner)
        {
            Owner = owner;
            Title = "Lycoris — Minf Editor (.xc)";
            Width = 880; Height = 620;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var openBtn = new Button { Content = "Open .xc…", MinWidth = 110, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
            openBtn.Click += (s, e) => Open();
            _addBtn = new Button { Content = "+ Add mtninf", MinWidth = 110, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            _addBtn.Click += (s, e) => AddMtninf();
            _saveBtn = new Button { Content = "Save .xc…", MinWidth = 110, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            _saveBtn.Click += (s, e) => Save();
            _txtBtn = new Button { Content = "Export TXT…", MinWidth = 110, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            _txtBtn.Click += (s, e) => ExportTxt();
            _importBtn = new Button { Content = "Import TXT…", MinWidth = 110, MinHeight = 30, IsEnabled = false };
            _importBtn.Click += (s, e) => ImportTxt();
            bar.Children.Add(openBtn);
            bar.Children.Add(_addBtn);
            bar.Children.Add(_saveBtn);
            bar.Children.Add(_txtBtn);
            bar.Children.Add(_importBtn);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Background = Theme.FieldBg,
                Foreground = Theme.Fg,
                RowBackground = Theme.FieldBg,
                ItemsSource = _rows,
            };
            _grid.Columns.Add(RoText("File", nameof(MinfRow.FileName), 110, true));
            _grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "Action (pick)",
                Width = 300,
                ItemsSource = MinfActions.Choices,   // ordered by tag (P10, P20, … P90)
                SelectedItemBinding = new Binding(nameof(MinfRow.SlotChoice)) { Mode = BindingMode.TwoWay },
            });
            _grid.Columns.Add(RoText("Slot (hex)", nameof(MinfRow.SlotHex), 120, false));
            _grid.Columns.Add(RoText("Start", nameof(MinfRow.Start), 70, false));
            _grid.Columns.Add(RoText("End", nameof(MinfRow.End), 70, false));
            _grid.Columns.Add(RoText("Speed", nameof(MinfRow.Speed), 70, false));
            root.Children.Add(_grid);

            Content = root;
            _status.Text = "Open a .xc (p10 / p20 / p84 …) to edit its animation slots and frame ranges.";
        }

        private static DataGridTextColumn RoText(string header, string path, int width, bool readOnly)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(path) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus },
                Width = width,
                IsReadOnly = readOnly,
            };
        }

        private void Open()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "YW3 archive (*.xc)|*.xc|All files|*.*", Title = "Open a .xc archive" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                byte[] data = File.ReadAllBytes(dlg.FileName);
                _files = Xpck.Read(data);   // throws if not an XPCK archive
                _path = dlg.FileName;

                _rows.Clear();
                foreach (var f in _files.Where(f => f.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase))
                                        .OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (f.Data.Length >= OFF_SPEED + 4)
                        _rows.Add(new MinfRow(f));
                }
                _saveBtn.IsEnabled = _rows.Count > 0;
                _txtBtn.IsEnabled = _rows.Count > 0;
                _importBtn.IsEnabled = _rows.Count > 0;
                _addBtn.IsEnabled = _files.Any(f => f.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase));
                Title = "Lycoris — Minf Editor — " + Path.GetFileName(_path);
                _status.Text = $"{_files.Count} file(s) in the archive, {_rows.Count} .mtninf. Pick an Action / edit Start / End / Speed, or Add mtninf, then Save.";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Minf Editor — open error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Add a new .mtninf by cloning an existing one (so its structure + mtn2 reference are
        /// valid); the user then picks its Action slot and frame range. A matching RES entry is created on Save.</summary>
        private void AddMtninf()
        {
            if (_files == null) return;
            var template = _files.FirstOrDefault(f => f.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase));
            if (template == null) { DarkMessage.Show("This archive has no .mtninf to clone as a template.", "Add mtninf"); return; }

            int max = -1;
            foreach (var f in _files)
                if (f.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(Path.GetFileNameWithoutExtension(f.Name), out int n)) max = Math.Max(max, n);
            string name = (max + 1).ToString("D3") + ".mtninf";

            var newFile = new XpckFile(name, (byte[])template.Data.Clone());
            _files.Add(newFile);
            _rows.Add(new MinfRow(newFile));
            _saveBtn.IsEnabled = true;
            _status.Text = $"Added {name} — pick its Action and set Start/End, then Save.";
        }

        /// <summary>Export the mtninf list to a .txt (Mtninf2TXT format:
        /// "file - HEX - action - start - end", one line per mtninf).</summary>
        private void ExportTxt()
        {
            if (_rows.Count == 0) return;
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt",
                FileName = System.IO.Path.GetFileNameWithoutExtension(_path ?? "mtninf") + ".txt",
                Title = "Export the mtninf list as TXT",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var lines = _rows.Select(r => $"{r.FileName} - {r.SlotHex} - {r.Action} - {r.Start} - {r.End}");
                File.WriteAllLines(dlg.FileName, lines);
                _status.Text = "Exported: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Minf Editor — export error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Import a Mtninf2TXT-format .txt ("file - HEX - [action -] start - end"), updating the
        /// matching rows' slot / start / end by filename. Rows the txt doesn't mention are left as-is.</summary>
        private void ImportTxt()
        {
            if (_rows.Count == 0) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Text file (*.txt)|*.txt|All files|*.*", Title = "Import mtninf values from TXT" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                var byName = new Dictionary<string, MinfRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _rows) byName[r.FileName] = r;

                int updated = 0, skipped = 0;
                foreach (var raw in File.ReadAllLines(dlg.FileName))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var parts = line.Split(new[] { " - " }, StringSplitOptions.None);
                    if (parts.Length < 4 || !byName.TryGetValue(parts[0].Trim(), out var row)) { skipped++; continue; }

                    if (int.TryParse(parts[parts.Length - 2].Trim(), out int st)) row.Start = st;
                    if (int.TryParse(parts[parts.Length - 1].Trim(), out int en)) row.End = en;
                    string slot = parts[1].Trim();
                    var toks = slot.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length == 4 && toks.All(t => byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
                        row.SlotHex = slot;
                    updated++;
                }
                _status.Text = $"Imported: {updated} row(s) updated, {skipped} skipped. Review, then Save.";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Minf Editor — import error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save()
        {
            if (_files == null || _rows.Count == 0) return;
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "YW3 archive (*.xc)|*.xc",
                FileName = Path.GetFileName(_path),
                Title = "Save the patched .xc",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                foreach (var r in _rows) r.WriteBack(OFF_SLOT, OFF_START, OFF_END, OFF_SPEED);
                EnsureResEntries();
                File.WriteAllBytes(dlg.FileName, Xpck.Write(_files));
                _status.Text = "Saved: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Minf Editor — save error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Ensure every .mtninf slot has a RES.MTNINF entry (the game registers slots from RES).
        /// Idempotent: only appends entries for slots not already present. No-op if the archive has no RES.</summary>
        private void EnsureResEntries()
        {
            var resFile = _files.FirstOrDefault(f => f.Name.Equals("RES.bin", StringComparison.OrdinalIgnoreCase));
            if (resFile == null) return;
            var res = Res.Read(resFile.Data);
            var have = new HashSet<uint>();
            var sec = res.Nodes.Find(s => s.Type == Res.MTNINF);
            if (sec != null) foreach (var e in sec.Entries) have.Add(BitConverter.ToUInt32(e, 0));

            int added = 0;
            foreach (var f in _files)
            {
                if (!f.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase) || f.Data.Length < OFF_SLOT + 4) continue;
                var slot = new byte[4];
                Array.Copy(f.Data, OFF_SLOT, slot, 0, 4);
                uint crc = BitConverter.ToUInt32(slot, 0);
                if (have.Add(crc)) { res.AddMtninf(slot, MinfActions.ShortName(slot)); added++; }
            }
            if (added > 0) resFile.Data = res.Write();
        }
    }

    /// <summary>One editable .mtninf row (slot hash + frame range + speed) over an <see cref="XpckFile"/>.</summary>
    public sealed class MinfRow : INotifyPropertyChanged
    {
        private readonly XpckFile _file;
        private string _slotHex;
        private int _start, _end;
        private float _speed;

        public MinfRow(XpckFile file)
        {
            _file = file;
            byte[] d = file.Data;
            _slotHex = string.Join(" ", d.Skip(0x1C).Take(4).Select(b => b.ToString("X2")));
            _start = BitConverter.ToInt32(d, 0x4C);
            _end = BitConverter.ToInt32(d, 0x50);
            _speed = BitConverter.ToSingle(d, 0x54);
        }

        public string FileName => _file.Name;

        public string SlotHex
        {
            get => _slotHex;
            set
            {
                _slotHex = (value ?? "").Trim().ToUpperInvariant();
                OnChanged(nameof(SlotHex));
                OnChanged(nameof(SlotChoice));
                OnChanged(nameof(Action));
            }
        }

        /// <summary>The action picked in the dropdown ("Name  (HEX)"); setting it sets the slot hex.</summary>
        public string SlotChoice
        {
            get => MinfActions.ChoiceFor(_slotHex);
            set { var h = MinfActions.HexFromChoice(value); if (h != null) SlotHex = h; }
        }

        public string Action => MinfActions.Name(_slotHex);

        public int Start { get => _start; set { _start = value; OnChanged(nameof(Start)); } }
        public int End { get => _end; set { _end = value; OnChanged(nameof(End)); } }
        public float Speed { get => _speed; set { _speed = value; OnChanged(nameof(Speed)); } }

        /// <summary>Patch the underlying mtninf bytes with the edited values.</summary>
        public void WriteBack(int offSlot, int offStart, int offEnd, int offSpeed)
        {
            byte[] d = _file.Data;
            byte[] slot = ParseSlot(_slotHex);
            if (slot != null) Array.Copy(slot, 0, d, offSlot, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_start), 0, d, offStart, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_end), 0, d, offEnd, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(_speed), 0, d, offSpeed, 4);
        }

        private static byte[] ParseSlot(string hex)
        {
            var parts = (hex ?? "").Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return null;
            var b = new byte[4];
            for (int i = 0; i < 4; i++)
                if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
            return b;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>One selectable slot in the grouped Action dropdown.</summary>
    public sealed class ActionChoice
    {
        public string Group;   // "P20"
        public string Name;    // "Attack"
        public string Hex;     // "98 12 A9 9B"
    }

    /// <summary>YW3 animation slot-hash → human name (from the community docs: Math_kk / Younes).</summary>
    public static class MinfActions
    {
        private const string Sep = "  —  ";   // separates the "Name (Pxx)" label from the HEX in a dropdown entry

        /// <summary>All known slots, in group order (P10, P20, P21, P84, P84 Boss, P90).</summary>
        public static readonly List<ActionChoice> ChoiceList = new List<ActionChoice>();

        /// <summary>Dropdown labels "Name (Pxx)  —  HEX", ORDERED by tag so the list reads grouped.</summary>
        public static readonly List<string> Choices = new List<string>();

        static MinfActions()
        {
            foreach (var c in ChoiceList) Choices.Add(c.Name + " (" + c.Group + ")" + Sep + c.Hex);
        }

        public static string Name(string slotHex)
        {
            var key = (slotHex ?? "").Trim().ToUpperInvariant();
            return Map.TryGetValue(key, out var n) ? n : "Unknown";
        }

        /// <summary>The dropdown label for a slot hex ("Name (Pxx)  —  HEX", or the raw hex if unknown).</summary>
        public static string ChoiceFor(string slotHex)
        {
            var key = (slotHex ?? "").Trim().ToUpperInvariant();
            return Map.TryGetValue(key, out var n) ? n + Sep + key : key;
        }

        /// <summary>Extract the slot hex from a dropdown label ("Name (Pxx)  —  HEX" → "HEX").</summary>
        public static string HexFromChoice(string choice)
        {
            if (string.IsNullOrWhiteSpace(choice)) return null;
            int k = choice.LastIndexOf(Sep, StringComparison.Ordinal);
            string hex = k >= 0 ? choice.Substring(k + Sep.Length) : choice;
            return hex.Trim().ToUpperInvariant();
        }

        /// <summary>A compact name for the RES string table from a raw 4-byte slot.</summary>
        public static string ShortName(byte[] slot4)
        {
            string hex = string.Join(" ", slot4.Select(b => b.ToString("X2")));
            if (!Map.TryGetValue(hex, out var n)) return "slot_" + hex.Replace(" ", "");
            return n.Replace(" ", "_").Replace("/", "_").Replace("(", "").Replace(")", "");
        }

        // Each group's slots (doc: Math_kk / Younes); the "(Pxx)" group tag is appended to every name.
        private static readonly Dictionary<string, string> Map = Build();

        private static Dictionary<string, string> Build()
        {
            var m = new Dictionary<string, string>();
            void G(string group, params string[] hp)
            {
                for (int i = 0; i + 1 < hp.Length; i += 2)
                {
                    m[hp[i]] = hp[i + 1] + " (" + group + ")";
                    ChoiceList.Add(new ActionChoice { Group = group, Name = hp[i + 1], Hex = hp[i] });
                }
            }
            G("P10",
                "A8 5A 6A 85", "T-pose", "4A 09 C3 43", "Idle", "44 6A 78 62", "Long idle",
                "A5 CF E5 80", "Talk", "B4 27 F7 FF", "Walk", "54 43 28 60", "Run", "20 33 E6 11", "???");
            G("P20",
                "58 DF 5B 84", "Battle start", "2F 60 5C C8", "Idle", "17 94 B0 2D", "Long idle",
                "23 8F 2C 41", "Tired/Sleeping", "F8 E2 EE 80", "Loafing", "7E 15 40 AB", "Recovering",
                "98 12 A9 9B", "Attack", "4D 03 C3 B7", "Magic/Inspirit", "B1 01 4E 48", "Guard",
                "CD 2A A7 DA", "Miss", "D6 D6 8B 14", "Damage", "B1 85 81 B5", "Death",
                "A9 E0 BB 11", "Ascension", "04 B7 C0 F9", "Charge",
                "54 53 5B 79", "Soultimate start", "0A 8E 77 DE", "Soultimate");
            G("P21",
                "AB 00 7C 21", "Victory 1 start", "56 E4 4D EC", "Victory 1",
                "11 51 75 B8", "Victory 2 start", "95 B7 60 C7", "Victory 2",
                "87 61 72 CF", "Victory 3 start", "D4 86 7B DE", "Victory 3",
                "24 F4 16 51", "Victory 4 start", "13 10 3A 91", "Victory 4");
            G("P84",
                "C2 B2 3B F0", "Walk", "22 D6 E4 6F", "Run", "3C 9C 0F 4C", "Idle",
                "B8 32 75 BC", "Long idle", "30 73 7F C5", "Tired/Stunned", "6D D8 FD 46", "Recovering",
                "47 7A AA 9D", "Guard", "DE E7 1A 37", "Victory 1", "84 65 80 40", "Damage",
                "76 16 E5 FB", "Death", "29 D3 F5 14", "Ascension", "B8 CD C1 CC", "Victory 2",
                "45 18 1E 68", "Victory 3", "17 4B 93 7D", "Charge up", "CA A1 A2 CF", "Attack",
                "09 A4 F3 67", "Power/Tank attack", "70 F0 AB 56", "Triple attack hit 2",
                "E6 C0 AC 21", "Triple attack hit 3", "85 CC 3B 21", "Dash attack",
                "38 BC 48 02", "Fall-back attack", "A6 8A 26 6D", "Magic/Debuff/Buff",
                "06 E0 50 2D", "Soultimate attack", "BF F1 3A D5", "Miss/Dodge",
                "EC 20 75 3D", "Blitz attack", "FA C0 CC AD", "Mighty attack", "EF D6 F1 F7", "Shockwave stomp");
            G("P84 Boss",
                "95 20 91 F0", "Basic attack", "73 F1 04 19", "Basic attack 2", "90 4C 68 59", "Charge up",
                "A0 4D 3D 06", "Power attack", "0E 33 0F 5A", "Jump up", "EF F8 F2 C1", "Jump down",
                "22 10 FD 57", "Sighting start", "DB AB E5 9F", "Sighting end", "7F 17 42 CF", "Trapped",
                "8E BC A0 FD", "Taunt", "44 AA 2E EB", "Chance start", "15 49 32 DC", "Chance middle",
                "96 44 D4 50", "Chance end", "CE 45 F6 84", "Getting eaten", "3C 69 83 CD", "Shockwave stomp",
                "06 B6 38 0E", "Magic", "E5 70 B6 65", "Soultimate start", "D6 0B 14 59", "Soultimate middle",
                "DC 81 81 C2", "Soultimate middle loop", "0C F0 64 8F", "Soultimate end",
                "B7 E9 56 25", "Rush start", "F0 92 48 F8", "Rush middle", "BA 28 90 80", "Rush end");
            G("P90", "70 35 34 1C", "???");
            return m;
        }
    }
}
