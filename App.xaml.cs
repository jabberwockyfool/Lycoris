using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Lycoris
{
	/// <summary>
	/// Logique d'interaction pour App.xaml
	/// </summary>
	public partial class App : Application
	{
		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);
			// Global crash logger — write any unhandled exception (UI thread, background, or finalizer) to a
			// file next to the exe so a hard crash leaves a trace we can read.
			AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogCrash(ev.ExceptionObject as Exception, "AppDomain");
			DispatcherUnhandledException += (s, ev) =>
			{
				LogCrash(ev.Exception, "Dispatcher");
				ev.Handled = true;   // keep the app alive; show the error instead of hard-crashing
				try { DarkMessage.Show(ev.Exception.ToString(), "Lycoris — unhandled error", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
			};
			System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ev) => LogCrash(ev.Exception, "Task");
			// Dark background + native title bar on EVERY window. The implicit Window style doesn't reach
			// Window subclasses (MainWindow, the code-built editors, dialogs…), so force it here globally.
			EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
				new RoutedEventHandler((s, ev) =>
				{
					var w = (Window)s;
					w.Background = Theme.WindowBg;
					w.Foreground = Theme.Fg;
					Theme.ApplyDarkTitleBar(w);
				}));
			new HomeWindow().Show();
		}

		private static void LogCrash(Exception ex, string source)
		{
			try
			{
				string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lycoris-crash.log");
				System.IO.File.AppendAllText(path, $"[{DateTime.Now:s}] ({source})\r\n{ex}\r\n\r\n");
			}
			catch { }
		}
	}
}
