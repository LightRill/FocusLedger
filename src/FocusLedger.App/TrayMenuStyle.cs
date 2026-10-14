using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FocusLedger.App;

internal static class TrayMenuStyle
{
    internal static void Apply(Forms.ContextMenuStrip menu)
    {
        var scale = menu.DeviceDpi / 96f;
        int Dip(int value) => (int)Math.Round(value * scale);
        menu.BackColor = ThemeManager.Native("Surface");
        menu.ForeColor = ThemeManager.Native("Ink");
        menu.Padding = new Forms.Padding(Dip(6));
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            item.Margin = Forms.Padding.Empty;
            item.Size = new Drawing.Size(Dip(212), Dip(item is Forms.ToolStripSeparator ? 9 : item is Forms.ToolStripLabel ? 32 : 36));
            item.Padding = item is Forms.ToolStripSeparator ? Forms.Padding.Empty : new Forms.Padding(Dip(12), 0, Dip(12), 0);
            item.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            item.ForeColor = ThemeManager.Native(item is Forms.ToolStripLabel ? "Muted" : "Ink");
        }
    }

    internal sealed class Renderer() : Forms.ToolStripProfessionalRenderer(new ThemeManager.TrayColors())
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            // Use the whole row height instead of the menu's default baseline offset.
            e.TextRectangle = new Drawing.Rectangle(e.Item.Padding.Left, 0, e.Item.Width - e.Item.Padding.Horizontal, e.Item.Height);
            e.TextFormat = Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix;
            e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }
    }
}
