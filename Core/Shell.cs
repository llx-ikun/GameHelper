using System.Diagnostics;
using System.Text;

namespace GameHelper.Core;

/// <summary>一次外部命令调用的结果。</summary>
public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>按行返回标准输出，已去除空行。</summary>
    public IEnumerable<string> Lines()
    {
        foreach (var raw in StdOut.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}

/// <summary>
/// 外部进程调用的统一封装。
///
/// 两个细节值得说明：
///   1. stdout / stderr 用异步读，避免管道写满导致的死锁 —— 这在
///      nvidia-smi -q（输出上千行）时是会真实发生的。
///   2. 超时后强制 Kill 整棵进程树，不让残留进程挂着。
/// </summary>
public static class Shell
{
    public static ShellResult Run(string fileName, string arguments, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, string.Empty, ex.Message);
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能正好自己退出了，忽略。
            }
            return new ShellResult(-1, SafeResult(outTask), "命令超时：" + fileName);
        }

        try
        {
            proc.WaitForExit(); // 确保异步读取全部冲刷完毕
        }
        catch
        {
            // 忽略
        }

        return new ShellResult(proc.ExitCode, SafeResult(outTask), SafeResult(errTask));
    }

    private static string SafeResult(Task<string> t)
    {
        try
        {
            return t.Wait(2000) ? t.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
