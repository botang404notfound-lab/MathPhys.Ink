using System.IO;
using System.Text;

namespace MathPhys.Ink.Infrastructure;

/// <summary>
/// 极简落盘日志。一体机上没有调试器，出了状况只能翻日志文件。
/// </summary>
/// <remarks>
/// 必须是纯静态、线程安全的工具：<b>渲染线程也要能记日志</b>——
/// 它在后台跑，既不能弹窗，也不能把异常抛给 UI 线程，只能安静地写一行然后继续。
/// </remarks>
public static class AppLog
{
    // 静态锁即可：日志量极小，不值得为它做无锁设计
    private static readonly object Gate = new();

    /// <summary>日志目录：%LOCALAPPDATA%\MathPhys.Ink\logs</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MathPhys.Ink", "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Write(string tag, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");

                var sb = new StringBuilder();
                sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] =====");
                sb.AppendLine(message);
                if (exception is not null)
                {
                    sb.AppendLine(exception.ToString());
                }
                sb.AppendLine();

                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身失败绝不能再抛异常，直接吞掉
        }
    }
}
