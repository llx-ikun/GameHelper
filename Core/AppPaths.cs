namespace GameHelper.Core;

/// <summary>
/// 输出路径的决策。
///
/// 优先把日志写在 exe 旁边的「日志」文件夹里 —— 用户最容找到；
/// 如果那个位置不可写（例如程序被放在 C:\Program Files），
/// 就回退到「文档」目录，而不是直接报错。
/// </summary>
public static class AppPaths
{
    private static string? _outputDir;
    private static string? _rootDir;

    /// <summary>程序所在目录。</summary>
    public static string RootDir
    {
        get
        {
            if (_rootDir is not null)
            {
                return _rootDir;
            }

            string exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            _rootDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            return _rootDir;
        }
    }

    /// <summary>日志与报告的输出目录，保证存在且可写。</summary>
    public static string OutputDir
    {
        get
        {
            if (_outputDir is not null)
            {
                return _outputDir;
            }

            string beside = Path.Combine(RootDir, "日志");
            if (TryPrepare(beside))
            {
                _outputDir = beside;
                return _outputDir;
            }

            string docs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "游戏助手");
            if (TryPrepare(docs))
            {
                _outputDir = docs;
                return _outputDir;
            }

            // 最后兜底：临时目录。至少不会因为写不了日志而崩掉。
            string tmp = Path.Combine(Path.GetTempPath(), "游戏助手");
            TryPrepare(tmp);
            _outputDir = tmp;
            return _outputDir;
        }
    }

    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
