using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// "Equip-transform" editor: register a character switch (equip the switch item on the ORIGINAL yo-kai →
    /// it becomes a CUSTOM/alternate form), à la Enma → Enma Blade. Writes the chara_ability EFF_DATA + EFFECT
    /// count and the chara_param CHARA_SAME_KIND_INFO into the mod. Reuses an existing in-game switch effect.
    /// </summary>
    public sealed class CharaSwitchWindow : Window
    {
        private readonly YokaiDatabase _db;
        private T2bFile _ability;
        private string _abilityPath;
        private YokaiInfo _from, _to;

        private readonly TextBlock _fromLbl = Lbl("(none)");
        private readonly TextBlock _toLbl = Lbl("(none)");
        private readonly TextBlock _abilityLbl = new TextBlock { Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly ComboBox _effect = new ComboBox { Width = 220, IsEditable = true };
        private readonly ListBox _existing = new ListBox { Height = 110, Background = Theme.FieldBg, Foreground = Theme.Fg, Margin = new Thickness(0, 4, 0, 0) };
        private readonly CheckBox _sameKind = new CheckBox { Content = "Declare SAME-KIND in chara_param (needed — the two forms are the same character)", Foreground = Theme.Fg, Margin = new Thickness(0, 8, 0, 0), IsChecked = true };
        private readonly ComboBox _item = new ComboBox { Width = 260, IsEditable = true, IsTextSearchEnabled = true };
        private readonly TextBox _skillStart = new TextBox { Width = 44, VerticalAlignment = VerticalAlignment.Center, Text = "5" };
        private readonly Button _apply = new Button { Content = "Apply switch", MinWidth = 120, MinHeight = 30 };
        private readonly Button _remove = new Button { Content = "Remove switch", MinWidth = 120, MinHeight = 30, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        private T2bFile _itemCfg;
        private string _itemCfgPath;

        public CharaSwitchWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Equip Transform (character switch)";
            Width = 620; Height = 420;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "Make a yo-kai transform when you equip the switch item (like Enma → Enma Blade). " +
                       "Pick the ORIGINAL yo-kai (the one you equip the item on) and the CUSTOM form it becomes; " +
                       "the switch item/ability already exist in-game — this registers the new pair on them.",
                Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });

            root.Children.Add(PickRow("Original yo-kai (equip on)", _fromLbl, () => { _from = Pick(); _fromLbl.Text = Desc(_from); }));
            root.Children.Add(PickRow("Transforms into (custom)", _toLbl, () => { _to = Pick(); _toLbl.Text = Desc(_to); }));

            var abRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            abRow.Children.Add(new TextBlock { Text = "chara_ability", Width = 170, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            var abBtn = new Button { Content = "Choose…", MinWidth = 80, MinHeight = 26 };
            abBtn.Click += (s, e) => ChooseAbility();
            abRow.Children.Add(abBtn);
            abRow.Children.Add(_abilityLbl);
            root.Children.Add(abRow);

            var effRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            effRow.Children.Add(new TextBlock { Text = "Switch effect", Width = 170, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            effRow.Children.Add(_effect);
            root.Children.Add(effRow);
            _effect.SelectionChanged += (s, e) => RefreshExisting();
            _effect.LostFocus += (s, e) => RefreshExisting();

            var itemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            itemRow.Children.Add(new TextBlock { Text = "Switch item (optional)", Width = 170, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            _item.DisplayMemberPath = "Name"; _item.SelectedValuePath = "Key";
            System.Windows.Controls.TextSearch.SetTextPath(_item, "Name");
            _item.ItemsSource = _db.ItemOptions;
            itemRow.Children.Add(_item);
            itemRow.Children.Add(new TextBlock { Text = "  skill#", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            itemRow.Children.Add(_skillStart);
            root.Children.Add(itemRow);
            root.Children.Add(new TextBlock { Text = "Item = allow the original yo-kai to equip it + grant the switch skill (ITEM_EQUIPMENT_SKILL[skill#], default 5).", Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap });

            root.Children.Add(_sameKind);

            root.Children.Add(new TextBlock { Text = "Existing switches on this effect (who it's for):", Foreground = Theme.FgMuted, Margin = new Thickness(0, 10, 0, 0) });
            root.Children.Add(_existing);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            _apply.Click += (s, e) => Apply();
            _remove.Click += (s, e) => Remove();
            btns.Children.Add(_apply);
            btns.Children.Add(_remove);
            root.Children.Add(btns);
            root.Children.Add(_status);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            LoadAbility();
        }

        private void LoadAbility()
        {
            string path = CharaSwitch.FindAbilityFile(_db);
            if (path == null)
            {
                _apply.IsEnabled = false;
                _abilityLbl.Text = "(not found)";
                _status.Text = "chara_ability_*.cfg.bin not found in the mod or reference — click Choose… to point to the retail chara_ability (the one with the switch data).";
                return;
            }
            SetAbility(path);
        }

        /// <summary>Let the user point to a chara_ability that actually contains the switch effect (the retail
        /// chara_ability_5.00.40) when the auto-found one doesn't.</summary>
        private void ChooseAbility()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "chara_ability (*.cfg.bin)|chara_ability*.cfg.bin|All cfg.bin|*.cfg.bin|All files|*.*",
                Title = "Choose the chara_ability file (retail chara_ability_5.00.40 has the switch effect)",
            };
            if (dlg.ShowDialog() == true) SetAbility(dlg.FileName);
        }

        private void SetAbility(string path)
        {
            try { _ability = T2bReader.ReadFile(path); }
            catch (Exception ex) { _apply.IsEnabled = false; _abilityLbl.Text = "(error)"; _status.Text = "Could not read chara_ability: " + ex.Message; return; }
            _abilityPath = path;
            _abilityLbl.Text = System.IO.Path.GetFileName(path);

            _effect.Items.Clear();
            var effects = CharaSwitch.DetectEffects(_ability);
            foreach (var kv in effects)
                _effect.Items.Add($"0x{unchecked((uint)kv.Key):X8}  ({kv.Value} mapping(s))");

            if (effects.Count > 0)
                _effect.Text = $"0x{unchecked((uint)effects[0].Key):X8}";   // default = an effect that REALLY exists here
            else
                _effect.Text = $"0x{unchecked((uint)CharaSwitch.DefaultEffect):X8}";

            _apply.IsEnabled = _db.ParamData != null;
            if (_db.ParamData == null) _status.Text = "chara_param not loaded.";
            else if (effects.Count == 0)
                _status.Text = $"{System.IO.Path.GetFileName(path)} has NO character-switch effect. Choose the retail chara_ability (chara_ability_5.00.40) instead.";
            else
                _status.Text = $"Ability: {System.IO.Path.GetFileName(path)} — {effects.Count} switch effect(s). Pick both yo-kai, then Apply.";
            RefreshExisting();
        }

        /// <summary>List the FROM→TO pairs already registered on the current effect, resolved to yo-kai names,
        /// so you can see which item/yo-kai each switch effect is for.</summary>
        private void RefreshExisting()
        {
            _existing.Items.Clear();
            if (_ability == null) return;
            int eff = ParseHex(_effect.Text);
            if (eff == 0) return;
            var maps = CharaSwitch.Mappings(_ability, eff);
            if (maps.Count == 0) { _existing.Items.Add("(no mapping on this effect yet)"); return; }
            foreach (var m in maps)
                _existing.Items.Add($"{ResolveName(m[0])}   →   {ResolveName(m[1])}");
        }

        private string ResolveName(int paramHash)
        {
            var y = _db.Yokai.FirstOrDefault(k => k.ParamHash == paramHash);
            return y != null ? $"{y.DisplayName} (0x{unchecked((uint)paramHash):X8})" : $"0x{unchecked((uint)paramHash):X8}";
        }

        private void Apply()
        {
            if (_from == null || _to == null) { _status.Text = "Pick both the original and the custom yo-kai."; return; }
            if (_from.ParamHash == 0 || _to.ParamHash == 0) { _status.Text = "One of the yo-kai has no ParamHash."; return; }
            if (_from.ParamHash == _to.ParamHash) { _status.Text = "The original and custom yo-kai must be different."; return; }

            int eff = ParseHex(_effect.Text);
            if (eff == 0) { _status.Text = "Enter a valid switch effect id (e.g. 0xC58E24C1)."; return; }

            bool abilityExists = CharaSwitch.SwitchExists(_ability, eff, _from.ParamHash, _to.ParamHash);
            bool wantSame = _sameKind.IsChecked == true;
            int? itemId = _item.SelectedValue as int?;
            if (DarkMessage.Show(
                    $"Register a switch on effect 0x{unchecked((uint)eff):X8}?\n\n" +
                    $"Equip → {Desc(_from)}  becomes  {Desc(_to)}\n\n" +
                    $"chara_ability: yes\nsame-kind (medallium): {(wantSame ? "yes ⚠" : "no")}\n" +
                    $"allow-equip on item: {(itemId.HasValue ? "yes" : "no")}",
                    "Equip Transform", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            try
            {
                // Only add the EFF_DATA mapping if it's not already there — but ALWAYS continue with same-kind + item
                // (so re-running to add the item part works even when the ability mapping already exists).
                if (!abilityExists) CharaSwitch.AddToAbility(_ability, eff, _from.ParamHash, _to.ParamHash);
                bool sameAdded = false;
                // SAME_KIND matches EFF_DATA exactly: INFO[0] = the BASE/equip-on form (From, = EFF_DATA[7]),
                // DATA[0] = the transformed/custom form (To, = EFF_DATA[8]). Verified in-game (base = INFO/[7]).
                if (wantSame && !CharaSwitch.SameKindExists(_db.ParamData, _from.ParamHash))
                { CharaSwitch.AddSameKind(_db.ParamData, _from.ParamHash, _to.ParamHash); sameAdded = true; }

                string aOut = _abilityPath;
                if (!abilityExists) { aOut = _db.MirrorToMod(_abilityPath) ?? _abilityPath; T2bWriter.WriteFile(_ability, aOut); _abilityPath = aOut; }
                if (sameAdded) { string pOut = _db.MirrorToMod(_db.ParamFile) ?? _db.ParamFile; T2bWriter.WriteFile(_db.ParamData, pOut); }
                string itemMsg = ApplyItem(itemId, allow: true, eff);

                _status.Text = $"Done. {Desc(_from)} → {Desc(_to)} on 0x{unchecked((uint)eff):X8}.";
                DarkMessage.Show(
                    $"Switch {(abilityExists ? "updated" : "registered")}:\nEquip → {Desc(_from)} becomes {Desc(_to)}\n\n" +
                    $"chara_ability mapping: {(abilityExists ? "already present" : "added")}\n" +
                    $"same-kind: {(sameAdded ? "added" : (wantSame ? "already present" : "skipped"))}\n{itemMsg}",
                    "Equip Transform", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshExisting();
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Equip Transform", MessageBoxButton.OK, MessageBoxImage.Error); _status.Text = "Failed: " + ex.Message; }
        }

        private void Remove()
        {
            if (_ability == null || _db.ParamData == null) { _status.Text = "Load a chara_ability (and a mod) first."; return; }
            if (_from == null || _to == null) { _status.Text = "Pick the same original + custom yo-kai to remove their switch."; return; }
            int eff = ParseHex(_effect.Text);
            if (eff == 0) { _status.Text = "Enter the switch effect id."; return; }
            int? itemId = _item.SelectedValue as int?;
            if (DarkMessage.Show(
                    $"Remove the switch {Desc(_from)} → {Desc(_to)} on 0x{unchecked((uint)eff):X8}?\n\n" +
                    "Removes its chara_ability mapping, the same-kind entry (if present)" +
                    (itemId.HasValue ? ", and the item equip permission." : "."),
                    "Remove switch", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                bool ab = CharaSwitch.RemoveFromAbility(_ability, eff, _from.ParamHash, _to.ParamHash);
                bool sk = CharaSwitch.RemoveSameKind(_db.ParamData, _from.ParamHash);   // INFO[0] = base/equip-on form (From)

                string aOut = _db.MirrorToMod(_abilityPath) ?? _abilityPath;
                T2bWriter.WriteFile(_ability, aOut); _abilityPath = aOut;
                if (sk) { string pOut = _db.MirrorToMod(_db.ParamFile) ?? _db.ParamFile; T2bWriter.WriteFile(_db.ParamData, pOut); }
                string itemMsg = ApplyItem(itemId, allow: false, eff);

                _status.Text = $"Removed. ability: {(ab ? "yes" : "not found")}, same-kind: {(sk ? "yes" : "none")}.";
                DarkMessage.Show($"Switch removed.\nchara_ability mapping: {(ab ? "removed" : "not found")}\n" +
                    $"same-kind: {(sk ? "removed" : "none")}\n{itemMsg}", "Remove switch", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshExisting();
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Remove switch", MessageBoxButton.OK, MessageBoxImage.Error); _status.Text = "Failed: " + ex.Message; }
        }

        /// <summary>Make (or unmake) the chosen item a switch item: allow the original yo-kai to equip it
        /// (ITEM_EQUIP_COND_CHARA) AND grant it the switch skill (REF_EQUIPMENT_SKILL → the ability that owns the
        /// effect). The skill index is auto-resolved from the effect (never a hard-coded 5). Returns a status line.</summary>
        private string ApplyItem(int? itemId, bool allow, int eff)
        {
            if (!itemId.HasValue) return "item: (no item chosen)";
            if (_itemCfg == null)
            {
                _itemCfgPath = CharaSwitch.FindItemConfig(_db);
                if (_itemCfgPath == null) return "item: ⚠ item_config not found";
                try { _itemCfg = T2bReader.ReadFile(_itemCfgPath); } catch (Exception ex) { return "item: ⚠ " + ex.Message; }
            }
            string err; string detail = "";
            if (allow)
            {
                err = CharaSwitch.AllowEquip(_itemCfg, itemId.Value, _from.BaseHash);
                if (err == null)
                {
                    // Resolve the ability that owns this effect (0xC58E24C1 → 0x9A5F207E), ensure it's in the item's
                    // ITEM_EQUIPMENT_SKILL list, and point the item's REF at its real index.
                    int? abilityId = CharaSwitch.SwitchAbilityId(_ability, eff);
                    int start;
                    if (abilityId.HasValue)
                    {
                        start = CharaSwitch.EnsureEquipSkillEntry(_itemCfg, abilityId.Value);
                        detail = $" (skill 0x{unchecked((uint)abilityId.Value):X8} @ index {start})";
                    }
                    else start = int.TryParse(_skillStart.Text.Trim(), out int s) ? s : CharaSwitch.DefaultEquipSkillStart;
                    if (start < 0) return "item: ⚠ could not add the switch skill to ITEM_EQUIPMENT_SKILL";
                    err = CharaSwitch.AddEquipSkill(_itemCfg, itemId.Value, start, 1);
                }
            }
            else
            {
                err = CharaSwitch.DisallowEquip(_itemCfg, itemId.Value, _from.BaseHash)
                      ?? CharaSwitch.DisallowEquipSkill(_itemCfg, itemId.Value);
            }
            if (err != null) return "item: ⚠ " + err;
            string iOut = _db.MirrorToMod(_itemCfgPath) ?? _itemCfgPath;
            T2bWriter.WriteFile(_itemCfg, iOut); _itemCfgPath = iOut;
            return $"item: {(allow ? "equip-cond + switch skill" + detail : "removed")} ({System.IO.Path.GetFileName(iOut)})";
        }

        private YokaiInfo Pick()
        {
            if (_db == null || _db.Yokai.Count == 0) { DarkMessage.Show("No yo-kai loaded.", "Pick yo-kai"); return null; }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Picked : null;
        }

        private static string Desc(YokaiInfo y) => y == null ? "(none)" : $"{y.DisplayName}  (0x{unchecked((uint)y.ParamHash):X8})";

        private FrameworkElement PickRow(string label, TextBlock valueLabel, Action onPick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, Width = 170, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Content = "yo-kai…", MinWidth = 80, MinHeight = 26 };
            btn.Click += (s, e) => onPick();
            sp.Children.Add(btn);
            valueLabel.Margin = new Thickness(10, 0, 0, 0);
            valueLabel.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(valueLabel);
            return sp;
        }

        private static TextBlock Lbl(string t) => new TextBlock { Text = t, Foreground = Theme.Fg };

        private static int ParseHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"0x([0-9A-Fa-f]{1,8})");
            if (m.Success && uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u))
                return unchecked((int)u);
            return 0;
        }
    }
}
