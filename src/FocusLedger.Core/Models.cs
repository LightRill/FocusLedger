namespace FocusLedger.Core;

public enum SegmentKind { Active, Manual, Idle, Excluded, Paused, Locked, Unavailable }

public sealed record Segment(long Id, DateTimeOffset Start, DateTimeOffset End,
    string App, SegmentKind Kind, string Note = "")
{
    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
    public bool IsEffective => Kind is SegmentKind.Active or SegmentKind.Manual;
    public string SourceLabel => Kind switch
    {
        SegmentKind.Active => "自动记录", SegmentKind.Manual => "人工修正",
        SegmentKind.Idle => "超过空闲阈值", SegmentKind.Excluded => "非统计软件",
        SegmentKind.Paused => "手动暂停", SegmentKind.Locked => "锁屏 / 断开会话",
        _ => "采集不可用"
    };
}

public sealed class AppRule
{
    public string Name { get; set; } = "新软件";
    public string ProcessName { get; set; } = "";
    public int IdleMinutes { get; set; } = 3;
    public bool Enabled { get; set; } = true;
}

public sealed class Settings
{
    public string Theme { get; set; } = "Forest";
    public int DefaultIdleMinutes { get; set; } = 3;
    public List<AppRule> Rules { get; set; } =
    [
        new() { Name = "VS Code", ProcessName = "Code" },
        new() { Name = "VS Code Insiders", ProcessName = "Code - Insiders" },
        new() { Name = "Codex", ProcessName = "Codex" },
        new() { Name = "ChatGPT", ProcessName = "ChatGPT" },
        new() { Name = "Zotero", ProcessName = "zotero", Enabled = false }
    ];

    public static string NormalizeProcess(string value) =>
        value.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value.Trim()[..^4] : value.Trim();

    public void Validate()
    {
        if (Theme is not ("Forest" or "Ocean" or "Dark")) throw new ArgumentException("请选择有效的颜色主题。");
        if (DefaultIdleMinutes is < 1 or > 60) throw new ArgumentException("默认空闲阈值须为 1–60 分钟。");
        if (Rules.Count == 0) throw new ArgumentException("至少保留一条软件规则，可以将其关闭。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            rule.Name = rule.Name.Trim();
            rule.ProcessName = NormalizeProcess(rule.ProcessName);
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.ProcessName))
                throw new ArgumentException("软件名称和进程名不能为空。");
            if (rule.ProcessName.IndexOfAny(['/', '\\', '*', '?', ':']) >= 0)
                throw new ArgumentException("请填进程名，如 Code；不要填路径或通配符。");
            if (rule.IdleMinutes is < 1 or > 60) throw new ArgumentException("空闲阈值须为 1–60 分钟。");
            if (!seen.Add(rule.ProcessName)) throw new ArgumentException($"进程名重复：{rule.ProcessName}");
        }
    }

    public AppRule CreateRule(string process) => new() { Name = process, ProcessName = process, IdleMinutes = DefaultIdleMinutes };

    public void ApplyIdleMinutesToAll(int minutes)
    {
        if (minutes is < 1 or > 60) throw new ArgumentException("空闲阈值须为 1–60 分钟。");
        DefaultIdleMinutes = minutes;
        foreach (var rule in Rules) rule.IdleMinutes = minutes;
    }
}

public sealed record ActivitySample(DateTimeOffset Utc, long MonotonicMs, string App,
    double IdleSeconds, int TimeoutSeconds, SegmentKind Kind);

public static class DurationText
{
    public static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes:00} 分" : $"{(int)span.TotalMinutes} 分 {span.Seconds:00} 秒";
    }
    public static string Short(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.#}h" : $"{seconds / 60:0.#}m";
}
