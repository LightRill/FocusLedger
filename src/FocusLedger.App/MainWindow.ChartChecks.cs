using FocusLedger.Core;
using System.Windows.Threading;

namespace FocusLedger.App;

public partial class MainWindow
{
    internal void CheckChartDetails(Action<string> capture)
    {
        void Flush() => Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(UpdateLayout));
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Chart details: " + message);
        }
        try
        {
            OverviewPage.ScrollToTop();
            foreach (var period in new[] { Period.Day, Period.Week, Period.Month, Period.Year })
            {
                SetPeriodForCheck(DateTime.Today.AddDays(-1), period); Flush();
                var index = buckets.FindIndex(b => b.Seconds > 0);
                Require(index >= 0, "demo must have recorded time");
                var point = new Point((index + .5) * TrendCanvas.ActualWidth / buckets.Count, 30);
                Require(VisualTreeHelper.HitTest(TrendCanvas, point) is not null, "column above bar must receive mouse input");
                UpdateTrendDetail(point); Flush();
                Require(ChartDetail.Visibility == Visibility.Visible && ChartDetailValue.Text == DurationText.Format(buckets[index].Seconds), "correct bucket details for " + period);
                Require(period != Period.Day || ChartDetailTitle.Text.Contains(":00–"), "day tooltip shows full hourly range");
                var left = Canvas.GetLeft(ChartDetail);
                UpdateTrendDetail(new Point(point.X + 3, point.Y + 4)); Flush();
                Require(Math.Abs(Canvas.GetLeft(ChartDetail) - left - 3) < .1, "tooltip follows pointer within same column");
                var pointer = TrendCanvas.TranslatePoint(new Point(point.X + 3, point.Y + 4), ChartOverlay);
                Require(!new Rect(Canvas.GetLeft(ChartDetail), Canvas.GetTop(ChartDetail), ChartDetail.ActualWidth, ChartDetail.ActualHeight).Contains(pointer), "tooltip avoids pointer");
                Require(!ChartOverlay.IsHitTestVisible, "overlay must not interrupt hover");
                capture("chart-detail-" + period + ".png");
            }
            SetPeriodForCheck(DateTime.Today.AddDays(-1), Period.Day); Flush();
            var empty = buckets.FindIndex(b => b.ObservedSeconds == 0);
            UpdateTrendDetail(new Point((empty + .5) * TrendCanvas.ActualWidth / buckets.Count, 50));
            Require(ChartDetailValue.Text == "无记录", "missing data is not shown as zero work");
            ShowChartDetail("边界检查", 60, 60, new Point(ChartOverlay.ActualWidth - 2, ChartOverlay.ActualHeight - 2)); Flush();
            Require(Canvas.GetLeft(ChartDetail) + ChartDetail.ActualWidth <= ChartOverlay.ActualWidth - 11 &&
                Canvas.GetTop(ChartDetail) + ChartDetail.ActualHeight <= ChartOverlay.ActualHeight - 11, "tooltip fits window edges");
            foreach (var theme in new[] { "Forest", "Ocean", "Dark" })
            {
                ThemeManager.Apply(theme); Flush();
                UpdateTrendDetail(new Point(9.5 * TrendCanvas.ActualWidth / buckets.Count, 60)); Flush();
                Require(((SolidColorBrush)ChartDetail.Background).Color == ((SolidColorBrush)ThemeManager.Brush("Surface")).Color, "theme background for " + theme);
                capture("chart-detail-" + theme + ".png");
            }
            TrendCanvas.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = MouseLeaveEvent });
            Require(ChartDetail.Visibility == Visibility.Collapsed, "mouse leave closes tooltip");
        }
        finally { ThemeManager.Apply("Dark"); HideChartDetail(); }
    }
}
