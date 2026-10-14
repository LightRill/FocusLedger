using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FocusLedger.App;

internal static class DateChecks
{
    internal static void Run(DatePicker picker, string folder)
    {
        var original = picker.SelectedDate;
        void Flush() => picker.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(picker.UpdateLayout));
        void Require(bool value, string note) { if (!value) throw new InvalidOperationException("Date picker: " + note); }
        void Save(FrameworkElement element, string name)
        {
            void Invalidate(DependencyObject node)
            {
                if (node is UIElement visual) { visual.InvalidateMeasure(); visual.InvalidateVisual(); }
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Invalidate(VisualTreeHelper.GetChild(node, i));
            }
            Invalidate(element);
            element.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(element.UpdateLayout));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name)); encoder.Save(file);
        }
        try
        {
            picker.ApplyTemplate(); Flush();
            var text = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
            text.Text = "2024/2/29";
            text.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); Flush();
            Require(picker.SelectedDate == new DateTime(2024, 2, 29), "typed leap date commits");
            var open = (Button)picker.Template.FindName("PART_Button", picker);
            foreach (var theme in new[] { "Dark", "Ocean", "Forest" })
            {
                ThemeManager.Apply(theme);
                picker.SelectedDate = DateTime.Today; picker.DisplayDate = DateTime.Today;
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                Require(picker.IsDropDownOpen && popup.IsOpen, "calendar button opens dropdown");
                var calendar = (Calendar)popup.Child;
                calendar.ApplyTemplate(); calendar.UpdateLayout();
                var item = (CalendarItem)calendar.Template.FindName("PART_CalendarItem", calendar);
                item.ApplyTemplate(); item.UpdateLayout();
                Require(((SolidColorBrush)calendar.Background).Color == ((SolidColorBrush)ThemeManager.Brush("Surface")).Color, "calendar theme");
                Save(calendar, "calendar-" + theme + ".png");
                Save(picker, "date-input-" + theme + ".png");
                var next = (Button)item.Template.FindName("PART_NextButton", item);
                var previous = (Button)item.Template.FindName("PART_PreviousButton", item);
                var before = calendar.DisplayDate;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayDate.Year == before.AddMonths(1).Year && calendar.DisplayDate.Month == before.AddMonths(1).Month, "next month");
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayDate.Month == before.Month, "previous month");
                var header = (Button)item.Template.FindName("PART_HeaderButton", item);
                header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayMode == CalendarMode.Year, "header opens month chooser");
                var yearGrid = (Grid)item.Template.FindName("PART_YearView", item);
                var monthGrid = (Grid)item.Template.FindName("PART_MonthView", item);
                Require(yearGrid.IsVisible && !monthGrid.IsVisible, "month chooser is actually visible");
                Save(calendar, "calendar-months-" + theme + ".png");
                header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayMode == CalendarMode.Decade, "header opens year chooser");
                Require(yearGrid.IsVisible && !monthGrid.IsVisible, "year chooser is actually visible");
                Save(calendar, "calendar-years-" + theme + ".png");
                ((CalendarButton)yearGrid.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayMode == CalendarMode.Year, "select year opens month chooser");
                ((CalendarButton)yearGrid.Children[3]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
                Require(calendar.DisplayMode == CalendarMode.Month && monthGrid.IsVisible && !yearGrid.IsVisible, "select month opens days");
                calendar.DisplayMode = CalendarMode.Month; Flush();
                var date = DateTime.Today.AddDays(-1);
                calendar.SelectedDate = date; Flush();
                Require(picker.SelectedDate == date, "calendar selection updates report date");
                picker.IsDropDownOpen = false; Flush();
                Require(!popup.IsOpen, "dropdown closes");
            }
        }
        finally { picker.IsDropDownOpen = false; picker.SelectedDate = original; ThemeManager.Apply("Dark"); }
    }
}
