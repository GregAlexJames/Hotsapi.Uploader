﻿using Hotsapi.Uploader.Common;
using NLog;
using Squirrel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Hotsapi.Uploader.Windows
{
    public partial class App : Application, INotifyPropertyChanged
    {
#if DEBUG
        public const bool Debug = true;
#else
        public const bool Debug = false;
#endif
        public event PropertyChangedEventHandler PropertyChanged;

        public NotifyIcon TrayIcon { get; private set; }
        public Manager Manager { get; private set; }
        public static Properties.Settings Settings { get { return Hotsapi.Uploader.Windows.Properties.Settings.Default; } }
        public static string AppExe { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string AppDir { get { return Path.GetDirectoryName(AppExe); } }
        public static string AppFile { get { return Path.GetFileName(AppExe); } }
        public bool UpdateAvailable
        {
            get {
                return _updateAvailable;
            }
            set {
                if (_updateAvailable == value) {
                    return;
                }
                _updateAvailable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateAvailable)));
            }
        }
        public string VersionString
        {
            get {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return $"v{version.Major}.{version.Minor}" + (version.Build == 0 ? "" : $".{version.Build}");
            }
        }
        public bool StartWithWindows
        {
            get {
                var shortcuts = _updateManager.GetShortcutsForExecutable(AppFile, ShortcutLocation.Startup);
                return shortcuts[ShortcutLocation.Startup] != null;
            }
            set {
                _updateManager.CreateShortcutsForExecutable(AppFile, ShortcutLocation.Startup, false, "--autorun");
                return;
                if (value) {
                    _updateManager.CreateShortcutsForExecutable(AppFile, ShortcutLocation.Startup, false, "--autorun");
                } else {
                    _updateManager.RemoveShortcutsForExecutable(AppFile, ShortcutLocation.Startup);
                }
            }
        }

        private static Logger _log = LogManager.GetCurrentClassLogger();
        private UpdateManager _updateManager;
        private bool _updateAvailable;
        private object _lock = new object();


        private void Application_Startup(object sender, StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SetExceptionHandlers();
            _log.Info($"App {VersionString} started");
            if (Settings.UpgradeRequired) {
                RestoreSettings();
            }
            SetupTrayIcon();
            Manager = new Manager(new ReplayStorage($@"{AppDir}\..\replays.xml"));
            // Enable collection modification from any thread
            BindingOperations.EnableCollectionSynchronization(Manager.Files, _lock);
            if (e.Args.Contains("--autorun") && Settings.MinimizeToTray) {
                TrayIcon.Visible = true;
            } else {
                new MainWindow().Show();
            }
            Manager.Start();
            //Check for updates on startup and then every hour
            CheckForUpdates();
            new DispatcherTimer() {
                Interval = TimeSpan.FromHours(1),
                IsEnabled = true
            }.Tick += (_, __) => CheckForUpdates();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            BackupSettings();
            _updateManager?.Dispose();
            TrayIcon?.Dispose();
        }

        private void SetupTrayIcon()
        {
            TrayIcon = new NotifyIcon {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Visible = false
            };
            TrayIcon.Click += (o, e) => {
                new MainWindow().Show();
                TrayIcon.Visible = false;
            };
        }

        private async void CheckForUpdates()
        {
            if (Debug || !Settings.AutoUpdate) {
                return;
            }
            try {
                if (_updateManager == null) {
                    _updateManager = await UpdateManager.GitHubUpdateManager(Settings.UpdateRepository);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartWithWindows)));
                }
                var release = await _updateManager.UpdateApp();
                if (release != null) {
                    UpdateAvailable = true;
                    BackupSettings();
                }
            }
            catch (Exception e) {
                _log.Warn(e, "Error checking for updates");
            }
        }

        /// <summary>
        /// Make a backup of our settings.
        /// Used to persist settings across updates.
        /// </summary>
        public static void BackupSettings()
        {
            Settings.Save();
            string settingsFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
            string destination = $@"{AppDir}\..\last.config";
            File.Copy(settingsFile, destination, true);
        }

        /// <summary>
        /// Restore our settings backup if any.
        /// Used to persist settings across updates and upgrade settings format.
        /// </summary>
        public static void RestoreSettings()
        {
            string destFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
            string sourceFile = $@"{AppDir}\..\last.config";

            if (File.Exists(sourceFile)) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)); // Create directory if needed
                    File.Copy(sourceFile, destFile, true);
                    Settings.Reload();
                    Settings.Upgrade();
                }
                catch (Exception e) {
                    _log.Error(e, "Error upgrading settings");
                }
            }

            Settings.UpgradeRequired = false;
            Settings.Save();
        }

        /// <summary>
        /// Log all unhandled exceptions
        /// </summary>
        private void SetExceptionHandlers()
        {
            DispatcherUnhandledException += (o, e) => LogAndDisplay(e.Exception, "dispatcher");
            TaskScheduler.UnobservedTaskException += (o, e) => LogAndDisplay(e.Exception, "task");
            AppDomain.CurrentDomain.UnhandledException += (o, e) => LogAndDisplay(e.ExceptionObject as Exception, "domain");
        }

        private void LogAndDisplay(Exception e, string type)
        {
            _log.Error(e, $"Unhandled {type} exception");
            try {
                MessageBox.Show(e.ToString(), $"Unhandled {type} exception");
            }
            catch { /* probably not gui thread */ }
        }
    }
}