using System.Globalization;
using FocusLedger.Core;

namespace FocusLedger.App;

public partial class ManualView : UserControl
{
    private readonly TimeZoneInfo zone;
    private readonly Segment? original;
    public Segment? Result { get; private set; }
    internal ManualView(Settings settings, Segment? original, DateTime selected, TimeZoneInfo zone)
    {
        this.zone = zone;
        this.original = original;
        InitializeComponent();
        AppName.ItemsSource = settings.Rules.Select(r => r.Name).Distinct().ToList();
        AppName.Text = original?.App ?? settings.Rules.FirstOrDefault(r => r.Enabled)?.Name ?? "工作学习";
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;
        var end = selected.Date >= localNow.Date ? localNow : selected.Date.AddHours(10);
        StartTime.Text = (original is null ? end.AddMinutes(-15) : TimeZoneInfo.ConvertTime(original.Start, zone).DateTime).ToString("yyyy-MM-dd HH:mm:ss");
        EndTime.Text = (original is null ? end : TimeZoneInfo.ConvertTime(original.End, zone).DateTime).ToString("yyyy-MM-dd HH:mm:ss");
        NoteText.Text = original?.Note ?? "";
        if (original is not null) Heading.Text = "修正这段时间";
        TimeZoneLabel.Text = "开始时间 · " + zone.Id;
    }
    internal event Action? Saved;
    internal event Action? Cancelled;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    internal void SaveForCheck() => Save_Click(this, new RoutedEventArgs());
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!DateTime.TryParseExact(StartTime.Text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateTime.TryParseExact(EndTime.Text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new ArgumentException("请输入完整时间，格式：yyyy-MM-dd HH:mm:ss。");
            var startUtc = Reports.EditedTimeUtc(start, zone, original?.Start);
            var endUtc = Reports.EditedTimeUtc(end, zone, original?.End);
            if (endUtc <= startUtc) throw new ArgumentException("结束时间必须晚于开始时间。");
            if (endUtc > DateTimeOffset.UtcNow) throw new ArgumentException("结束时间不能在未来。");
            if (string.IsNullOrWhiteSpace(AppName.Text)) throw new ArgumentException("请填写软件名称。");
            Result = new(0, startUtc, endUtc, AppName.Text.Trim(), SegmentKind.Manual, NoteText.Text.Trim());
            Saved?.Invoke();
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
}
