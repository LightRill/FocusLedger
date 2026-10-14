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
            GetWindowThreadProcessId(window, out var processId);
            using var process = Process.GetProcessById((int)processId);
            var rule = settings.Rules.FirstOrDefault(r => r.Enabled && r.ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (rule is null) return new(now, monotonic, "", idle, 180, SegmentKind.Excluded);
            var timeout = rule.IdleMinutes * 60;
            return new(now, monotonic, rule.Name, idle, timeout, idle < timeout ? SegmentKind.Active : SegmentKind.Idle);
        }
        catch { return new(now, monotonic, "", idle, 180, SegmentKind.Unavailable); }
    }

    internal static string[] VisibleProcesses() => Process.GetProcesses().Select(p =>
    {
        using (p) { try { return p.MainWindowHandle != 0 ? p.ProcessName : null; } catch { return null; } }
    }).Where(p => p is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
}
