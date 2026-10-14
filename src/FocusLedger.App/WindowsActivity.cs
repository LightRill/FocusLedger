using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusLedger.Core;

namespace FocusLedger.App;

internal static class WindowsActivity
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    internal static ActivitySample Capture(Settings settings, bool paused, bool locked)
    {
        var now = DateTimeOffset.UtcNow;
        var monotonic = Environment.TickCount64;
        if (locked) return new(now, monotonic, "", 0, 180, SegmentKind.Locked);
        if (paused) return new(now, monotonic, "", 0, 180, SegmentKind.Paused);
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return new(now, monotonic, "", 0, 180, SegmentKind.Unavailable);
        CloseDesktop(desktop);
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input)) return new(now, monotonic, "", 0, 180, SegmentKind.Unavailable);
        var idle = unchecked((uint)Environment.TickCount64 - input.Tick) / 1000d;
        try
        {
            var window = GetForegroundWindow();
            if (window == 0) return new(now, monotonic, "", idle, 180, SegmentKind.Unavailable);
            var processName = WindowProcessName(window);
            var rule = settings.Rules.FirstOrDefault(r => r.Enabled && r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
            if (rule is null) return new(now, monotonic, "", idle, 180, SegmentKind.Excluded);
            var timeout = rule.IdleMinutes * 60;
            return new(now, monotonic, rule.Name, idle, timeout, idle < timeout ? SegmentKind.Active : SegmentKind.Idle);
        }
        catch { return new(now, monotonic, "", idle, 180, SegmentKind.Unavailable); }
    }

    private static string? OwnerProcessName(nint window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var id);
            if (id == 0) return null;
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal static string? ResolveHostedProcess(string? owner, IEnumerable<string?> children)
    {
        if (!string.Equals(owner, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return owner;
        var candidates = children.Where(p => !string.IsNullOrEmpty(p) && !p.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Do not group unrelated hosted applications under a shared host rule.
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static string? WindowProcessName(nint window)
    {
        var owner = OwnerProcessName(window);
        if (!string.Equals(owner, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return owner;
        var children = new List<string?>();
        EnumChildWindows(window, (child, _) => { children.Add(OwnerProcessName(child)); return true; }, 0);
        return ResolveHostedProcess(owner, children);
    }

    internal static string[] VisibleProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && WindowProcessName(window) is string name &&
                !name.Equals("FocusLedger", StringComparison.OrdinalIgnoreCase)) names.Add(name);
            return true;
        }, 0);
        return names.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
