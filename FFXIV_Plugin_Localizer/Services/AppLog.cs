using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FFXIVPluginLocalizer.Services;

/// <summary> 应用日志（内存环形缓冲 + 可选文件落盘）。级别：信息/警告/错误。搬自旧项目。 </summary>
public sealed class AppLog
{
    public enum Level { Info, Warn, Error }

    public sealed record Entry(DateTime Time, Level Lv, string Text);

    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private const int Capacity = 2000;

    /// <summary> 日志文件路径（为空表示仅内存日志，不落盘）。 </summary>
    public string? FilePath { get; }

    /// <summary> 最近一条错误（置底显示用）。 </summary>
    public string LastError { get; private set; } = "";

    public event Action? Changed;

    /// <summary> 构造。传入 filePath 时把每条日志同步追加到该文件（启动时写一条会话分隔头）。 </summary>
    public AppLog(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(filePath,
                    $"{Environment.NewLine}===== 插件启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====" +
                    $"{Environment.NewLine}", Encoding.UTF8);
                FilePath = filePath;
            }
            catch
            {
                FilePath = null; // 落盘不可用则退化为纯内存日志
            }
        }
    }

    public void Info(string text) => Add(Level.Info, text);
    public void Warn(string text) => Add(Level.Warn, text);
    public void Error(string text)
    {
        Add(Level.Error, text);
        LastError = $"[{DateTime.Now:HH:mm:ss}] {text}";
        Changed?.Invoke();
    }

    private void Add(Level lv, string text)
    {
        var entry = new Entry(DateTime.Now, lv, text);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        }
        WriteToFile(entry);
    }

    private void WriteToFile(Entry e)
    {
        if (FilePath == null) return;
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(FilePath,
                    $"{e.Time:yyyy-MM-dd HH:mm:ss} [{Tag(e.Lv)}] {e.Text}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            /* 落盘失败不影响内存日志 */
        }
    }

    private static string Tag(Level lv) => lv switch
    {
        Level.Error => "错误",
        Level.Warn => "警告",
        _ => "信息"
    };

    /// <summary> 快照（新→旧）。 </summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.AsEnumerable().Reverse().ToList();
        }
    }

    /// <summary> 清除日志（内存与日志文件一并清空）。 </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            LastError = "";
        }
        if (FilePath != null)
        {
            try { lock (_fileLock) File.WriteAllText(FilePath, "", Encoding.UTF8); } catch { }
        }
        Changed?.Invoke();
    }
}
