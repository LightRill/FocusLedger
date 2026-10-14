using FocusLedger.Core;
using FocusLedger.Storage;

namespace FocusLedger.App;

public partial class MainWindow
{
    internal void CheckTimelineFilters(string checkRoot)
    {
        var totalBefore = TotalText.Text;
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Timeline filter: " + message);
        }
        try
        {
            visible = [
                new(1, start, start.AddMilliseconds(59999), "VS Code", SegmentKind.Active),
                new(2, start.AddMinutes(2), start.AddMinutes(3), "VS Code", SegmentKind.Active),
                new(3, start.AddMinutes(4), start.AddMinutes(6), "ChatGPT", SegmentKind.Manual)
            ];
            // More than a page of newer short records must not hide older matches.
            for (int i = 0; i < 1001; i++)
            {
                var next = start.AddHours(1).AddSeconds(i * 10);
                visible.Add(new(10 + i, next, next.AddSeconds(3), "VS Code", SegmentKind.Active));
            }
            timelineOriginals = visible.ToDictionary(s => s.Id);
            UpdateTimelineApps(); ApplyTimelineFilters();
            Require(Timeline.Items.Count == 2, "default >= 60 seconds must filter before the 1000-row limit");
            Require(((TimelineRow)Timeline.Items[1]).Original.Id == 2, "exactly one minute is included");
            TimelineAppFilter.SelectedItem = TimelineAppFilter.Items.Cast<TimelineAppChoice>().Single(c => c.App == "VS Code");
            Require(Timeline.Items.Count == 1, "combined app and duration filter");
            Timeline.SelectedItem = Timeline.Items[0];
            UpdateTimelineApps(); ApplyTimelineFilters();
            Require(Timeline.SelectedItem is TimelineRow row && row.Original.Id == 2, "selection survives refresh");
            TimelineDurationFilter.SelectedIndex = 3;
            Require(Timeline.Items.Count == 0 && EmptyHint.Visibility == Visibility.Visible, "empty filter result");
            TimelineDurationFilter.SelectedIndex = 0;
            Require(Timeline.Items.Count == 1000, "unlimited duration still respects display cap");
            Require(TotalText.Text == totalBefore && visible.Count == 1004, "summary and export source are unchanged");
            var csv = System.IO.Path.Combine(checkRoot, "timeline-filter-export.csv");
            LedgerStore.ExportCsv(csv, visible, zone);
            Require(System.IO.File.ReadAllLines(csv).Length == 1005, "CSV contains every effective record");
            visible = [];
            UpdateTimelineApps(); ApplyTimelineFilters();
            Require((TimelineAppFilter.SelectedItem as TimelineAppChoice)?.App == "VS Code" && Timeline.Items.Count == 0,
                "app selection survives a period with no matching app");
        }
        finally
        {
            TimelineAppFilter.SelectedIndex = 0;
            TimelineDurationFilter.SelectedIndex = 2;
            Refresh();
        }
    }
}
