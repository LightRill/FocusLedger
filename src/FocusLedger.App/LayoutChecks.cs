using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FocusLedger.App;

internal static class LayoutChecks
{
    internal static void CheckTable(DataGrid grid)
    {
        grid.UpdateLayout();
        int checkedCells = 0;
        foreach (var item in grid.Items)
        {
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) continue;
            foreach (var column in grid.Columns)
            {
                if (column.GetCellContent(row) is not FrameworkElement content || !content.IsVisible) continue;
                var cell = ParentCell(content);
                var center = content.TranslatePoint(new Point(0, content.ActualHeight / 2), cell).Y;
                if (Math.Abs(center - cell.ActualHeight / 2) > 1.1)
                    throw new InvalidOperationException($"{column.Header}: cell content is not vertically centered ({center:0.##}/{cell.ActualHeight:0.##}).");
                checkedCells++;
            }
        }
        if (checkedCells == 0) throw new InvalidOperationException("No realized table cells were checked.");
    }

    internal static void CheckEditor(SettingsView view)
    {
        var grid = view.RulesGrid;
        grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[3]);
        grid.Focus();
        if (!grid.BeginEdit()) throw new InvalidOperationException("Could not start editing the timeout cell.");
        grid.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(grid.UpdateLayout));
        if (grid.Columns[3].GetCellContent(grid.Items[0]) is not TextBox)
            throw new InvalidOperationException("Timeout editor was not created.");
        CheckTable(grid);
        grid.CancelEdit(DataGridEditingUnit.Cell); grid.CancelEdit(DataGridEditingUnit.Row);
    }

    private static DataGridCell ParentCell(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is DataGridCell cell) return cell;
        throw new InvalidOperationException("Cell presenter is detached.");
    }
}
