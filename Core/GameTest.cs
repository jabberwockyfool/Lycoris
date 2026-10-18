using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Lycoris
{
    /// <summary>
    /// "Test in game": runs the mod in Zakuro (or a 3dsrecomp port of YW3 built on it) without exporting anything.
    /// The mod's include/ folder is linked (directory junction) as Zakuro's fa/ mod folder, whose loose files Zakuro
    /// lays into yw_a.fa / yw_lg_en.fa on the fly — edits saved in Lycoris show up on the next launch.
    /// Test points are copies of the game's own save (not emulator states, which hold the archive tables the game
    /// read at the time and break once the mod's files change): the game boots on the current mod, then Continue.
    /// </summary>
    internal static class GameTest
    {
        public const string TitleId = "00040000001D6800";   // Yo-kai Watch 3 (EUR), the build Zakuro's compatibility list has

        private static string LycorisDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lycoris");
        private static string ConfigPath => Path.Combine(LycorisDir, "game.tsv");
        public static string TestPointsDir => Path.Combine(LycorisDir, "testpoints");

        public static string ZakuroDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zakuro");
        public static string ModLink => Path.Combine(ZakuroDir, "mods", TitleId, "fa");
        public static string SaveDir => Path.Combine(ZakuroDir, "user", "savedata", TitleId);

        private static Process _game;

        public static bool IsRunning => _game != null && !_game.HasExited;

        /// <summary>The game executable and ROM, asking for them the first time (or when they moved).</summary>
        private static bool GetConfig(Window owner, out string exe, out string rom)
        {
            exe = rom = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var f = File.ReadAllText(ConfigPath).Split('\t');
                    if (f.Length >= 2) { exe = f[0].Trim(); rom = f[1].Trim(); }
                }
            }
            catch { /* unreadable: ask again */ }
            if (File.Exists(exe) && File.Exists(rom)) return true;

            var pickExe = new Microsoft.Win32.OpenFileDialog
            {
                Title = "The game: Zakuro, or YW3 recompiled with 3dsrecomp port (yw3.exe)",
                Filter = "Executable (*.exe)|*.exe"
            };
            if (pickExe.ShowDialog(owner) != true) return false;
            var pickRom = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Your decrypted Yo-kai Watch 3 ROM (EUR)",
                Filter = "3DS ROM (*.3ds;*.cci;*.cxi)|*.3ds;*.cci;*.cxi|All files|*.*"
            };
            if (pickRom.ShowDialog(owner) != true) return false;
            exe = pickExe.FileName; rom = pickRom.FileName;
            Directory.CreateDirectory(LycorisDir);
            File.WriteAllText(ConfigPath, exe + "\t" + rom);
            return true;
        }

        /// <summary>Forget the game paths, so the next test asks for them again.</summary>
        public static void ResetConfig()
        {
            try { File.Delete(ConfigPath); } catch { /* ignore */ }
        }

        /// <summary>Points Zakuro's fa/ mod folder at the mod's include/ folder.</summary>
        private static void LinkMod(string modFolder)
        {
            string include = Path.Combine(modFolder, "include");
            if (!Directory.Exists(include))
                throw new InvalidOperationException($"The mod has no include folder:\n{include}");

            if (Directory.Exists(ModLink))
            {
                bool junction = (File.GetAttributes(ModLink) & FileAttributes.ReparsePoint) != 0;
                if (!junction)
                    throw new InvalidOperationException(
                        $"{ModLink} is a real folder, not a link Lycoris made. Move it away, then test again.");
                Directory.Delete(ModLink);   // removes the link only, never the mod it points to
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ModLink));
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{ModLink}\" \"{include}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            });
            string error = mklink.StandardError.ReadToEnd();
            mklink.WaitForExit();
            if (mklink.ExitCode != 0 || !Directory.Exists(ModLink))
                throw new InvalidOperationException("The mod folder could not be linked for Zakuro:\n" + error);
        }

        /// <summary>Closes the game Lycoris started, asking first. False when the user keeps it running.</summary>
        public static bool CloseGame(string why)
        {
            if (!IsRunning) return true;
            if (DarkMessage.Show($"The game is running. Close it {why}?", "Test in game",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            try { _game.Kill(); _game.WaitForExit(5000); } catch { /* already gone */ }
            return true;
        }

        /// <summary>Runs the game with the mod in modFolder, after setup (e.g. restoring a test point) if given.</summary>
        public static void Launch(Window owner, string modFolder, Action setup = null)
        {
            try
            {
                if (!GetConfig(owner, out string exe, out string rom)) return;
                if (!CloseGame("and restart it with the mod as it is saved now")) return;
                LinkMod(modFolder);
                setup?.Invoke();
                _game = Process.Start(new ProcessStartInfo(exe, $"\"{rom}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Test in game", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------------------------- Test points ----------------------------

        public static List<string> TestPoints()
        {
            if (!Directory.Exists(TestPointsDir)) return new List<string>();
            return Directory.GetDirectories(TestPointsDir)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(Path.GetFileName).ToList();
        }

        /// <summary>Copies the game's current save into a test point named name.</summary>
        public static void Capture(string name)
        {
            if (!Directory.Exists(SaveDir) || Directory.GetFiles(SaveDir).Length == 0)
                throw new InvalidOperationException("There is no save yet. Play, save in the game, then capture it.");
            string to = Path.Combine(TestPointsDir, name);
            if (Directory.Exists(to)) Directory.Delete(to, true);
            CopyFiles(SaveDir, to);
            Directory.SetLastWriteTimeUtc(to, DateTime.UtcNow);
        }

        /// <summary>Puts the test point in place of the game's save, keeping the save it replaces as a backup point.</summary>
        public static void Restore(string name)
        {
            if (Directory.Exists(SaveDir) && Directory.GetFiles(SaveDir).Length > 0)
                Capture("(backup) " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
            if (Directory.Exists(SaveDir)) Directory.Delete(SaveDir, true);
            CopyFiles(Path.Combine(TestPointsDir, name), SaveDir);
        }

        public static void Delete(string name)
        {
            Directory.Delete(Path.Combine(TestPointsDir, name), true);
        }

        private static void CopyFiles(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        }

        public static bool IsValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Test points: named copies of the game's save, to start a test right where it matters.</summary>
    internal sealed class TestPointsWindow : Window
    {
        private readonly string _modFolder;
        private readonly ListBox _list = new ListBox { Height = 220 };

        public TestPointsWindow(Window owner, string modFolder)
        {
            Owner = owner;
            _modFolder = modFolder;
            Title = "Test points";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.WindowBg;

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = "A test point is a copy of the game's save. Save in the game where you want to test (in front of the NPC, " +
                       "at the dungeon entrance…), capture it here, then launch from it: the game starts with the mod as it is " +
                       "saved now, and Continue brings you there.",
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(_list);

            var capture = Btn("Capture the current save…", Capture);
            var launch = Btn("▶ Launch from it", Launch);
            var delete = Btn("Delete", Delete);
            launch.FontWeight = FontWeights.SemiBold;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            bar.Children.Add(launch);
            bar.Children.Add(capture);
            bar.Children.Add(delete);
            root.Children.Add(bar);

            Content = root;
            _list.MouseDoubleClick += (s, e) => Launch();
            Refresh();
        }

        private static Button Btn(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        private void Refresh()
        {
            _list.ItemsSource = GameTest.TestPoints();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private string Selected => _list.SelectedItem as string;

        private void Capture()
        {
            // the game writes its save as it plays; capturing a save being written would copy half of it
            if (!GameTest.CloseGame("so its save can be captured")) return;
            string name = NameDialog.Ask(this, "Name of the test point", "In front of the NPC");
            if (name == null) return;
            try { GameTest.Capture(name); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            Refresh();
        }

        private void Launch()
        {
            string name = Selected;
            if (name == null) return;
            GameTest.Launch(this, _modFolder, () => GameTest.Restore(name));
            Refresh();
        }

        private void Delete()
        {
            string name = Selected;
            if (name == null) return;
            if (DarkMessage.Show($"Delete the test point “{name}”?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            try { GameTest.Delete(name); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            Refresh();
        }
    }

    /// <summary>Asks for a test point's name.</summary>
    internal sealed class NameDialog : Window
    {
        private readonly TextBox _name = new TextBox();

        private NameDialog(Window owner, string prompt, string initial)
        {
            Owner = owner;
            Title = prompt;
            Width = 360;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.WindowBg;
            _name.Text = initial;

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = prompt, Foreground = Theme.FgMuted });
            root.Children.Add(_name);
            var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) =>
            {
                if (GameTest.IsValidName(_name.Text?.Trim())) DialogResult = true;
                else DarkMessage.Show("The name can't be empty or hold \\ / : * ? \" < > |", Title);
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            root.Children.Add(btns);
            Content = root;
            Loaded += (s, e) => { _name.Focus(); _name.SelectAll(); };
        }

        public static string Ask(Window owner, string prompt, string initial)
        {
            var d = new NameDialog(owner, prompt, initial);
            return d.ShowDialog() == true ? d._name.Text.Trim() : null;
        }
    }
}
