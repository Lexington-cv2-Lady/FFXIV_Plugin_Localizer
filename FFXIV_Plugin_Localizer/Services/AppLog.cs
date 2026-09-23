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

    /// <summary> 单个日志文件大小上限，超过则轮转（默认 10 MB）。 </summary>
    public const long MaxFileSize = 10L * 1024 * 1024;
    /// <summary> 轮转保留的旧日志份数（<c>汉化日志.1.log … .N.log</c>）。 </summary>
    public const int KeepFiles = 3;
    /// <summary> 当前日志文件已写入的字节数（启动时读一次，之后累加，避免每条日志都 stat 文件）。 </summary>
    private long _fileBytes;

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
                try { _fileBytes = new FileInfo(filePath).Length; } catch { _fileBytes = 0; } // 含刚写的启动头
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
        var line = $"{e.Time:yyyy-MM-dd HH:mm:ss} [{Tag(e.Lv)}] {e.Text}{Environment.NewLine}";
        try
        {
            lock (_fileLock)
            {
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_fileBytes > 0 && _fileBytes + bytes > MaxFileSize)
                    RotateFile();   // 超限先轮转（内部把 _fileBytes 归零）
                File.AppendAllText(FilePath, line, Encoding.UTF8);
                _fileBytes += bytes;
            }
        }
        catch
        {
            /* 落盘失败不影响内存日志 */
        }
    }

    /// <summary>
    /// 日志轮转（在 <c>_fileLock</c> 内调用）。
    ///   · 历史遗留的**超大**日志（无轮转时期累积、&gt; MaxFileSize×KeepFiles）：直接删除、立即释放空间；
    ///   · 否则：当前 → <c>.1.log</c>，<c>.1→.2 …</c>，删除最旧的 <c>.N.log</c>。
    /// 命名在扩展名前插序号：<c>汉化日志.log → 汉化日志.1.log</c>。
    /// </summary>
    private void RotateFile()
    {
        var basePath = FilePath!;
        var dir = Path.GetDirectoryName(basePath) ?? "";
        var noExt = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        string Numbered(int n) => Path.Combine(dir, $"{noExt}.{n}{ext}");

        // ① 历史超大文件（如本次发现的 102MB 无轮转日志）：直接删，不占轮转名额
        try
        {
            if (new FileInfo(basePath).Length > MaxFileSize * KeepFiles)
            {
                File.Delete(basePath);
                _fileBytes = 0;
                return;
            }
        }
        catch { /* 删不动则按正常轮转走 */ }

        // ② 正常轮转：删最旧 → 从高到低依次后移（每个目标都已被腾空，File.Move 不会撞名）
        var oldest = Numbered(KeepFiles);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = KeepFiles; i >= 1; i--)
        {
            var src = i == 1 ? basePath : Numbered(i - 1);
            var dst = Numbered(i);
            if (File.Exists(src)) File.Move(src, dst);
        }
        _fileBytes = 0;
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
            try { lock (_fileLock) { File.WriteAllText(FilePath, "", Encoding.UTF8); _fileBytes = 0; } } catch { }
        }
        Changed?.Invoke();
    }
}
