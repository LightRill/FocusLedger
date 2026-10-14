using System.Windows.Threading;
using FocusLedger.Core;
using FocusLedger.Storage;
using Microsoft.Win32;

namespace FocusLedger.App;

internal sealed class Tracker : IDisposable
{
    private LedgerStore store;
    private readonly TrackingEngine engine = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dispatcher dispatcher;
    private bool locked, suspended;
    public Settings Settings { get; set; }
    public bool Paused { get; private set; }
    public string Status { get; private set; } = "准备记录";
    public string LastApp { get; private set; } = "尚无记录";
    public event Action? Updated;

    public Tracker(LedgerStore store, Settings settings)
    {
        this.store = store;
        Settings = settings;
        dispatcher = Dispatcher.CurrentDispatcher;
        timer.Tick += (_, _) => Poll();
    }
    public void Start()
    {
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        Poll();
        timer.Start();
    }
    public void TogglePause()
    {
        Poll();
        Paused = !Paused;
        engine.Reset();
        Poll();
    }
    public void Reset() => engine.Reset();
    public void SwitchStore(LedgerStore target, Settings settings)
    {
        store = target;
        Settings = settings;
        engine.Reset();
    }
    public void Poll()
    {
        try
        {
            var sample = WindowsActivity.Capture(Settings, Paused, locked || suspended);
            foreach (var segment in engine.Push(sample)) store.Append(segment);
            if (sample.Kind == SegmentKind.Active) LastApp = sample.App;
            Status = sample.Kind switch
            {
                SegmentKind.Active => $"正在记录 · {sample.App} · 空闲 {Math.Floor(sample.IdleSeconds)} 秒",
                SegmentKind.Idle => $"已暂停 · {sample.App} 无操作超过 {sample.TimeoutSeconds / 60} 分钟",
                SegmentKind.Paused => "已手动暂停记录",
                SegmentKind.Locked => "已暂停 · 锁屏或会话断开",
                SegmentKind.Unavailable => "采集暂不可用 · 当前区间不计时",
                _ => "等待目标软件处于前台"
            };
        }
        catch (Exception ex)
        {
            engine.Reset();
            Status = "保存失败 · " + ex.Message;
        }
        Updated?.Invoke();
    }
    private void OnSession(object sender, SessionSwitchEventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
        { locked = true; Poll(); }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
        { locked = false; engine.Reset(); Poll(); }
    });
    private void OnPower(object sender, PowerModeChangedEventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) { suspended = true; Poll(); engine.Reset(); }
        else if (e.Mode == PowerModes.Resume) { suspended = false; engine.Reset(); Poll(); }
    });
    public void Dispose()
    {
        timer.Stop();
        SystemEvents.SessionSwitch -= OnSession;
        SystemEvents.PowerModeChanged -= OnPower;
    }
}
