using System.Text.Json;
using FocusLedger.Core;

namespace FocusLedger.Storage;

public static class SettingsFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new InvalidDataException("空设置");
            settings.Validate();
            return settings;
        }
        catch (Exception ex) { throw new InvalidDataException($"设置文件无法读取：{path}\n原文件已保留，请检查或从 .bak 恢复。", ex); }
    }
    public static Settings Clone(Settings settings) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
    public static void Save(string path, Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }
}
