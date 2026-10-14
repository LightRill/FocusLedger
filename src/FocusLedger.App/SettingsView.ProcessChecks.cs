namespace FocusLedger.App;

public partial class SettingsView
{
    internal void CheckProcessPicker()
    {
        var before = rules.ToArray();
        void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Process picker: " + message);
        }
        try
        {
            AddProcess_Click(this, new RoutedEventArgs());
            Require(rules.Count == before.Length && ProcessMenu.IsOpen, "button opens menu without adding a default process");
            SetProcessChoices(["TestAppA", "TestAppB"]);
            ((MenuItem)ProcessMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(rules.Count == before.Length + 1 && rules.Last().ProcessName == "TestAppB", "adds the explicitly selected app");
            Require(ErrorText.Text == "", "successful addition produces no footer hint");
            SetProcessChoices(["TestAppA", "TestAppB", "TestAppC"]);
            Require(!((MenuItem)ProcessMenu.Items[1]).IsEnabled, "already added process is disabled in menu");
            Add("TestAppB");
            Require(rules.Count == before.Length + 1, "duplicate addition is prevented");
            SetProcessChoices([]);
            Require(ProcessMenu.Items.Count == 1 && !((MenuItem)ProcessMenu.Items[0]).IsEnabled, "empty list is explained inside menu");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["ApplicationFrameHost", "CalculatorApp", "calculatorapp"]) == "CalculatorApp", "host resolves to real app");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["ApplicationFrameHost", null]) is null, "unresolved host is omitted");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["AppA", "AppB"]) is null, "ambiguous host is omitted");
            Require(WindowsActivity.ResolveHostedProcess("Code", []) == "Code", "ordinary desktop process is preserved");
            Require(!WindowsActivity.VisibleProcesses().Any(p => p.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)), "native list excludes shared host");
        }
        finally
        {
            ProcessMenu.IsOpen = false;
            rules.Clear(); foreach (var rule in before) rules.Add(rule);
            LoadProcesses();
            ErrorText.Text = "";
        }
    }
}
