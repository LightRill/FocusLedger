using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusLedger.Core;
using FocusLedger.Storage;
using Forms = System.Windows.Forms;

namespace FocusLedger.App;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    private LedgerStore? store;
    private Tracker? tracker;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private MainWindow? window;
    private DataLocation location = null!;
    internal string DataDirectory { get; private set; } = "";
    internal string DefaultDataDirectory => location.DefaultDirectory;
    internal string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var smokeIndex = Array.IndexOf(e.Args, "--self-check");
            var runtimeIndex = Array.IndexOf(e.Args, "--runtime-check");
            var smoke = smokeIndex >= 0;
            if (smoke) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var runtimeCheck = runtimeIndex >= 0;
            if (smoke && e.Args.Length <= smokeIndex + 1) throw new ArgumentException("--self-check 需要独立的输出目录。");
            if (runtimeCheck && e.Args.Length <= runtimeIndex + 1) throw new ArgumentException("--runtime-check 需要独立的输出目录。");
            var isolated = smoke || runtimeCheck;
            var testDirectory = isolated ? Path.GetFullPath(e.Args[(smoke ? smokeIndex : runtimeIndex) + 1]) : null;
            location = new DataLocation(isolated ? Path.Combine(testDirectory!, "test-program") : AppContext.BaseDirectory,
                isolated ? Path.Combine(testDirectory!, "test-profile") : null);
            DataDirectory = isolated ? testDirectory! : location.Load();
            var changedAtStartup = false;
            try { DataDirectoryMigration.EnsureWritable(DataDirectory); }
            catch (Exception ex) when (!isolated && ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (MessageBox.Show($"当前数据目录不可写或不可用：\n{DataDirectory}\n\n请选择其他目录继续使用。原记录会保留。", "选择数据目录", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    throw new OperationCanceledException();
                var folder = new Microsoft.Win32.OpenFolderDialog { Title = "选择可写的数据目录" };
                if (folder.ShowDialog() != true) throw new OperationCanceledException();
                DataDirectory = DataLocation.Normalize(folder.FolderName);
                DataDirectoryMigration.EnsureWritable(DataDirectory);
                changedAtStartup = true;
            }
            instance = AcquireDataMutex(DataDirectory);
            ownsMutex = true;
            if (!isolated && !changedAtStartup && !location.IsConfigured && !DataDirectoryMigration.HasDatabase(DataDirectory))
            {
                var legacy = FindLegacyDirectory();
                if (legacy is not null && !DataLocation.Same(legacy, DataDirectory))
                {
                    using var legacyLock = AcquireDataMutex(legacy);
                    try
                    {
                        DataDirectoryMigration.CopyToEmptyDirectory(Path.Combine(legacy, "focusledger.db"), DataDirectory, SettingsFile.Load(Path.Combine(legacy, "settings.json")));
                        location.Save(DataDirectory);
                    }
                    finally { legacyLock.ReleaseMutex(); }
                }
            }
            var settings = SettingsFile.Load(SettingsPath);
            if (!File.Exists(SettingsPath)) SettingsFile.Save(SettingsPath, settings);
            store = new LedgerStore(Path.Combine(DataDirectory, "focusledger.db"));
            if (changedAtStartup) location.Save(DataDirectory);
            ThemeManager.Apply(settings.Theme);
            tracker = new Tracker(store, settings);
            if (smoke) SeedDemo(store);
            window = new MainWindow(store, tracker, SettingsPath, DataDirectory);
            MainWindow = window;
            if (smoke)
            {
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -15000;
                window.Top = -15000;
                window.Show();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunSelfCheck());
                return;
            }
            SetupTray(!runtimeCheck);
            tracker.Start();
            if (runtimeCheck)
            {
                var finish = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                finish.Tick += (_, _) =>
                {
                    finish.Stop(); tracker.Poll();
                    var rows = store.ReadAll();
                    var success = rows.Count > 0 && !tracker.Status.StartsWith("保存失败");
                    File.WriteAllText(Path.Combine(DataDirectory, "runtime-check.txt"),
                        $"{(success ? "PASS" : "FAIL")}\nStatus: {tracker.Status}\nStored segments: {rows.Count}\nObserved seconds: {rows.Sum(s => s.Seconds):0.###}\nIntegrity: {store.IntegrityCheck()}\nTray initialized, collector timer ran, session/power event subscriptions succeeded.\n");
                    Shutdown(success ? 0 : 1);
                };
                finish.Start();
                return;
            }
            if (!e.Args.Contains("--minimized")) window.Show();
            SessionEnding += (_, _) => { window.AllowClose = true; ExitApp(); };
        }
        catch (OperationCanceledException) { Shutdown(); }
        catch (Exception ex)
        {
            if (e.Args.Contains("--self-check") || e.Args.Contains("--runtime-check"))
            {
                if (DataDirectory.Length > 0) File.WriteAllText(Path.Combine(DataDirectory, "error.txt"), ex.ToString());
            }
            else MessageBox.Show("启动失败，现有数据未删除。\n\n" + ex.Message, "FocusLedger", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? FindLegacyDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "focusledger.root")))
            {
                var legacy = Path.Combine(current.FullName, "data");
                return DataDirectoryMigration.HasDatabase(legacy) ? legacy : null;
            }
            current = current.Parent;
        }
        return null;
    }

    private static Mutex AcquireDataMutex(string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataLocation.Normalize(path).ToUpperInvariant())))[..20];
        var handle = new Mutex(false, "Local\\FocusLedger_" + key);
        try
        {
            try { if (handle.WaitOne(0)) return handle; }
            catch (AbandonedMutexException) { return handle; }
            throw new IOException("该数据目录已被另一个 FocusLedger 实例使用，请先从其托盘退出。");
        }
        catch { handle.Dispose(); throw; }
    }

    internal void ApplySettings(Settings settings, string targetDirectory, bool useExisting)
    {
        settings.Validate();
        targetDirectory = DataLocation.Normalize(targetDirectory);
        tracker!.Poll();
        if (DataLocation.Same(DataDirectory, targetDirectory))
        {
            SettingsFile.Save(SettingsPath, settings);
            tracker.Settings = settings; tracker.Reset(); tracker.Poll();
            ThemeManager.Apply(settings.Theme);
            return;
        }
        var nextMutex = AcquireDataMutex(targetDirectory);
        LedgerStore? nextStore = null;
        var committed = false;
        try
        {
            DataDirectoryMigration.EnsureWritable(targetDirectory);
            Settings nextSettings;
            if (useExisting)
            {
                if (!DataDirectoryMigration.HasDatabase(targetDirectory)) throw new FileNotFoundException("目标目录的数据库已不存在。");
                nextSettings = SettingsFile.Load(Path.Combine(targetDirectory, "settings.json"));
            }
            else
            {
                DataDirectoryMigration.CopyToEmptyDirectory(store!.DatabasePath, targetDirectory, settings);
                nextSettings = settings;
            }
            nextStore = new LedgerStore(Path.Combine(targetDirectory, "focusledger.db"));
            if (nextStore.IntegrityCheck() != "ok") throw new InvalidDataException("目标数据库完整性检查失败，仍使用原目录。");
            _ = nextStore.Read(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
            if (!File.Exists(Path.Combine(targetDirectory, "settings.json"))) SettingsFile.Save(Path.Combine(targetDirectory, "settings.json"), nextSettings);
            // Commit the location only after the new data can be opened and read.
            location.Save(targetDirectory);
            var oldStore = store; var oldMutex = instance;
            store = nextStore; nextStore = null;
            instance = nextMutex; DataDirectory = targetDirectory;
            tracker.SwitchStore(store, nextSettings);
            ThemeManager.Apply(nextSettings.Theme);
            window!.ChangeDataSource(store, SettingsPath, DataDirectory);
            committed = true;
            oldStore?.Dispose(); oldMutex?.ReleaseMutex(); oldMutex?.Dispose();
            tracker.Poll();
        }
        finally
        {
            nextStore?.Dispose();
            if (!committed) { nextMutex.ReleaseMutex(); nextMutex.Dispose(); tracker.Reset(); }
        }
    }

    private void SetupTray(bool visible = true)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Font = new System.Drawing.Font("Microsoft YaHei UI", 10), ShowImageMargin = false,
            Padding = new Forms.Padding(6), Renderer = new TrayMenuStyle.Renderer()
        };
        var title = new Forms.ToolStripLabel("FocusLedger · 专注时账") { Font = new System.Drawing.Font("Microsoft YaHei UI", 9) };
        menu.Items.Add(title);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开概览", null, (_, _) => { ShowWindow(); window!.ShowOverviewForCheck(); });
        var pause = menu.Items.Add("暂停记录", null, (_, _) => tracker!.TogglePause());
        menu.Items.Add("设置", null, (_, _) => { ShowWindow(); window!.OpenSettings(); });
        menu.Items.Add("备份数据", null, (_, _) => { ShowWindow(); window!.OpenBackup(); });
        menu.Items.Add("打开数据目录", null, (_, _) => Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => TrayMenuStyle.Apply(menu);
        TrayMenuStyle.Apply(menu);
        using (var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/focusledger.ico")).Stream)
        using (var original = new System.Drawing.Icon(stream)) trayIcon = new System.Drawing.Icon(original, Forms.SystemInformation.SmallIconSize);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "FocusLedger · 专注时账", Visible = visible, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow();
        tracker!.Updated += () =>
        {
            pause.Text = tracker.Paused ? "恢复记录" : "暂停记录";
            var text = "FocusLedger · " + tracker.Status;
            tray.Text = text.Length > 63 ? text[..63] : text;
        };
    }
    private void ShowWindow() { window!.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
    internal void ExitApp() { tracker?.Poll(); if (window is not null) window.AllowClose = true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.ContextMenuStrip?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); tracker?.Dispose(); store?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }

    private static void SeedDemo(LedgerStore db)
    {
        if (db.ReadAll().Count > 0) return;
        var yesterday = DateTime.Today.AddDays(-1);
        var zone = TimeZoneInfo.Local;
        foreach (var item in new[] { (9d, 1.4, "VS Code"), (10.7, .6, "Codex"), (13.3, 1.7, "VS Code"), (15.4, .7, "ChatGPT") })
        {
            var start = Reports.ToUtc(yesterday.AddMinutes(Math.Round(item.Item1 * 60)), zone);
            db.Append(new(0, start, start.AddMinutes(Math.Round(item.Item2 * 60)), item.Item3, SegmentKind.Active));
        }
        db.SaveManual(new(0, Reports.ToUtc(yesterday.AddHours(16.5), zone), Reports.ToUtc(yesterday.AddHours(17), zone), "Zotero", SegmentKind.Manual, "演示：阅读文献"));
    }

    private void RunSelfCheck()
    {
        var checkRoot = DataDirectory;
        try
        {
            window!.SetPeriodForCheck(DateTime.Today.AddDays(-1), Period.Day);
            window.CheckTimelineFilters(checkRoot);
            SaveImage(window, Path.Combine(DataDirectory, "today.png"));
            LayoutChecks.CheckTable(window.Timeline);
            window.SetPeriodForCheck(DateTime.Today, Period.Year);
            SaveImage(window, Path.Combine(DataDirectory, "year.png"));
            var settingsView = window.OpenSettings();
            settingsView.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(window.UpdateLayout));
            settingsView.CheckProcessPicker();
            File.WriteAllLines(Path.Combine(checkRoot, "visible-processes.txt"), WindowsActivity.VisibleProcesses());
            SaveImage(window, Path.Combine(DataDirectory, "settings.png"));
            LayoutChecks.CheckTable(settingsView.RulesGrid);
            LayoutChecks.CheckEditor(settingsView);
            settingsView.ShowDataTab(); SaveImage(window, Path.Combine(DataDirectory, "data-settings.png"));
            settingsView.ShowThemeTab(); SaveImage(window, Path.Combine(DataDirectory, "themes.png"));
            settingsView.DefaultIdleInput.Text = "7";
            settingsView.ThemeChoice.SelectedIndex = 2;
            settingsView.SaveForCheck();
            if (tracker!.Settings.Theme != "Dark" || tracker.Settings.DefaultIdleMinutes != 7 || SettingsFile.Load(SettingsPath).Theme != "Dark")
                throw new Exception("Inline settings failed to save.");
            SaveImage(window, Path.Combine(DataDirectory, "settings-dark.png"));
            LayoutChecks.CheckTable(window.OpenSettings().RulesGrid);
            var previousWidth = window.Width; var previousHeight = window.Height;
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            SaveImage(window, Path.Combine(DataDirectory, "settings-compact.png"));
            LayoutChecks.CheckTable(window.OpenSettings().RulesGrid);
            window.Width = previousWidth; window.Height = previousHeight;
            window.OpenBackup();
            var backup = window.CreateBackup(Path.Combine(checkRoot, "backups"));
            using (var db = new LedgerStore(Path.Combine(backup, "focusledger.db")))
                if (db.IntegrityCheck() != "ok" || db.ReadAll().Count == 0 || SettingsFile.Load(Path.Combine(backup, "settings.json")).Theme != "Dark") throw new Exception("Inline backup failed.");
            SaveImage(window, Path.Combine(DataDirectory, "backup-dark.png"));
            window.OpenManualForCheck(); SaveImage(window, Path.Combine(DataDirectory, "manual-dark.png"));
            window.ShowOverviewForCheck();
            window.SetPeriodForCheck(DateTime.Today.AddDays(-1), Period.Day);
            SaveImage(window, Path.Combine(DataDirectory, "overview-dark.png"));
            window.CheckChartDetails(name => SaveImage(window, Path.Combine(checkRoot, name)));
            window.OverviewPage.ScrollToBottom();
            SaveImage(window, Path.Combine(DataDirectory, "timeline-filters-dark.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            window.OverviewPage.ScrollToBottom();
            SaveImage(window, Path.Combine(DataDirectory, "timeline-filters-compact.png"));
            window.Width = previousWidth; window.Height = previousHeight;
            window.OverviewPage.ScrollToTop();
            ThemeManager.Apply("Ocean"); SaveImage(window, Path.Combine(DataDirectory, "overview-ocean.png"));
            ThemeManager.Apply("Forest");
            var reset = SettingsFile.Clone(tracker.Settings); reset.DefaultIdleMinutes = 3; reset.Theme = "Forest";
            ApplySettings(reset, DataDirectory, false);
            if (Application.Current.Windows.Count != 1) throw new Exception("Page navigation opened extra windows.");
            SetupTray(false);
            foreach (var palette in new[] { "Dark", "Ocean", "Forest" })
            {
                ThemeManager.Apply(palette);
                var menu = tray!.ContextMenuStrip!;
                TrayMenuStyle.Apply(menu);
                menu.CreateControl(); menu.PerformLayout();
                using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                bitmap.Save(Path.Combine(checkRoot, $"tray-{palette}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            ChromeChecks.Run(window);
            var nativeSample = WindowsActivity.Capture(tracker.Settings, false, false);
            tracker.TogglePause();
            var oldDatabase = store!.DatabasePath;
            var expected = store.ReadAll().Where(s => s.IsEffective).Sum(s => s.Seconds);
            var changedSettings = SettingsFile.Clone(tracker.Settings); changedSettings.ApplyIdleMinutesToAll(5);
            var migratedDirectory = Path.Combine(checkRoot, "migration-target");
            ApplySettings(changedSettings, migratedDirectory, false);
            if (!File.Exists(oldDatabase) || !DataLocation.Same(location.Load(), migratedDirectory) || tracker.Settings.DefaultIdleMinutes != 5
                || Math.Abs(store.ReadAll().Where(s => s.IsEffective).Sum(s => s.Seconds) - expected) > .001)
                throw new InvalidOperationException("End-to-end data migration failed.");
            // Exercise switching to an existing ledger as well as copying to a new folder.
            ApplySettings(changedSettings, checkRoot, true);
            if (!DataLocation.Same(DataDirectory, checkRoot) || tracker.Settings.DefaultIdleMinutes != 3)
                throw new InvalidOperationException("Switching to existing data failed.");
            File.WriteAllText(Path.Combine(checkRoot, "self-check.txt"), $"PASS\nDatabase integrity: {store.IntegrityCheck()}\nNative capture: {nativeSample.Kind}\nUI: inline overview, settings, themes, backup, manual entry rendered; three palettes; settings save and backup verified; single window; custom tray icon loaded\nLayout: table display and editor vertical centers checked in normal/compact layouts\nChrome: caption and resize hit tests, minimize/maximize/restore/hide, themed scrollbar page-down and mouse wheel passed\nData migration and switching existing ledger passed\nNo tracking timer started\n");
            window.AllowClose = true;
            Shutdown(0);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(checkRoot, "error.txt"), ex.ToString()); Shutdown(1); }
    }
    private static void SaveImage(Window target, string path)
    {
        foreach (var element in Descendants(target).OfType<UIElement>()) element.InvalidateVisual();
        target.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(target.UpdateLayout));
        target.UpdateLayout();
        var content = (FrameworkElement)target.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class WindowCloser(Window window) : IDisposable
    {
        public Window Window => window;
        public void Dispose() => window.Close();
    }
}
