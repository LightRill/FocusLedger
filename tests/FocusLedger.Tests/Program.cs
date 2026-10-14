using FocusLedger.Core;
using FocusLedger.Storage;

var tests = new List<(string Name, Action Test)>();
var origin = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
ActivitySample Sample(double second, string app = "Code", double idle = 0, SegmentKind kind = SegmentKind.Active) =>
    new(origin.AddSeconds(second), (long)(second * 1000), app, idle, 180, kind);
static void Equal(double expected, double actual, string note = "")
{ if (Math.Abs(expected - actual) > .002) throw new Exception($"{note}: expected {expected}, actual {actual}"); }
static void True(bool condition, string note = "Assertion failed") { if (!condition) throw new Exception(note); }
static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
static double Effective(IEnumerable<Segment> items) => items.Where(s => s.IsEffective).Sum(s => s.Seconds);
var temp = Path.Combine(Path.GetTempPath(), "FocusLedger-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
LedgerStore Db(string name) => new(Path.Combine(temp, name + ".db"));

tests.Add(("All default rules use 180 seconds", () => { var settings = new Settings(); True(settings.Rules.All(r => r.IdleMinutes == 3)); settings.Validate(); }));
tests.Add(("Ten minutes idle retains exactly first three minutes", () =>
{
    var engine = new TrackingEngine(); List<Segment> all = [];
    for (int i = 0; i <= 600; i++) all.AddRange(engine.Push(Sample(i, idle: i, kind: i < 180 ? SegmentKind.Active : SegmentKind.Idle)));
    Equal(180, Effective(all)); Equal(420, all.Where(s => s.Kind == SegmentKind.Idle).Sum(s => s.Seconds));
}));
tests.Add(("Reading pauses under threshold are retained", () =>
{
    var engine = new TrackingEngine(); List<Segment> all = [];
    for (int i = 0; i <= 360; i++) all.AddRange(engine.Push(Sample(i, idle: i % 120)));
    Equal(360, Effective(all));
}));
tests.Add(("Threshold within sample splits precisely", () =>
{
    var engine = new TrackingEngine(); engine.Push(Sample(0, idle: 179.6)); var result = engine.Push(Sample(1, idle: 180.6, kind: SegmentKind.Idle));
    Equal(.4, Effective(result)); Equal(.6, result.Single(s => s.Kind == SegmentKind.Idle).Seconds);
}));
tests.Add(("Switching apps cannot double count", () =>
{
    var engine = new TrackingEngine(); List<Segment> all = [];
    for (int i = 0; i <= 60; i++) all.AddRange(engine.Push(Sample(i, i < 30 ? "Code" : "Codex")));
    Equal(60, Effective(all)); Equal(30, all.Where(s => s.App == "Code").Sum(s => s.Seconds));
}));
tests.Add(("Excluded and locked states stop counting", () =>
{
    var engine = new TrackingEngine(); List<Segment> all = [];
    for (int i = 0; i <= 60; i++) all.AddRange(engine.Push(Sample(i, kind: i < 20 ? SegmentKind.Active : i < 40 ? SegmentKind.Locked : SegmentKind.Excluded)));
    Equal(20, Effective(all));
}));
tests.Add(("Sleep and collector stalls are not filled", () =>
{
    var engine = new TrackingEngine(); engine.Push(Sample(0)); Equal(0, Effective(engine.Push(Sample(600))));
    Equal(1, Effective(engine.Push(Sample(601)))); engine.Reset(); Equal(0, Effective(engine.Push(Sample(900))));
}));
tests.Add(("Clock jumps do not generate time", () =>
{
    var engine = new TrackingEngine(); engine.Push(Sample(0)); Equal(0, Effective(engine.Push(Sample(1) with { Utc = origin.AddHours(1) })));
    engine.Reset(); engine.Push(Sample(1)); Equal(0, Effective(engine.Push(Sample(2) with { Utc = origin })));
}));
tests.Add(("Resuming from idle never fills the preceding idle second", () =>
{
    var engine = new TrackingEngine(); engine.Push(Sample(0, idle: 200, kind: SegmentKind.Idle));
    Equal(0, Effective(engine.Push(Sample(1)))); Equal(1, Effective(engine.Push(Sample(2))));
}));
tests.Add(("Continuous samples merge in SQLite", () =>
{
    using var db = Db("merge");
    for (int i = 0; i < 100; i++) db.Append(new(0, origin.AddSeconds(i), origin.AddSeconds(i + 1), "Code", SegmentKind.Active));
    Equal(1, db.ReadAll().Count); Equal(100, Effective(db.ReadAll())); True(db.IntegrityCheck() == "ok");
}));
tests.Add(("Manual overlap splits old records and preserves total", () =>
{
    using var db = Db("manual"); db.Append(new(0, origin, origin.AddHours(1), "Code", SegmentKind.Active));
    db.SaveManual(new(0, origin.AddMinutes(20), origin.AddMinutes(40), "Zotero", SegmentKind.Manual));
    var all = db.ReadAll(); Equal(3, all.Count); Equal(3600, Effective(all)); Equal(1200, all.Single(s => s.Kind == SegmentKind.Manual).Seconds);
    for (int i = 1; i < all.Count; i++) True(all[i].Start >= all[i - 1].End);
}));
tests.Add(("Editing can shrink and relabel an entire record", () =>
{
    using var db = Db("edit"); db.Append(new(0, origin, origin.AddHours(1), "Code", SegmentKind.Active));
    var id = db.ReadAll()[0].Id;
    db.SaveManual(new(0, origin.AddMinutes(10), origin.AddMinutes(50), "Codex", SegmentKind.Manual), id);
    Equal(2400, Effective(db.ReadAll())); True(db.ReadAll()[0].Kind == SegmentKind.Manual);
    db.Delete(db.ReadAll()[0].Id); Equal(0, db.ReadAll().Count);
}));
tests.Add(("Invalid manual changes leave database unchanged", () =>
{
    using var db = Db("invalid"); db.Append(new(0, origin, origin.AddHours(1), "Code", SegmentKind.Active));
    Throws(() => db.SaveManual(new(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "Code", SegmentKind.Manual)));
    Throws(() => db.SaveManual(new(0, origin, origin.AddMinutes(5), "Code", SegmentKind.Manual), 999));
    Equal(3600, Effective(db.ReadAll()));
}));
tests.Add(("Out of order automatic samples cannot overlap existing time", () =>
{
    using var db = Db("rollback"); db.Append(new(0, origin, origin.AddMinutes(10), "Code", SegmentKind.Active));
    db.Append(new(0, origin.AddMinutes(5), origin.AddMinutes(11), "Codex", SegmentKind.Active));
    Equal(660, Effective(db.ReadAll()));
}));
tests.Add(("Cross-midnight reporting divides at local midnight", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var start = Reports.ToUtc(new DateTime(2026, 10, 10, 23, 30, 0), zone);
    var s = new Segment(1, start, start.AddHours(1), "Code", SegmentKind.Active);
    var first = Reports.Range(new(2026, 10, 10), Period.Day, zone); var second = Reports.Range(new(2026, 10, 11), Period.Day, zone);
    Equal(1800, Effective(Reports.Clip([s], first.Start, first.End))); Equal(1800, Effective(Reports.Clip([s], second.Start, second.End)));
}));
tests.Add(("Weeks start Monday and leap years have twelve month buckets", () =>
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var week = Reports.Range(new(2026, 10, 14), Period.Week, zone);
    True(TimeZoneInfo.ConvertTime(week.Start, zone).DayOfWeek == DayOfWeek.Monday); Equal(7, (week.End - week.Start).TotalDays);
    var year = Reports.Range(new(2024, 2, 29), Period.Year, zone); Equal(366, (year.End - year.Start).TotalDays); Equal(12, Reports.Buckets([], year, Period.Year, zone).Count);
}));
tests.Add(("Online backup opens and preserves data", () =>
{
    using var db = Db("backup-source"); db.Append(new(0, origin, origin.AddSeconds(60), "Code", SegmentKind.Active));
    var path = Path.Combine(temp, "backup.db"); db.Backup(path);
    using var backup = new LedgerStore(path); Equal(60, Effective(backup.ReadAll())); True(backup.IntegrityCheck() == "ok");
    Throws(() => db.Backup(db.DatabasePath));
}));
tests.Add(("CSV preserves Chinese, quotes, and blocks formula cells", () =>
{
    var path = Path.Combine(temp, "export.csv"); LedgerStore.ExportCsv(path, [new(1, origin, origin.AddMinutes(1), "=恶意公式", SegmentKind.Manual, "引号\"与,逗号")], TimeZoneInfo.Utc);
    var text = File.ReadAllText(path); True(text.Contains("'=恶意公式")); True(text.Contains("引号\"\"与,逗号")); True(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
}));
tests.Add(("Settings normalize exe names and reject duplicates", () =>
{
    var s = new Settings { Rules = [new() { ProcessName = "Code.exe" }] }; s.Validate(); True(s.Rules[0].ProcessName == "Code");
    s.Rules.Add(new() { ProcessName = "code" }); Throws(s.Validate);
}));
tests.Add(("Settings are atomically saved with a backup", () =>
{
    var path = Path.Combine(temp, "settings.json"); var s = new Settings(); SettingsFile.Save(path, s);
    s.Rules[0].IdleMinutes = 4; SettingsFile.Save(path, s);
    Equal(4, SettingsFile.Load(path).Rules[0].IdleMinutes); Equal(3, SettingsFile.Load(path + ".bak").Rules[0].IdleMinutes);
    File.WriteAllText(path, "broken"); Throws(() => SettingsFile.Load(path)); True(File.ReadAllText(path) == "broken");
}));

tests.Add(("Custom default idle threshold persists and new apps inherit it", () =>
{
    var settings = new Settings { DefaultIdleMinutes = 7 };
    var rule = settings.CreateRule("new-app"); Equal(7, rule.IdleMinutes);
    settings.Rules.Add(rule); var path = Path.Combine(temp, "custom-idle.json"); SettingsFile.Save(path, settings);
    var loaded = SettingsFile.Load(path); Equal(7, loaded.DefaultIdleMinutes); Equal(7, loaded.Rules.Last().IdleMinutes);
    Equal(3, loaded.Rules[0].IdleMinutes);
    loaded.ApplyIdleMinutesToAll(5); True(loaded.Rules.All(r => r.IdleMinutes == 5)); Equal(5, loaded.DefaultIdleMinutes);
    Throws(() => loaded.ApplyIdleMinutesToAll(0)); Throws(() => loaded.ApplyIdleMinutesToAll(61));
}));
tests.Add(("Five minute custom timeout counts exactly five minutes", () =>
{
    var engine = new TrackingEngine(); List<Segment> all = [];
    for (int i = 0; i <= 600; i++) all.AddRange(engine.Push(Sample(i, idle: i, kind: i < 300 ? SegmentKind.Active : SegmentKind.Idle) with { TimeoutSeconds = 300 }));
    Equal(300, Effective(all));
    Throws(() => new Settings { DefaultIdleMinutes = 0 }.Validate());
    Throws(() => new Settings { DefaultIdleMinutes = 61 }.Validate());
}));
tests.Add(("Old settings retain three minute default and per-app values", () =>
{
    var path = Path.Combine(temp, "legacy-settings.json");
    File.WriteAllText(path, "{\"Rules\":[{\"Name\":\"Code\",\"ProcessName\":\"Code\",\"IdleMinutes\":9,\"Enabled\":true}]}");
    var settings = SettingsFile.Load(path); Equal(3, settings.DefaultIdleMinutes); Equal(9, settings.Rules[0].IdleMinutes);
}));
tests.Add(("Data location defaults strictly beneath executable directory", () =>
{
    var exeDir = Path.Combine(temp, "portable", "publish");
    var location = new DataLocation(exeDir, Path.Combine(temp, "profiles"));
    True(location.Load() == Path.Combine(exeDir, "data")); True(!location.IsConfigured);
}));
tests.Add(("Custom data path survives restart and default remains portable", () =>
{
    var exeDir = Path.Combine(temp, "portable-config"); var profiles = Path.Combine(temp, "profiles");
    var location = new DataLocation(exeDir, profiles); var custom = Path.Combine(temp, "我的记录");
    location.Save(custom); True(new DataLocation(exeDir, profiles).Load() == custom);
    location.Save(location.DefaultDirectory);
    var movedExe = Path.Combine(temp, "moved-program"); Directory.CreateDirectory(movedExe);
    File.Copy(location.PortableConfigPath, Path.Combine(movedExe, "focusledger.location.json"));
    True(new DataLocation(movedExe, profiles).Load() == Path.Combine(movedExe, "data"));
}));
tests.Add(("User override remains authoritative when portable config is stale", () =>
{
    var location = new DataLocation(Path.Combine(temp, "protected-program"), Path.Combine(temp, "profile-override"));
    location.Save(location.DefaultDirectory); Directory.CreateDirectory(Path.GetDirectoryName(location.UserConfigPath)!);
    File.WriteAllText(location.UserConfigPath, System.Text.Json.JsonSerializer.Serialize(new { DataDirectory = Path.Combine(temp, "custom-before") }));
    var target = Path.Combine(temp, "custom-after"); location.Save(target); True(location.Load() == target);
}));
tests.Add(("Migration includes live WAL records and preserves source", () =>
{
    using var source = Db("migrate-source");
    source.Append(new(0, origin, origin.AddHours(1), "Code", SegmentKind.Active));
    source.SaveManual(new(0, origin.AddMinutes(20), origin.AddMinutes(30), "阅读", SegmentKind.Manual));
    var target = Path.Combine(temp, "迁移目标"); var settings = new Settings(); settings.ApplyIdleMinutesToAll(8);
    DataDirectoryMigration.CopyToEmptyDirectory(source.DatabasePath, target, settings);
    using var migrated = new LedgerStore(Path.Combine(target, "focusledger.db"));
    Equal(3600, Effective(migrated.ReadAll())); Equal(3600, Effective(source.ReadAll()));
    Equal(3, migrated.ReadAll().Count); Equal(8, SettingsFile.Load(Path.Combine(target, "settings.json")).DefaultIdleMinutes);
    True(migrated.IntegrityCheck() == "ok");
    source.Append(new(0, origin.AddHours(1), origin.AddHours(2), "Code", SegmentKind.Active));
    Equal(3600, Effective(migrated.ReadAll())); Equal(7200, Effective(source.ReadAll()));
}));
tests.Add(("Migration refuses existing data without overwriting", () =>
{
    using var source = Db("conflict-source"); source.Append(new(0, origin, origin.AddMinutes(1), "Code", SegmentKind.Active));
    var target = Path.Combine(temp, "existing-ledger"); Directory.CreateDirectory(target);
    var settingsPath = Path.Combine(target, "settings.json"); File.WriteAllText(settingsPath, "do not overwrite");
    Throws(() => DataDirectoryMigration.CopyToEmptyDirectory(source.DatabasePath, target, new Settings()));
    True(File.ReadAllText(settingsPath) == "do not overwrite"); True(!File.Exists(Path.Combine(target, "focusledger.db")));
}));
tests.Add(("Failed migration does not change location or source", () =>
{
    var fakeDatabase = Path.Combine(temp, "invalid.db"); File.WriteAllText(fakeDatabase, "not a database");
    var location = new DataLocation(Path.Combine(temp, "failure-program"), Path.Combine(temp, "failure-profile"));
    location.Save(location.DefaultDirectory);
    var target = Path.Combine(temp, "failed-target");
    Throws(() => DataDirectoryMigration.CopyToEmptyDirectory(fakeDatabase, target, new Settings()));
    True(location.Load() == location.DefaultDirectory); True(File.ReadAllText(fakeDatabase) == "not a database");
    True(!File.Exists(Path.Combine(target, "focusledger.db")));
}));
tests.Add(("Invalid location config fails without replacing it", () =>
{
    var location = new DataLocation(Path.Combine(temp, "broken-program"), Path.Combine(temp, "broken-profile"));
    Directory.CreateDirectory(location.ExecutableDirectory); File.WriteAllText(location.PortableConfigPath, "broken");
    Throws(() => location.Load()); True(File.ReadAllText(location.PortableConfigPath) == "broken");
    Throws(() => DataLocation.Normalize("relative-path"));
}));

tests.Add(("Sub-millisecond collector tails are ignored and next valid sample persists", () =>
{
    using var db = Db("precision-tail");
    db.Append(new(0, origin, origin.AddSeconds(1), "Code", SegmentKind.Active));
    db.Append(new(0, origin.AddSeconds(1), origin.AddSeconds(1).AddTicks(1), "Code", SegmentKind.Idle));
    db.Append(new(0, origin.AddSeconds(1).AddTicks(1), origin.AddSeconds(2).AddTicks(7), "Code", SegmentKind.Active));
    Equal(2, Effective(db.ReadAll())); Equal(1, db.ReadAll().Count); True(db.IntegrityCheck() == "ok");
}));
tests.Add(("Manual boundaries normalize before splitting and invalid precision preserves original", () =>
{
    using var db = Db("precision-manual");
    db.Append(new(0, origin, origin.AddSeconds(2), "Code", SegmentKind.Active));
    db.SaveManual(new(0, origin.AddTicks(1), origin.AddSeconds(1).AddTicks(1), "Read", SegmentKind.Manual));
    Equal(2, db.ReadAll().Count); Equal(2, Effective(db.ReadAll()));
    Throws(() => db.SaveManual(new(0, origin.AddTicks(1), origin.AddTicks(2), "Read", SegmentKind.Manual), db.ReadAll()[0].Id));
    Equal(2, Effective(db.ReadAll())); True(db.IntegrityCheck() == "ok");
}));
tests.Add(("Fractional sample timestamps never create phantom idle tails", () =>
{
    var engine = new TrackingEngine(); using var db = Db("precision-engine");
    for (int i = 0; i < 2000; i++)
        foreach (var segment in engine.Push(Sample(i) with { Utc = origin.AddSeconds(i).AddTicks((i * 7919) % 10000) }))
        { True(segment.Kind == SegmentKind.Active, "Phantom idle tail"); db.Append(segment); }
    Equal(1999, Effective(db.ReadAll())); Equal(1, db.ReadAll().Count);
}));

var failed = 0;
foreach (var test in tests)
{
    try { test.Test(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed. Test data: {temp}");
return failed == 0 ? 0 : 1;
