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
                var bar = trendBars.Single(b => b.Index == index).Bounds;
                var point = new Point(bar.X + bar.Width / 2, bar.Y + bar.Height / 2);
                UpdateTrendDetail(new Point(point.X, bar.Top - 6));
                Require(ChartDetail.Visibility == Visibility.Collapsed, "empty area above active bar does not trigger");
                UpdateTrendDetail(new Point(point.X, bar.Top - 2));
                Require(ChartDetail.Visibility == Visibility.Visible, "small tolerance around active bar");
                UpdateTrendDetail(new Point(bar.Right + 5, point.Y));
                Require(ChartDetail.Visibility == Visibility.Collapsed, "horizontal gap does not trigger");
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
            var empty = buckets.FindIndex(b => b.Seconds == 0);
            UpdateTrendDetail(new Point((empty + .5) * TrendCanvas.ActualWidth / buckets.Count, 117));
            Require(ChartDetail.Visibility == Visibility.Collapsed, "empty bar never triggers");
            ShowChartDetail("边界检查", 60, new Point(ChartOverlay.ActualWidth - 2, ChartOverlay.ActualHeight - 2)); Flush();
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
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
            foreach (var year in new[] { 2024, 2026 })
            for (var month = 1; month <= 12; month++)
            {
                var grid = CreateHeatmapDays(new DateTime(year, month, 1), [], scale, scale);
                var cells = grid.Children.Cast<Border>().ToArray();
                Require(cells.Length == DateTime.DaysInMonth(year, month), "all days including leap day");
                for (var i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i];
                    var x = Canvas.GetLeft(cell); var y = Canvas.GetTop(cell);
                    Require(Math.Abs(x * scale - Math.Round(x * scale)) < .001 && Math.Abs(y * scale - Math.Round(y * scale)) < .001,
                        "heatmap origins align to physical pixels");
                    Require(x + cell.Width <= grid.Width + .001 && y + cell.Height <= grid.Height + .001, "heatmap cell fits month");
                    if (i > 0 && Math.Abs(y - Canvas.GetTop(cells[i - 1])) < .001)
                    {
                        var gap = x - Canvas.GetLeft(cells[i - 1]) - cells[i - 1].Width;
                        Require(Math.Abs(gap * scale - Math.Round(3 * scale)) < .001, "equal horizontal gaps at " + scale);
                    }
                }
            }
            SetPeriodForCheck(DateTime.Today, Period.Year); Flush();
            YearHeatmapCard.BringIntoView(); Flush();
            capture("year-grid-dark.png");
        }
        finally { SetPeriodForCheck(DateTime.Today.AddDays(-1), Period.Day); ThemeManager.Apply("Dark"); HideChartDetail(); }
    }
}
