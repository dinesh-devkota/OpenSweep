using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OpenSweep.Core;

namespace OpenSweep
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Write("Fatal: " + a.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Write("Background task: " + a.Exception); a.SetObserved(); };

            var args = e.Args.Select(a => a.Trim().ToLowerInvariant()).ToArray();
            if (args.Contains("/auto") || args.Contains("/install") || args.Contains("/uninstall"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    if (args.Contains("/auto")) AutoClean.Run();
                    else if (args.Contains("/install")) RunInstall();
                    else RunUninstall();
                }
                catch (Exception ex) { Log.Write("Command line mode failed: " + ex); }
                Shutdown();
                return;
            }

            ThemeManager.Apply(AppSettings.Current.Theme);
            var window = new MainWindow();
            MainWindow = window;
            window.SourceInitialized += (s, a) => ThemeManager.ApplyTitleBar(window);
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppSettings.Current.Save();
            base.OnExit(e);
        }

        static void RunInstall()
        {
            var r = MessageBox.Show(
                "Install OpenSweep for the current user?\n\nIt will be copied to:\n" + SelfInstaller.InstallDir +
                "\n\nYes – install and add a desktop shortcut\nNo – install with a Start menu shortcut only\nCancel – don't install",
                "Install OpenSweep", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            try
            {
                SelfInstaller.Install(r == MessageBoxResult.Yes);
                MessageBox.Show("OpenSweep is installed. You can start it from the Start menu.", "Install OpenSweep",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Installation failed: " + ex.Message, "Install OpenSweep", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static void RunUninstall()
        {
            var r = MessageBox.Show(
                "Remove OpenSweep from this PC?\n\nYes – remove the app and its settings, logs and registry backups\n" +
                "No – remove the app but keep settings and backups\nCancel – keep OpenSweep",
                "Uninstall OpenSweep", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            SelfInstaller.Uninstall(r == MessageBoxResult.Yes);
            MessageBox.Show("OpenSweep has been removed.", "Uninstall OpenSweep", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("UI error: " + e.Exception);
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to the log in:\n" + AppPaths.LogDir,
                "OpenSweep", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
