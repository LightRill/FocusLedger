using System.Collections.ObjectModel;
using FocusLedger.Core;
using FocusLedger.Storage;
using Microsoft.Win32;

namespace FocusLedger.App;

public partial class SettingsView : UserControl
{
    private readonly ObservableCollection<AppRule> rules;
    private readonly string defaultDataDirectory;
    public Settings? Result { get; private set; }
    public string SelectedDataDirectory { get; private set; } = "";
    public bool? StartupEnabled => StartupCheck.IsEnabled ? StartupCheck.IsChecked == true : null;
    internal SettingsView(Settings settings, string dataDirectory, string defaultDataDirectory)
    {
        this.defaultDataDirectory = defaultDataDirectory;
        InitializeComponent();
        rules = new(SettingsFile.Clone(settings).Rules);
        RulesGrid.ItemsSource = rules;
        var startup = StartupRegistration.IsEnabled();
        StartupCheck.IsChecked = startup == true;
        if (startup is null) { StartupCheck.IsEnabled = false; StartupCheck.Content = "开机自启不可用：当前环境不允许读取启动设置"; }
        DefaultIdleInput.Text = settings.DefaultIdleMinutes.ToString();
        DataDirectoryInput.Text = dataDirectory;
        DefaultPathText.Text = "默认位置：程序 exe 所在目录下的 data 文件夹。\n" + defaultDataDirectory;
        ThemeChoice.SelectedIndex = settings.Theme == "Dark" ? 2 : settings.Theme == "Ocean" ? 1 : 0;
        LoadProcesses();
    }
    private void LoadProcesses() { Processes.ItemsSource = WindowsActivity.VisibleProcesses(); if (Processes.Items.Count > 0) Processes.SelectedIndex = 0; }
    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => LoadProcesses();
    private void AddProcess_Click(object sender, RoutedEventArgs e) { if (Processes.SelectedItem is string process) Add(process); }
    private void AddExecutable_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Filter = "Windows 程序|*.exe", Title = "选择要统计的软件" };
        if (open.ShowDialog(Window.GetWindow(this)) == true) Add(Path.GetFileNameWithoutExtension(open.FileName));
    }
    private void Add(string process)
    {
        if (rules.Any(r => r.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase))) { ErrorText.Text = "该进程已经在列表中。"; return; }
        if (!TryDefaultMinutes(out var minutes)) return;
        var rule = new Settings { DefaultIdleMinutes = minutes }.CreateRule(process);
        rules.Add(rule); RulesGrid.SelectedItem = rule; ErrorText.Text = "";
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (RulesGrid.SelectedItem is AppRule rule) rules.Remove(rule); }
    internal event Action? Saved;
    internal event Action? Cancelled;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    internal void SaveForCheck() => Save_Click(this, new RoutedEventArgs());
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !RulesGrid.CommitEdit(DataGridEditingUnit.Row, true)) throw new ArgumentException("请先修正表格中的无效输入。");
            if (!TryDefaultMinutes(out var minutes)) return;
            var settings = new Settings { DefaultIdleMinutes = minutes, Rules = rules.ToList(), Theme = (string)((ComboBoxItem)ThemeChoice.SelectedItem).Tag };
            settings.Validate();
            SelectedDataDirectory = DataLocation.Normalize(DataDirectoryInput.Text);
            Result = settings;
            Saved?.Invoke();
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private bool TryDefaultMinutes(out int minutes)
    {
        if (!int.TryParse(DefaultIdleInput.Text.Trim(), out minutes) || minutes is < 1 or > 60)
        { ErrorText.Text = "空闲阈值须为 1–60 的整数分钟。"; return false; }
        return true;
    }
    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!TryDefaultMinutes(out var minutes)) return;
        if (!RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !RulesGrid.CommitEdit(DataGridEditingUnit.Row, true))
        { ErrorText.Text = "请先修正表格中的无效输入。"; return; }
        new Settings { Rules = rules.ToList() }.ApplyIdleMinutesToAll(minutes);
        RulesGrid.Items.Refresh(); ErrorText.Text = "已应用到全部软件，点击保存后生效。";
    }
    private void ChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择数据保存目录", InitialDirectory = Directory.Exists(DataDirectoryInput.Text) ? DataDirectoryInput.Text : "" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) DataDirectoryInput.Text = picker.FolderName;
    }
    private void DefaultDirectory_Click(object sender, RoutedEventArgs e) => DataDirectoryInput.Text = defaultDataDirectory;
    internal void ShowThemeTab() => SettingsTabs.SelectedIndex = 2;
    internal void ShowDataTab() => SettingsTabs.SelectedIndex = 1;
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool? IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("FocusLedger") is string;
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    internal static void SetEnabled(bool enabled)
    {
        if (enabled == IsEnabled()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
            if (!Path.GetFileName(exe).Equals("FocusLedger.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请从 FocusLedger.exe 启动后再设置开机自启。");
            key.SetValue("FocusLedger", $"\"{exe}\" --minimized");
        }
        else key.DeleteValue("FocusLedger", false);
    }
}
