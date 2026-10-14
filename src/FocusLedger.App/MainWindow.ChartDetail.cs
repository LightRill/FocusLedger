using FocusLedger.Core;

namespace FocusLedger.App;

public partial class MainWindow
{
    private readonly List<(int Index, Rect Bounds)> trendBars = [];
    private void HideChartDetail() => ChartDetail.Visibility = Visibility.Collapsed;

    private void UpdateTrendDetail(Point point)
    {
        if (buckets.Count == 0 || TrendCanvas.ActualWidth <= 0 || point.X < 0 || point.X >= TrendCanvas.ActualWidth ||
            point.Y < 0 || point.Y > TrendCanvas.ActualHeight)
        { HideChartDetail(); return; }
        // Keep a small tolerance around the drawn bar, never the full column.
        var index = Math.Min(buckets.Count - 1, (int)(point.X / TrendCanvas.ActualWidth * buckets.Count));
        var bucket = buckets[index];
        var bounds = trendBars.FirstOrDefault(b => b.Index == index).Bounds;
        bounds.Inflate(3, 4);
        if (bucket.Seconds <= 0 || !bounds.Contains(point)) { HideChartDetail(); return; }
        var date = TimeZoneInfo.ConvertTime(bucket.Start, zone);
        var end = TimeZoneInfo.ConvertTime(index + 1 < buckets.Count ? buckets[index + 1].Start : CurrentRange.End, zone);
        var title = CurrentPeriod switch
        {
            Period.Day => $"{date:MM 月 dd 日 HH:mm}–{end:HH:mm}",
            Period.Year => date.ToString("yyyy 年 M 月"),
            _ => date.ToString("yyyy 年 M 月 d 日")
        };
        ShowChartDetail(title, bucket.Seconds, TrendCanvas.TranslatePoint(point, ChartOverlay));
    }

    private void ShowChartDetail(string title, double seconds, Point pointer)
    {
        ChartDetailTitle.Text = title;
        ChartDetailValue.Text = DurationText.Format(seconds);
        ChartDetail.Visibility = Visibility.Visible;
        ChartDetail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ChartDetail.DesiredSize;
        const double gap = 18, edge = 12;
        var x = pointer.X + gap;
        var y = pointer.Y + gap;
        if (x + size.Width > ChartOverlay.ActualWidth - edge) x = pointer.X - size.Width - gap;
        if (y + size.Height > ChartOverlay.ActualHeight - edge) y = pointer.Y - size.Height - gap;
        Canvas.SetLeft(ChartDetail, Math.Clamp(x, edge, Math.Max(edge, ChartOverlay.ActualWidth - size.Width - edge)));
        Canvas.SetTop(ChartDetail, Math.Clamp(y, edge, Math.Max(edge, ChartOverlay.ActualHeight - size.Height - edge)));
    }
}
