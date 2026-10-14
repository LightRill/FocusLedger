using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusLedger.Core;
using Microsoft.Data.Sqlite;

namespace FocusLedger.Storage;

/// <summary>A small bootstrap file locates the settings and database before they can be read.</summary>
public sealed class DataLocation
{
    public string ExecutableDirectory { get; }
    public string DefaultDirectory => Path.Combine(ExecutableDirectory, "data");
    public string PortableConfigPath => Path.Combine(ExecutableDirectory, "focusledger.location.json");
    public string UserConfigPath { get; }
    public bool IsConfigured => File.Exists(UserConfigPath) || File.Exists(PortableConfigPath);

    public DataLocation(string executableDirectory, string? userConfigRoot = null)
    {
        ExecutableDirectory = Normalize(executableDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ExecutableDirectory.ToUpperInvariant())))[..20];
        UserConfigPath = Path.Combine(userConfigRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusLedger", "locations"), key + ".json");
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new ArgumentException("请选择完整的数据目录路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }
    public static bool Same(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    public string Load()
    {
        var path = File.Exists(UserConfigPath) ? UserConfigPath : PortableConfigPath;
        if (!File.Exists(path)) return DefaultDirectory;
        try
        {
            var config = JsonSerializer.Deserialize<LocationConfig>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(config?.DataDirectory)) throw new InvalidDataException("数据路径为空。");
            return Normalize(Path.GetFullPath(config.DataDirectory, ExecutableDirectory));
        }
        catch (Exception ex) { throw new InvalidDataException($"无法读取数据位置配置：{path}。原文件已保留。", ex); }
    }

    public void Save(string directory)
    {
        directory = Normalize(directory);
        var value = Same(directory, DefaultDirectory) ? "data" : directory;
        var json = JsonSerializer.Serialize(new LocationConfig(value), new JsonSerializerOptions { WriteIndented = true });
        // Keep using an existing user override so a stale portable file never wins.
        if (File.Exists(UserConfigPath)) { SaveAtomic(UserConfigPath, json); return; }
        try { SaveAtomic(PortableConfigPath, json); }
        catch (UnauthorizedAccessException) { SaveAtomic(UserConfigPath, json); }
        catch (System.Security.SecurityException) { SaveAtomic(UserConfigPath, json); }
    }

    private static void SaveAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record LocationConfig(string DataDirectory);
}

public static class DataDirectoryMigration
{
    public static bool HasDatabase(string directory) => File.Exists(Path.Combine(directory, "focusledger.db"));

    public static void EnsureWritable(string directory)
    {
        directory = DataLocation.Normalize(directory);
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".focusledger-write-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        file.WriteByte(1);
        file.Flush(true);
    }

    /// <summary>Take an online SQLite snapshot including WAL data. Do not delete or modify the source.</summary>
    public static void CopyToEmptyDirectory(string sourceDatabase, string targetDirectory, Settings settings)
    {
        settings.Validate();
        if (!File.Exists(sourceDatabase)) throw new FileNotFoundException("原数据库不存在。", sourceDatabase);
        targetDirectory = DataLocation.Normalize(targetDirectory);
        EnsureWritable(targetDirectory);
        var reserved = new[] { "focusledger.db", "focusledger.db-wal", "focusledger.db-shm", "settings.json", "settings.json.bak" };
        if (reserved.Any(name => File.Exists(Path.Combine(targetDirectory, name))))
            throw new IOException("目标目录已有记录或设置。请选择其他目录，或明确切换到已有记录。");
        var token = ".focusledger-copy-" + Guid.NewGuid().ToString("N");
        var tempDb = Path.Combine(targetDirectory, token + ".db");
        var tempSettings = Path.Combine(targetDirectory, token + ".json");
        var finalDb = Path.Combine(targetDirectory, "focusledger.db");
        var finalSettings = Path.Combine(targetDirectory, "settings.json");
        var createdDatabase = false;
        var completed = false;
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDb, Pooling = false }.ToString()))
            {
                source.Open(); target.Open(); source.BackupDatabase(target);
            }
            using (var verification = new LedgerStore(tempDb))
                if (verification.IntegrityCheck() != "ok") throw new InvalidDataException("复制后的数据库校验失败，仍使用原目录。");
            SettingsFile.Save(tempSettings, settings);
            File.Move(tempDb, finalDb); // Never overwrite an existing destination.
            createdDatabase = true;
            File.Move(tempSettings, finalSettings);
            completed = true;
        }
        finally
        {
            if (!completed && createdDatabase && File.Exists(finalDb)) File.Delete(finalDb);
            foreach (var file in new[] { tempDb, tempDb + "-wal", tempDb + "-shm", tempSettings, tempSettings + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
