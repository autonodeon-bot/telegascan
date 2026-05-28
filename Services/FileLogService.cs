using System.IO;

namespace TelegaScan.Services;

public sealed class FileLogService : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _path;

    public bool Enabled { get; set; } = true;

    public void SetLogFile(string? path)
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _path = path;
            if (string.IsNullOrWhiteSpace(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            _writer.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} сессия ---");
        }
    }

    public void Write(string line)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(_path)) return;
        lock (_lock)
        {
            try { _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"); }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
