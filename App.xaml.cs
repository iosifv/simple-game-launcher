using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace GameLauncher
{
    public partial class App : Application
    {
        Mutex _mutex;
        EventWaitHandle _showEvent;
        WinForms.NotifyIcon _tray;
        MainWindow _win;

        protected override void OnStartup(StartupEventArgs e)
        {
            string user = Environment.UserName;
            _mutex = new Mutex(true, "GameLauncher.SingleInstance." + user, out bool first);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "GameLauncher.Show." + user);
            if (!first)
            {
                // Already running: ask that instance to show itself.
                _showEvent.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, a) =>
            {
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), $"[{DateTime.Now}] {a.Exception}\r\n\r\n"); } catch { }
                MessageBox.Show(a.Exception.Message, "Simple Game Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                a.Handled = true;
            };

            base.OnStartup(e);
            Store.Load();

            _win = new MainWindow();
            MainWindow = _win;
            SetupTray();

            var listener = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() => _win.ShowLauncher()));
            }) { IsBackground = true };
            listener.Start();

            _win.Initialize();
            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (!startInTray) _win.ShowLauncher();
        }

        void SetupTray()
        {
            System.Drawing.Icon icon = null;
            try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
            _tray = new WinForms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "Simple Game Launcher",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open launcher", null, (s, a) => _win.ShowLauncher());
            menu.Items.Add("Open shortcuts folder", null, (s, a) => OpenFolder(Store.S.Folder));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var startup = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = StartupReg.IsEnabled(), CheckOnClick = true };
            startup.Click += (s, a) => StartupReg.Set(startup.Checked);
            menu.Items.Add(startup);
            menu.Items.Add("Edit settings file", null, (s, a) =>
            {
                Store.Save();
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Store.FilePath}\"") { UseShellExecute = true });
            });
            menu.Items.Add("Reload settings", null, (s, a) => _win.ReloadSettings());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, a) => { if (a.Button == WinForms.MouseButtons.Left) _win.ToggleLauncher(); };
        }

        public static void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        public void ExitApp()
        {
            Store.Save();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_win != null) _win.AllowClose = true;
            Shutdown();
        }
    }
}
