using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemoteDesktop.Core;

public sealed class ConnectionEntry
{
    public string  Nickname { get; set; } = "";
    public string  Ip       { get; set; } = "";
    public int     Port     { get; set; } = 7890;
    /// <summary>
    /// 上次成功連線時使用的 PIN（顏色名 e.g. "紅" 或 自訂 PIN 文字）。
    /// 舊版 history.json 沒有此欄位 → 反序列化為 null。
    /// </summary>
    public string? LastPin  { get; set; }

    public override string ToString()
    {
        string head = string.IsNullOrWhiteSpace(Nickname) ? Ip : $"{Nickname}  ({Ip})";
        if (string.IsNullOrEmpty(LastPin)) return head;
        // 顏色名直接顯示，自訂 PIN 用 ●●● 遮罩避免肩窺
        string hint = IsColorName(LastPin) ? LastPin : "●●●";
        return $"{head}  [{hint}]";
    }

    private static bool IsColorName(string s) =>
        s == "紅" || s == "藍" || s == "黃" || s == "黑" || s == "白";
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

    public static void AddOrUpdate(string nickname, string ip, string? pin = null, int port = 7890)
    {
        var list = Load();
        // 若同 IP/port 已有舊紀錄，且本次未帶 pin，保留舊 pin
        string? oldPin = null;
        foreach (var e in list)
            if (e.Ip == ip && e.Port == port) { oldPin = e.LastPin; break; }

        list.RemoveAll(e => e.Ip == ip && e.Port == port);
        list.Insert(0, new ConnectionEntry
        {
            Nickname = nickname.Trim(),
            Ip       = ip.Trim(),
            Port     = port,
            LastPin  = string.IsNullOrEmpty(pin) ? oldPin : pin,
        });
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
