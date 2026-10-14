using System.Text;
using System.Text.Json;
using FocusLedger.Core;
using Microsoft.Data.Sqlite;

namespace FocusLedger.Storage;

public sealed class LedgerStore : IDisposable
{
    private readonly SqliteConnection connection;
    public string DatabasePath { get; }

    public LedgerStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=3000;
            CREATE TABLE IF NOT EXISTS segments (
                id INTEGER PRIMARY KEY, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                app TEXT NOT NULL, kind INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '',
                CHECK(end_ms > start_ms));
            CREATE INDEX IF NOT EXISTS ix_segments_time ON segments(start_ms, end_ms);
            CREATE TABLE IF NOT EXISTS corrections (
                id INTEGER PRIMARY KEY, at_utc TEXT NOT NULL, action TEXT NOT NULL,
                before_json TEXT NOT NULL, after_json TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string, object)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private List<Segment> Query(string sql, SqliteTransaction? transaction = null, params (string, object)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        List<Segment> result = [];
        while (reader.Read()) result.Add(new(reader.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3), (SegmentKind)reader.GetInt32(4), reader.GetString(5)));
        return result;
    }

    private void Insert(Segment s, SqliteTransaction? transaction = null) =>
        Execute("INSERT INTO segments(start_ms,end_ms,app,kind,note) VALUES($start,$end,$app,$kind,$note)", transaction,
            ("$start", s.Start.ToUnixTimeMilliseconds()), ("$end", s.End.ToUnixTimeMilliseconds()), ("$app", s.App), ("$kind", (int)s.Kind), ("$note", s.Note));

    public void Append(Segment segment)
    {
        if (!segment.IsEffective) return;
        segment = MillisecondPrecision(segment);
        if (segment.End <= segment.Start) return;
        using var tx = connection.BeginTransaction();
        var last = Query("SELECT * FROM segments ORDER BY end_ms DESC LIMIT 1", tx).FirstOrDefault();
        // Wall clock rollback or a recent manual correction must never double-count.
        if (last is not null && segment.Start < last.End) segment = segment with { Start = last.End };
        if (segment.End <= segment.Start) { tx.Commit(); return; }
        if (last is not null && last.End == segment.Start && last.App == segment.App && last.Kind == segment.Kind && last.Note == segment.Note)
            Execute("UPDATE segments SET end_ms=$end WHERE id=$id", tx, ("$end", segment.End.ToUnixTimeMilliseconds()), ("$id", last.Id));
        else Insert(segment, tx);
        tx.Commit();
    }

    public List<Segment> Read(DateTimeOffset start, DateTimeOffset end) =>
        Query("SELECT * FROM segments WHERE end_ms > $start AND start_ms < $end ORDER BY start_ms", null,
            ("$start", start.ToUnixTimeMilliseconds()), ("$end", end.ToUnixTimeMilliseconds()));

    public List<Segment> ReadAll() => Query("SELECT * FROM segments ORDER BY start_ms");

    public void SaveManual(Segment replacement, long? editingId = null, DateTimeOffset? now = null)
    {
        replacement = MillisecondPrecision(replacement);
        if (replacement.End <= replacement.Start) throw new ArgumentException("结束时间必须晚于开始时间。");
        if (replacement.End > (now ?? DateTimeOffset.UtcNow)) throw new ArgumentException("不能补记未来时间。");
        if (string.IsNullOrWhiteSpace(replacement.App)) throw new ArgumentException("请选择或填写软件名称。");
        if (replacement.Note.Length > 500) throw new ArgumentException("备注最多 500 字。");
        replacement = replacement with { Id = 0, Kind = SegmentKind.Manual };
        using var tx = connection.BeginTransaction();
        List<Segment> before = [];
        if (editingId is not null)
        {
            var original = Query("SELECT * FROM segments WHERE id=$id", tx, ("$id", editingId.Value));
            if (original.Count != 1) throw new InvalidOperationException("该记录已不存在，请刷新后重试。");
            before.AddRange(original);
            Execute("DELETE FROM segments WHERE id=$id", tx, ("$id", editingId.Value));
        }
        var overlaps = Query("SELECT * FROM segments WHERE end_ms > $start AND start_ms < $end", tx,
            ("$start", replacement.Start.ToUnixTimeMilliseconds()), ("$end", replacement.End.ToUnixTimeMilliseconds()));
        before.AddRange(overlaps);
        foreach (var overlap in overlaps)
        {
            Execute("DELETE FROM segments WHERE id=$id", tx, ("$id", overlap.Id));
            if (overlap.Start < replacement.Start) Insert(overlap with { End = replacement.Start }, tx);
            if (overlap.End > replacement.End) Insert(overlap with { Start = replacement.End }, tx);
        }
        Insert(replacement, tx);
        Audit("manual", before, replacement, tx);
        tx.Commit();
    }

    public void Delete(long id)
    {
        using var tx = connection.BeginTransaction();
        var before = Query("SELECT * FROM segments WHERE id=$id", tx, ("$id", id));
        Execute("DELETE FROM segments WHERE id=$id", tx, ("$id", id));
        Audit("delete", before, (object?)null, tx);
        tx.Commit();
    }

    private void Audit(string action, object before, object? after, SqliteTransaction tx) =>
        Execute("INSERT INTO corrections(at_utc,action,before_json,after_json) VALUES($at,$action,$before,$after)", tx,
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$action", action),
            ("$before", JsonSerializer.Serialize(before)), ("$after", JsonSerializer.Serialize(after)));

    public void Backup(string destination)
    {
        if (Path.GetFullPath(destination).Equals(DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("备份不能覆盖正在使用的数据库。");
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        connection.BackupDatabase(target);
    }

    public string IntegrityCheck()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return (string)command.ExecuteScalar()!;
    }

    public static void ExportCsv(string path, IEnumerable<Segment> segments, TimeZoneInfo zone)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("开始时间,结束时间,软件,来源,时长秒,备注");
        foreach (var s in segments.Where(s => s.IsEffective))
            writer.WriteLine(string.Join(",", Cell(TimeZoneInfo.ConvertTime(s.Start, zone).ToString("yyyy-MM-dd HH:mm:ss zzz")),
                Cell(TimeZoneInfo.ConvertTime(s.End, zone).ToString("yyyy-MM-dd HH:mm:ss zzz")), Cell(s.App), Cell(s.SourceLabel),
                s.Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), Cell(s.Note)));
    }

    public void Dispose() => connection.Dispose();

    // Normalize before comparison, merging and splitting: SQLite stores integer milliseconds.
    private static Segment MillisecondPrecision(Segment segment) => segment with
    {
        Start = DateTimeOffset.FromUnixTimeMilliseconds(segment.Start.ToUnixTimeMilliseconds()),
        End = DateTimeOffset.FromUnixTimeMilliseconds(segment.End.ToUnixTimeMilliseconds())
    };
}
