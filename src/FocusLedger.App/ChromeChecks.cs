using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FocusLedger.App;

// Runs only under --self-check, on the isolated off-screen test window.
internal static class ChromeChecks
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    private static void Flush(Window window) => window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Run(MainWindow window)
    {
        window.ShowOverviewForCheck(); Flush(window);
        var scroll = window.OverviewPage;
        scroll.ScrollToTop(); Flush(window);
        Require(scroll.ScrollableHeight > 0, "Overview must overflow for scrolling check.");
        var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        Require(bar.ActualWidth <= 12, "Themed scrollbar width not applied.");
        ScrollBar.PageDownCommand.Execute(null, bar); Flush(window);
        Require(scroll.VerticalOffset > 0, "Page-down scrollbar command did not scroll.");
        scroll.ScrollToTop(); Flush(window);
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent });
        Flush(window); Require(scroll.VerticalOffset > 0, "Mouse wheel did not scroll.");
        scroll.ScrollToTop(); Flush(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        int Hit(Point point)
        {
            var screen = window.PointToScreen(point);
            var packed = (nint)(((int)screen.X & 0xFFFF) | (((int)screen.Y & 0xFFFF) << 16));
            return (int)SendMessage(hwnd, 0x0084, 0, packed); // WM_NCHITTEST
        }
        Require(Hit(new Point(300, 20)) == 2, "Title bar is not draggable (HTCAPTION).");
        Require(Hit(window.CloseButton.TranslatePoint(new Point(23, 18), window)) == 1, "Caption buttons do not receive clicks (HTCLIENT).");
        Require(Hit(new Point(1, 160)) == 10, "Left edge is not resizable (HTLEFT).");

        // Opacity prevents the isolated window from flashing on the user's monitor.
        window.Opacity = 0;
        try
        {
            window.MinimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window);
            Require(window.WindowState == WindowState.Minimized, "Minimize command failed.");
            SystemCommands.RestoreWindow(window); Flush(window);
            window.MaximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window);
            Require(window.WindowState == WindowState.Maximized, "Maximize command failed.");
            Require((string)window.MaximizeButton.ToolTip == "还原", "Restore button state did not update.");
            var workArea = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var captionTop = window.CloseButton.PointToScreen(new Point(0, 0));
            var captionEnd = window.CloseButton.PointToScreen(new Point(window.CloseButton.ActualWidth, window.CloseButton.ActualHeight));
            Require(captionTop.Y >= workArea.Top && captionEnd.X <= workArea.Right, "Maximized caption is clipped by the resize border.");
            window.MaximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window);
            Require(window.WindowState == WindowState.Normal, "Restore command failed.");
            window.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(window);
            Require(!window.IsVisible, "Close should hide to tray without terminating tracking.");
            window.Show(); Flush(window);
        }
        finally { window.Opacity = 1; }
    }
}
