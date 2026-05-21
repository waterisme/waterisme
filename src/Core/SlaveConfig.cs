using System;
using System.IO;
using System.Text.Json;

namespace RemoteDesktop.Core;

/// <summary>
/// Persistent slave-side settings (custom PIN, etc.) stored at
/// %APPDATA%\RemoteDesktop\slave.json.
/// </summary>
public sealed class SlaveConfig
{
    public string? CustomPin { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteDesktop", "slave.json");

    public static SlaveConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<SlaveConfig>(json) ?? new SlaveConfig();
            }
        }
        catch { }
        return new SlaveConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
