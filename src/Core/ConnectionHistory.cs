using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemoteDesktop.Core;

public sealed class ConnectionEntry
{
    public string Nickname { get; set; } = "";
    public string Ip       { get; set; } = "";
    public int    Port     { get; set; } = 7890;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Nickname) ? Ip : $"{Nickname}  ({Ip})";
}

public static class ConnectionHistory
{
    private static readonly string Dir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesktop");
    private static readonly string File =
        Path.Combine(Dir, "history.json");

    private const int Max = 10;

    public static List<ConnectionEntry> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new();
            var json = System.IO.File.ReadAllText(File);
            return JsonSerializer.Deserialize<List<ConnectionEntry>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void AddOrUpdate(string nickname, string ip, int port = 7890)
    {
        var list = Load();
        list.RemoveAll(e => e.Ip == ip && e.Port == port);
        list.Insert(0, new ConnectionEntry { Nickname = nickname.Trim(), Ip = ip.Trim(), Port = port });
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
