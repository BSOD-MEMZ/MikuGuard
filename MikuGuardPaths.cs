using System.Reflection;

namespace MikuGuard;

/// <summary>插件自身的路径（AppContext.BaseDirectory 是宿主的目录，别用错了）。</summary>
public static class MikuGuardPaths
{
    private static string? _pluginDir;

    public static string PluginDirectory =>
        _pluginDir ??= Path.GetDirectoryName(typeof(MikuGuardPaths).Assembly.Location) ?? "";

    /// <summary>解析插件内相对路径；绝对路径原样返回。</summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(PluginDirectory, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
