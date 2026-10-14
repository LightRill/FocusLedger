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
            Require(Processes.SelectedIndex == -1, "no default process selection");
            AddProcess_Click(this, new RoutedEventArgs());
            Require(rules.Count == before.Length, "clicking without selection must not add the first process");
            Require(Processes.Items.Count == 0 || Processes.IsDropDownOpen, "click without selection opens the picker");
            Processes.IsDropDownOpen = false;
            SetProcessChoices(["TestAppA", "TestAppB"]);
            Processes.SelectedItem = "TestAppB";
            AddProcess_Click(this, new RoutedEventArgs());
            Require(rules.Count == before.Length + 1 && rules.Last().ProcessName == "TestAppB", "adds the explicitly selected app");
            AddProcess_Click(this, new RoutedEventArgs());
            Require(rules.Count == before.Length + 1, "duplicate selection is not added twice");
            SetProcessChoices(["TestAppA", "TestAppB", "TestAppC"]);
            Require((string?)Processes.SelectedItem == "TestAppB", "refresh preserves selection");
            SetProcessChoices(["TestAppA"]);
            Require(Processes.SelectedIndex == -1, "closed app does not silently select another app");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["ApplicationFrameHost", "CalculatorApp", "calculatorapp"]) == "CalculatorApp", "host resolves to real app");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["ApplicationFrameHost", null]) is null, "unresolved host is omitted");
            Require(WindowsActivity.ResolveHostedProcess("ApplicationFrameHost", ["AppA", "AppB"]) is null, "ambiguous host is omitted");
            Require(WindowsActivity.ResolveHostedProcess("Code", []) == "Code", "ordinary desktop process is preserved");
            Require(!WindowsActivity.VisibleProcesses().Any(p => p.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)), "native list excludes shared host");
        }
        finally
        {
            Processes.IsDropDownOpen = false;
            rules.Clear(); foreach (var rule in before) rules.Add(rule);
            Processes.SelectedIndex = -1; LoadProcesses();
            ErrorText.Text = "";
        }
    }
}
