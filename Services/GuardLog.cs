using System.Text;

namespace MikuGuard.Services;

/// <summary>插件日志：写到配置目录下的 mikuguard.log，方便排查（宿主不显示插件控制台）。</summary>
public static class GuardLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Initialize(string configFolder)
    {
        try
        {
            Directory.CreateDirectory(configFolder);
            _path = System.IO.Path.Combine(configFolder, "mikuguard.log");
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 512 * 1024)
                fi.Delete();
            Info("========== MikuGuard 启动 ==========");
        }
        catch
        {
            // 日志不可用不应影响插件运行
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // ignore
        }
    }
}
