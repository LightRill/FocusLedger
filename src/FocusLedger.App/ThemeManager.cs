using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace FocusLedger.App;

internal static class ThemeManager
{
    internal static event Action? Changed;
    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    internal static Brush Heat(double seconds, bool observed)
    {
        if (!observed) return Brush("Line");
        var weight = seconds <= 0 ? .08 : seconds < 3600 ? .25 : seconds < 10800 ? .45 : seconds < 21600 ? .7 : 1;
        var front = ((SolidColorBrush)Brush("Accent")).Color;
        var back = ((SolidColorBrush)Brush("Surface")).Color;
        byte Mix(byte a, byte b) => (byte)Math.Round(a * weight + b * (1 - weight));
        return new SolidColorBrush(Color.FromRgb(Mix(front.R, back.R), Mix(front.G, back.G), Mix(front.B, back.B)));
    }
    internal static void Apply(string theme)
    {
        string[] keys = ["Page", "Surface", "Ink", "Muted", "Accent", "Soft", "Line", "Sidebar", "SidebarInk", "SidebarMuted", "OnAccent", "Danger", "Selected"];
        string[] colors = theme switch
        {
            "Dark" => ["#141B26", "#1D2735", "#E7EDF5", "#A6B6CB", "#8CBAB0", "#293C46", "#36465B", "#101722", "#F1F5FA", "#A6B6CB", "#112C29", "#FFADA3", "#344E5D"],
            "Ocean" => ["#F2F5FB", "#FFFFFF", "#203653", "#627591", "#3569B1", "#E5EDFA", "#D8E2F0", "#203858", "#F5F8FF", "#B4C7E4", "#FFFFFF", "#B44444", "#D7E6FC"],
            _ => ["#F3F5EF", "#FFFFFF", "#233D35", "#657C70", "#32745E", "#E5EEDF", "#DCE5D8", "#203E34", "#F4FAF5", "#B4CDBF", "#FFFFFF", "#B44444", "#D5E8DA"]
        };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Application.Current.Resources["ScrollGrip"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme switch { "Dark" => "#506278", "Ocean" => "#ADBFD6", _ => "#B1C5B9" }));
        // Native WPF selection/popup surfaces also follow the selected palette.
        Application.Current.Resources[SystemColors.HighlightBrushKey] = Brush("Selected");
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Brush("Ink");
        Application.Current.Resources[SystemColors.WindowBrushKey] = Brush("Surface");
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Brush("Ink");
        Application.Current.Resources[SystemColors.ControlBrushKey] = Brush("Page");
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = Brush("Ink");
        Changed?.Invoke();
    }
    internal static Drawing.Color Native(string key)
    {
        var c = ((SolidColorBrush)Brush(key)).Color;
        return Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }
    internal sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Native("Surface");
        public override Drawing.Color ImageMarginGradientBegin => Native("Surface");
        public override Drawing.Color ImageMarginGradientMiddle => Native("Surface");
        public override Drawing.Color ImageMarginGradientEnd => Native("Surface");
        public override Drawing.Color MenuItemSelected => Native("Soft");
        public override Drawing.Color MenuItemBorder => Native("Line");
        public override Drawing.Color MenuBorder => Native("Line");
        public override Drawing.Color SeparatorDark => Native("Line");
        public override Drawing.Color SeparatorLight => Native("Surface");
    }
}
