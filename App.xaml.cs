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
	}
}
