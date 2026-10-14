using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Shapes;
using Path = System.IO.Path;
using FocusLedger.Core;
using FocusLedger.Storage;
using Microsoft.Win32;

namespace FocusLedger.App;

public partial class MainWindow : Window
{
    private LedgerStore store;
    private readonly Tracker tracker;
    private string settingsPath, dataDirectory;
    private readonly TimeZoneInfo zone = TimeZoneInfo.Local;
    private List<SummaryBucket> buckets = [];
    private List<Segment> visible = [];
    private Dictionary<long, Segment> timelineOriginals = [];
    private bool updatingTimelineFilters;
    private bool initialized;
    private DateTime lastRefresh = DateTime.MinValue;
    public bool AllowClose { get; set; }
    private Period CurrentPeriod => (Period)Math.Max(0, PeriodSelect.SelectedIndex);
    private PeriodRange CurrentRange => Reports.Range(DateSelect.SelectedDate ?? DateTime.Today, CurrentPeriod, zone);

    internal MainWindow(LedgerStore store, Tracker tracker, string settingsPath, string dataDirectory)
    {
        this.store = store; this.tracker = tracker; this.settingsPath = settingsPath; this.dataDirectory = dataDirectory;
        InitializeComponent();
        TrendCanvas.Background = Brushes.Transparent;
        TrendCanvas.MouseMove += (_, e) => UpdateTrendDetail(e.GetPosition(TrendCanvas));
        TrendCanvas.MouseLeave += (_, _) => HideChartDetail();
        OverviewPage.ScrollChanged += (_, _) => HideChartDetail();
        Deactivated += (_, _) => HideChartDetail();
        StateChanged += (_, _) => UpdateWindowState();
        SizeChanged += (_, _) => { UpdateMaximizedInsets(); HideChartDetail(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { UpdateMaximizedInsets(); Refresh(); }));
        DateSelect.SelectedDate = DateTime.Today;
        initialized = true;
        ThemeManager.Changed += Refresh;
        ShowPage(0);
        tracker.Updated += OnTrackerUpdated;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Refresh();
    }

    private void OnTrackerUpdated()
    {
        StatusText.Text = tracker.Status;
        LastAppText.Text = "最近记录：" + tracker.LastApp;
        PauseButton.Content = tracker.Paused ? "恢复记录" : "暂停记录";
        var refreshSeconds = CurrentPeriod switch { Period.Year => 60, Period.Month => 20, Period.Week => 10, _ => 5 };
        if (IsVisible && (DateTime.Now - lastRefresh).TotalSeconds >= refreshSeconds)
        {
            if (CurrentPeriod == Period.Day && lastRefresh.Date < DateTime.Today && DateSelect.SelectedDate?.Date == lastRefresh.Date)
                DateSelect.SelectedDate = DateTime.Today;
            Refresh();
        }
    }

    private void Repository_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/LightRill/FocusLedger") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "无法打开默认浏览器，请手动访问：https://github.com/LightRill/FocusLedger", "打开项目仓库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M 0 3 H 7 V 10 H 0 Z M 3 3 V 0 H 10 V 7 H 7" : "M 0 0 H 10 V 10 H 0 Z");
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(UpdateMaximizedInsets));
    }
    private void UpdateMaximizedInsets()
    {
        if (WindowState != WindowState.Maximized) { WindowLayout.Margin = new Thickness(0); return; }
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == 0 || PresentationSource.FromVisual(this) is null) return;
        // Windows keeps an invisible resize frame outside the work area when maximized.
        // Convert the actual overhang to DIPs so content stays inside on every monitor/DPI.
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);
        WindowLayout.Margin = new Thickness(Math.Max(0, (work.Left - topLeft.X) / dpi.DpiScaleX),
            Math.Max(0, (work.Top - topLeft.Y) / dpi.DpiScaleY),
            Math.Max(0, (bottomRight.X - work.Right) / dpi.DpiScaleX),
            Math.Max(0, (bottomRight.Y - work.Bottom) / dpi.DpiScaleY));
    }

    internal void ChangeDataSource(LedgerStore nextStore, string nextSettingsPath, string nextDataDirectory)
    {
        store = nextStore; settingsPath = nextSettingsPath; dataDirectory = nextDataDirectory;
        SettingsPage.Content = null; BackupSource.Text = dataDirectory;
        BackupFolder.Text = Path.Combine(dataDirectory, "backups");
        Refresh();
    }

    private void Refresh()
    {
        if (!initialized) return;
        try
        {
            lastRefresh = DateTime.Now;
            var range = CurrentRange;
            RangeText.Text = range.Label;
            var all = store.Read(range.Start, range.End);
            var clipped = Reports.Clip(all, range.Start, range.End);
            visible = clipped.Where(s => s.IsEffective).ToList();
            var total = visible.Sum(s => s.Seconds);
            TotalText.Text = DurationText.Format(total);
            AutoText.Text = DurationText.Format(visible.Where(s => s.Kind == SegmentKind.Active).Sum(s => s.Seconds));
            ManualText.Text = DurationText.Format(visible.Where(s => s.Kind == SegmentKind.Manual).Sum(s => s.Seconds));
            LongestText.Text = "最长连续投入 " + DurationText.Format(Reports.LongestEffective(visible));
            buckets = Reports.Buckets(clipped, range, CurrentPeriod, zone);
            ChartHint.Text = CurrentPeriod switch { Period.Day => "每小时", Period.Year => "每月", _ => "每天" };
            DrawChart();
            DrawBreakdown(total);
            DrawYearHeatmap(clipped, range);
            // Editing receives the original interval, not a clipped cross-midnight copy.
            timelineOriginals = all.ToDictionary(s => s.Id);
            UpdateTimelineApps();
            ApplyTimelineFilters();
        }
        catch (Exception ex) { FooterText.Text = "读取失败：" + ex.Message; }
    }

    private sealed record TimelineAppChoice(string? App, string Label)
    {
        public override string ToString() => Label;
    }

    private void UpdateTimelineApps()
    {
        var selectedApp = (TimelineAppFilter.SelectedItem as TimelineAppChoice)?.App;
        var names = visible.Select(s => s.App).Distinct(StringComparer.Ordinal).ToList();
        // Keep an explicit selection when the next date has no records for that app.
        if (selectedApp is not null && !names.Contains(selectedApp)) names.Add(selectedApp);
        var choices = new List<TimelineAppChoice> { new(null, "全部程序") };
        choices.AddRange(names.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).Select(s => new TimelineAppChoice(s, s)));
        updatingTimelineFilters = true;
        try
        {
            TimelineAppFilter.ItemsSource = choices;
            TimelineAppFilter.SelectedItem = choices.First(c => c.App == selectedApp);
        }
        finally { updatingTimelineFilters = false; }
    }

    private void TimelineFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !updatingTimelineFilters) ApplyTimelineFilters();
    }

    private void ApplyTimelineFilters()
    {
        var selectedId = (Timeline.SelectedItem as TimelineRow)?.Original.Id;
        var app = (TimelineAppFilter.SelectedItem as TimelineAppChoice)?.App;
        var minimumSeconds = int.Parse((string)((ComboBoxItem)TimelineDurationFilter.SelectedItem).Tag);
        var matches = visible.Where(s => (app is null || s.App == app) && s.Seconds >= minimumSeconds)
            .OrderByDescending(s => s.Start).ToList();
        var rows = matches.Take(1000).Select(s => new TimelineRow(timelineOriginals[s.Id], s, zone)).ToList();
        Timeline.ItemsSource = rows;
        if (selectedId is not null) Timeline.SelectedItem = rows.FirstOrDefault(r => r.Original.Id == selectedId);
        TimelineTitle.Text = $"时间明细 · {matches.Count}/{visible.Count} 段";
        TimelineTitle.ToolTip = matches.Count > 1000 ? "列表仅显示符合条件的最近 1000 段；CSV 导出包含全部有效记录。" : null;
        EmptyHint.Text = visible.Count == 0 ? "该周期暂无有效记录。" : "没有符合筛选条件的记录，可切换程序或降低最短时长。";
        EmptyHint.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawChart()
    {
        if (!initialized || TrendCanvas.ActualWidth < 20 || buckets.Count == 0) return;
        TrendCanvas.Children.Clear();
        trendBars.Clear();
        var width = TrendCanvas.ActualWidth;
        var max = Math.Max(CurrentPeriod == Period.Day ? 3600 : 1800, buckets.Max(b => b.Seconds));
        const double chartHeight = 118;
        var unit = width / buckets.Count;
        var baseline = new Line { X1 = 0, X2 = width, Y1 = chartHeight, Y2 = chartHeight, Stroke = ThemeManager.Brush("Line"), StrokeThickness = 1 };
        TrendCanvas.Children.Add(baseline);
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            var barHeight = b.Seconds / max * (chartHeight - 12);
            var bar = new Border { Width = Math.Max(3, unit * .58), Height = Math.Max(3, barHeight), CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = ThemeManager.Brush(b.Seconds > 0 ? "Accent" : "Line") };
            Canvas.SetLeft(bar, i * unit + unit * .21); Canvas.SetTop(bar, chartHeight - bar.Height); TrendCanvas.Children.Add(bar);
            trendBars.Add((i, new Rect(Canvas.GetLeft(bar), Canvas.GetTop(bar), bar.Width, bar.Height)));
            if (buckets.Count <= 12 || i % (buckets.Count > 25 ? 5 : 3) == 0)
            {
                var label = new TextBlock { Text = b.Label, FontSize = 10, Foreground = ThemeManager.Brush("Muted") };
                Canvas.SetLeft(label, i * unit + 1); Canvas.SetTop(label, chartHeight + 9); TrendCanvas.Children.Add(label);
            }
        }
        var top = new TextBlock { Text = DurationText.Short(max), FontSize = 10, Foreground = ThemeManager.Brush("Muted") };
        Canvas.SetLeft(top, 0); Canvas.SetTop(top, 0); TrendCanvas.Children.Add(top);
        // Refreshes rebuild bars, but the canvas keeps the hover area and pointer state.
        if (TrendCanvas.IsMouseOver) UpdateTrendDetail(Mouse.GetPosition(TrendCanvas));
    }

    private void DrawBreakdown(double total)
    {
        AppBreakdown.Children.Clear();
        var groups = visible.GroupBy(s => s.App).Select(g => (App: g.Key, Seconds: g.Sum(s => s.Seconds))).OrderByDescending(g => g.Seconds).ToList();
        if (groups.Count == 0) AppBreakdown.Children.Add(new TextBlock { Text = "开始一段专注，\n这里会逐渐充实。", Foreground = ThemeManager.Brush("Muted"), LineHeight = 25, Margin = new Thickness(0, 15, 0, 0) });
        foreach (var g in groups.Take(6))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var value = new TextBlock { Text = DurationText.Short(g.Seconds), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = g.App, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            AppBreakdown.Children.Add(row);
            AppBreakdown.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, total), Value = g.Seconds, Height = 5, BorderThickness = new Thickness(0), Foreground = ThemeManager.Brush("Accent"), Background = ThemeManager.Brush("Soft"), Margin = new Thickness(0, 0, 0, 12) });
        }
        if (groups.Count > 6) AppBreakdown.Children.Add(new TextBlock { Text = $"另有 {groups.Count - 6} 个软件，详见 CSV", FontSize = 11 });
    }

    private void DrawYearHeatmap(List<Segment> all, PeriodRange range)
    {
        YearHeatmapCard.Visibility = CurrentPeriod == Period.Year ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentPeriod != Period.Year) return;
        YearHeatmap.Children.Clear();
        // Monthly blocks keep dates legible and avoid implying unobserved days were zero.
        var start = TimeZoneInfo.ConvertTime(range.Start, zone).Date;
        for (int month = 0; month < 12; month++)
        {
            var first = start.AddMonths(month);
            var dpi = VisualTreeHelper.GetDpi(this);
            var days = CreateHeatmapDays(first, all, dpi.DpiScaleX, dpi.DpiScaleY);
            var block = new StackPanel { Width = days.Width, Margin = new Thickness(0, 0, 12, 15) };
            block.Children.Add(new TextBlock { Text = $"{first.Month} 月", FontSize = 11, Margin = new Thickness(0, 0, 0, 7) });
            block.Children.Add(days); YearHeatmap.Children.Add(block);
        }
    }

    private Canvas CreateHeatmapDays(DateTime first, List<Segment> all, double dpiX, double dpiY)
    {
        // Fixed physical-pixel cell sizes and gaps avoid fractional 130 / 7 columns.
        var cellWidth = Math.Round(16 * dpiX) / dpiX;
        var cellHeight = Math.Round(14 * dpiY) / dpiY;
        var gapX = Math.Round(3 * dpiX) / dpiX;
        var gapY = Math.Round(3 * dpiY) / dpiY;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var count = DateTime.DaysInMonth(first.Year, first.Month);
        var rows = (offset + count + 6) / 7;
        var days = new Canvas { Width = cellWidth * 7 + gapX * 6, Height = rows * cellHeight + (rows - 1) * gapY,
            UseLayoutRounding = false, SnapsToDevicePixels = true };
        for (int day = 0; day < count; day++)
        {
            var date = first.AddDays(day);
            var parts = Reports.Clip(all, Reports.ToUtc(date, zone), Reports.ToUtc(date.AddDays(1), zone));
            var seconds = parts.Where(s => s.IsEffective).Sum(s => s.Seconds);
            var cell = new Border { Width = cellWidth, Height = cellHeight, CornerRadius = new CornerRadius(2),
                Background = ThemeManager.Heat(seconds) };
            cell.MouseMove += (_, e) =>
            {
                if (seconds > 0) ShowChartDetail(date.ToString("yyyy 年 M 月 d 日"), seconds, e.GetPosition(ChartOverlay));
                else HideChartDetail();
            };
            cell.MouseLeave += (_, _) => HideChartDetail();
            Canvas.SetLeft(cell, (offset + day) % 7 * (cellWidth + gapX));
            Canvas.SetTop(cell, (offset + day) / 7 * (cellHeight + gapY));
            days.Children.Add(cell);
        }
        return days;
    }

    private void Today_Click(object sender, RoutedEventArgs e) => ShowPage(0);
    internal void SetPeriodForCheck(DateTime date, Period period) { DateSelect.SelectedDate = date; PeriodSelect.SelectedIndex = (int)period; Refresh(); }
    private void Period_Changed(object sender, RoutedEventArgs e) => Refresh();
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
    private void Previous_Click(object sender, RoutedEventArgs e) => MovePeriod(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MovePeriod(1);
    private void MovePeriod(int direction)
    {
        var date = DateSelect.SelectedDate ?? DateTime.Today;
        DateSelect.SelectedDate = CurrentPeriod switch { Period.Week => date.AddDays(7 * direction), Period.Month => date.AddMonths(direction), Period.Year => date.AddYears(direction), _ => date.AddDays(direction) };
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => tracker.TogglePause();
    private void Add_Click(object sender, RoutedEventArgs e) => EditSegment(null);
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Timeline.SelectedItem is TimelineRow row) EditSegment(row.Original); }
    private void Timeline_DoubleClick(object sender, MouseButtonEventArgs e) { if (Timeline.SelectedItem is TimelineRow row) EditSegment(row.Original); }
    private void EditSegment(Segment? segment)
    {
        var view = new ManualView(tracker.Settings, segment, DateSelect.SelectedDate ?? DateTime.Today, zone);
        view.Cancelled += () => ShowPage(0);
        view.Saved += () =>
        {
            tracker.Poll(); store.SaveManual(view.Result!, segment?.Id); tracker.Reset(); Refresh(); ShowPage(0);
            FooterText.Text = "时间已保存；重叠部分已替换，没有重复计时。";
        };
        ManualHost.Content = view; ShowPage(3);
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedItem is not TimelineRow row) return;
        if (MessageBox.Show(this, $"删除 {row.App} 的整段记录？\n{row.Original.Start.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {row.Original.End.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n跨日期片段也会整段删除。", "删除记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Guard(() => { tracker.Poll(); store.Delete(row.Original.Id); tracker.Reset(); Refresh(); }, "记录已删除，修改历史已保留。");
    }
    private void ShowPage(int index)
    {
        HideChartDetail();
        OverviewPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ManualPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        var buttons = new[] { OverviewNav, SettingsNav, BackupNav };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].SetResourceReference(BackgroundProperty, i == index ? "Accent" : "Sidebar");
            buttons[i].SetResourceReference(ForegroundProperty, i == index ? "OnAccent" : "SidebarInk");
            buttons[i].SetResourceReference(BorderBrushProperty, i == index ? "Accent" : "Sidebar");
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    internal SettingsView OpenSettings()
    {
        var app = (App)Application.Current;
        if (SettingsPage.Content is not SettingsView view)
        {
            view = new SettingsView(tracker.Settings, dataDirectory, app.DefaultDataDirectory);
            view.Cancelled += () => { SettingsPage.Content = null; ShowPage(0); };
            view.Saved += () =>
            {
                var useExisting = !DataLocation.Same(view.SelectedDataDirectory, dataDirectory) && DataDirectoryMigration.HasDatabase(view.SelectedDataDirectory);
                if (useExisting && MessageBox.Show(this, "目标目录已有记录。是否切换到该目录的记录和设置？当前数据会保留，本次设置修改不会应用到目标目录。", "使用已有数据", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                app.ApplySettings(view.Result!, view.SelectedDataDirectory, useExisting);
                if (view.StartupEnabled is bool startup) StartupRegistration.SetEnabled(startup);
                SettingsPage.Content = null;
                var saved = OpenSettings(); saved.ErrorText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                saved.ErrorText.Text = "已保存，设置已生效。";
                Refresh();
            };
            SettingsPage.Content = view;
        }
        ShowPage(1); return view;
    }
    private void Backup_Click(object sender, RoutedEventArgs e) => OpenBackup();
    internal void OpenBackup()
    {
        BackupSource.Text = dataDirectory;
        if (string.IsNullOrWhiteSpace(BackupFolder.Text)) BackupFolder.Text = Path.Combine(dataDirectory, "backups");
        ShowPage(2);
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dataDirectory) { UseShellExecute = true }), "已打开数据目录。");
    private void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择备份保存目录" };
        if (picker.ShowDialog(this) == true) BackupFolder.Text = picker.FolderName;
    }
    internal string CreateBackup(string directory)
    {
        var target = Path.Combine(DataLocation.Normalize(directory), $"FocusLedger-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}");
        tracker.Poll();
        DataDirectoryMigration.CopyToEmptyDirectory(store.DatabasePath, target, tracker.Settings);
        return target;
    }
    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try { var target = CreateBackup(BackupFolder.Text); BackupResult.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); BackupResult.Text = "备份完成 · 数据库完整性已检查\n" + target; }
        catch (Exception ex) { BackupResult.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); BackupResult.Text = "备份未完成：" + ex.Message; }
    }
    internal void OpenManualForCheck() => EditSegment(null);
    internal void ShowOverviewForCheck() => ShowPage(0);
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { Title = "导出当前周期的有效时间", Filter = "CSV 表格|*.csv", FileName = $"FocusLedger-{CurrentPeriod}-{DateSelect.SelectedDate:yyyyMMdd}.csv" };
        if (save.ShowDialog(this) != true) return;
        Guard(() => { Refresh(); LedgerStore.ExportCsv(save.FileName, visible, zone); }, "CSV 已导出，可用 Excel 打开。");
    }
    private void Guard(Action action, string success)
    {
        try { action(); FooterText.Text = success; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (!AllowClose) { e.Cancel = true; Hide(); } }

    private sealed class TimelineRow(Segment original, Segment clipped, TimeZoneInfo zone)
    {
        public Segment Original => original;
        public string StartText => TimeZoneInfo.ConvertTime(clipped.Start, zone).ToString("MM-dd HH:mm:ss");
        public string EndText => TimeZoneInfo.ConvertTime(clipped.End, zone).ToString("MM-dd HH:mm:ss");
        public string App => clipped.App;
        public string Duration => DurationText.Format(clipped.Seconds);
        public string Source => clipped.SourceLabel;
    }
}
