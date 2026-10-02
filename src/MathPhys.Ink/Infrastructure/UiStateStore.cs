using System.IO;

namespace MathPhys.Ink.Infrastructure;

/// <summary>
/// 跨次启动要记住的一点点界面状态（全屏 / 主题）。
/// </summary>
/// <remarks>
/// 用一行一个键值对而不是 JSON：出现"怎么又全屏了""怎么变成浅色了"这类疑问时，
/// 用记事本打开就能看懂、也能直接改回来；这个体量的东西值不上一个序列化库，
/// 更不值上"读坏了整个程序起不来"的风险。
/// <para>
/// 所以任何读写失败都被吞掉 —— 它只是个可有可无的偏好记录（权限、杀软锁文件都可能发生）。
/// </para>
/// <para>
/// ★ 写入必须是**读-改-写**（M10 S6 改）：早先的实现是 <c>File.WriteAllText</c> 整体覆盖，
/// 加到第二项（主题）时就会把第一项（全屏）擦掉 ——
/// 表现为"记住浅色之后，全屏状态突然记不住了"。
/// 现在只替换目标键那一行，其余行原样保留（包括我们不认识的键）。
/// </para>
/// </remarks>
public static class UiStateStore
{
    private const string FullScreenKey = "fullscreen";
    private const string ThemeKey = "theme";

    // ---- M12 新增的三个偏好键（沿用同一套"读不出 = 没记录"的语义）----

    /// <summary>顶部工具栏是否处于隐藏（沉浸）状态。</summary>
    private const string ToolHiddenKey = "toolbar_hidden";

    /// <summary>悬浮球的显示策略：<c>auto</c>（工具栏隐藏时出现，默认）/ <c>always</c> / <c>off</c>。</summary>
    private const string BallModeKey = "floating_ball";

    /// <summary>悬浮球沿右缘的垂直位置（0~1 的比例值）。</summary>
    private const string BallYKey = "ball_y";

    /// <summary>上次使用的工作台模式 Id（批改 / 讲评 / 演示）。</summary>
    private const string ModeKey = "mode";

    // ---- M13 新增的四个偏好键 ----

    /// <summary>纸张底色预设 Id（white / cream / green / blue）。</summary>
    private const string PaperColorKey = "paper_color";

    /// <summary>
    /// M23 一次性迁移标记：旧版本（M21 之前）的出厂默认是「白」，升级前只要程序写过
    /// 这个键，文件里就会留下 white —— M21 把出厂默认改成深灰后，它会被当成
    /// 「用户显式选择」一直被尊重，老师看到的就是「怎么还是白的」。
    /// 标记只在迁移时写一次；之后老师再手动点白，照常尊重（标记已置位，不再动）。
    /// </summary>
    private const string PaperMigratedKey = "paper_color_m23";

    /// <summary>悬浮球的水平位置比例（0~1）。</summary>
    private const string BallXKey = "ball_x";

    /// <summary>浮动工具栏的位置比例 X / Y（0~1，按画布尺寸归一）。</summary>
    private const string ToolbarXKey = "toolbar_x";
    private const string ToolbarYKey = "toolbar_y";

    /// <summary>状态文件：%LOCALAPPDATA%\MathPhys.Ink\ui-state.txt（与日志同一层目录）。</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MathPhys.Ink", "ui-state.txt");

    /// <summary>上次的全屏状态；没有记录（首次运行）或读不出来时返回 <c>null</c>。</summary>
    /// <param name="filePath">
    /// 只为 harness 存在的覆盖口：自动化测试不能去动用户真实的偏好文件
    /// （会把人家"上次是窗口模式"的选择重置掉）。生产代码一律不传。
    /// </param>
    public static bool? ReadFullScreen(string? filePath = null)
    {
        var value = ReadValue(FullScreenKey, filePath);
        if (value is null) return null;

        if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>记住全屏状态。写失败不影响本次使用。</summary>
    /// <param name="fullScreen">是否全屏。</param>
    /// <param name="filePath">同 <see cref="ReadFullScreen"/>：只为 harness 存在的覆盖口。</param>
    public static void WriteFullScreen(bool fullScreen, string? filePath = null)
        => WriteValue(FullScreenKey, fullScreen ? "1" : "0", filePath);

    /// <summary>上次的界面主题（<c>dark</c> / <c>light</c>）；没有记录时返回 <c>null</c>。</summary>
    /// <param name="filePath">同 <see cref="ReadFullScreen"/>。</param>
    public static string? ReadTheme(string? filePath = null) => ReadValue(ThemeKey, filePath);

    /// <summary>记住界面主题。写失败不影响本次使用。</summary>
    /// <param name="theme">主题标识（<c>dark</c> / <c>light</c>）。</param>
    /// <param name="filePath">同 <see cref="ReadFullScreen"/>。</param>
    public static void WriteTheme(string theme, string? filePath = null)
        => WriteValue(ThemeKey, theme, filePath);

    // ---------------------------------------------------------------- M12 偏好

    /// <summary>上次的工具栏隐藏状态；没有记录时返回 <c>null</c>（按显示处理）。</summary>
    public static bool? ReadToolHidden(string? filePath = null)
    {
        var value = ReadValue(ToolHiddenKey, filePath);
        return value switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => null,
        };
    }

    /// <summary>记住工具栏隐藏状态。写失败不影响本次使用。</summary>
    public static void WriteToolHidden(bool hidden, string? filePath = null)
        => WriteValue(ToolHiddenKey, hidden ? "1" : "0", filePath);

    /// <summary>悬浮球显示策略（<c>auto</c> / <c>always</c> / <c>off</c>）；没有记录时返回 <c>null</c>。</summary>
    public static string? ReadBallMode(string? filePath = null) => ReadValue(BallModeKey, filePath);

    /// <summary>记住悬浮球显示策略。写失败不影响本次使用。</summary>
    public static void WriteBallMode(string mode, string? filePath = null)
        => WriteValue(BallModeKey, mode, filePath);

    /// <summary>悬浮球的垂直位置比例（0~1）；读不出或不是合法比例时返回 <c>null</c>。</summary>
    public static double? ReadBallY(string? filePath = null)
    {
        var value = ReadValue(BallYKey, filePath);
        if (value is null) return null;

        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double ratio))
        {
            return null;
        }

        return ratio is >= 0.0 and <= 1.0 ? ratio : null;
    }

    /// <summary>记住悬浮球的垂直位置比例。写失败不影响本次使用。</summary>
    public static void WriteBallY(double ratio, string? filePath = null)
        => WriteValue(BallYKey, ratio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), filePath);

    /// <summary>上次使用的工作台模式 Id；没有记录时返回 <c>null</c>。</summary>
    public static string? ReadMode(string? filePath = null) => ReadValue(ModeKey, filePath);

    /// <summary>记住工作台模式 Id。写失败不影响本次使用。</summary>
    public static void WriteMode(string modeId, string? filePath = null)
        => WriteValue(ModeKey, modeId, filePath);

    // ---------------------------------------------------------------- M13 偏好

    /// <summary>上次的纸张底色预设 Id；没有记录时返回 <c>null</c>（按白色处理）。</summary>
    public static string? ReadPaperColor(string? filePath = null) => ReadValue(PaperColorKey, filePath);

    /// <summary>记住纸张底色预设 Id。写失败不影响本次使用。</summary>
    public static void WritePaperColor(string colorId, string? filePath = null)
        => WriteValue(PaperColorKey, colorId, filePath);

    /// <summary>M23 一次性迁移标记读过没有（只应为真一次）。</summary>
    public static bool ReadPaperMigratedV23(string? filePath = null)
        => ReadValue(PaperMigratedKey, filePath) == "1";

    /// <summary>写下 M23 一次性迁移标记。写失败不影响本次使用。</summary>
    public static void WritePaperMigratedV23(string? filePath = null)
        => WriteValue(PaperMigratedKey, "1", filePath);

    /// <summary>按键读 0~1 的比例值（ball_x / toolbar_x / toolbar_y 共用）；非法一律 null。</summary>
    private static double? ReadRatio(string key, string? filePath)
    {
        var value = ReadValue(key, filePath);
        if (value is null) return null;

        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double ratio))
        {
            return null;
        }

        return ratio is >= 0.0 and <= 1.0 ? ratio : null;
    }

    /// <summary>按键写 0~1 的比例值，固定三位小数（与 ball_y 同一不变式格式）。</summary>
    private static void WriteRatio(string key, double ratio, string? filePath)
        => WriteValue(key, ratio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), filePath);

    /// <summary>悬浮球的水平位置比例（0~1）；读不出或不是合法比例时返回 <c>null</c>。</summary>
    public static double? ReadBallX(string? filePath = null) => ReadRatio(BallXKey, filePath);

    /// <summary>记住悬浮球的水平位置比例。写失败不影响本次使用。</summary>
    public static void WriteBallX(double ratio, string? filePath = null)
        => WriteRatio(BallXKey, ratio, filePath);

    /// <summary>浮动工具栏的水平位置比例（0~1）；读不出或不是合法比例时返回 <c>null</c>。</summary>
    public static double? ReadToolbarX(string? filePath = null) => ReadRatio(ToolbarXKey, filePath);

    /// <summary>记住浮动工具栏的水平位置比例。写失败不影响本次使用。</summary>
    public static void WriteToolbarX(double ratio, string? filePath = null)
        => WriteRatio(ToolbarXKey, ratio, filePath);

    /// <summary>浮动工具栏的垂直位置比例（0~1）；读不出或不是合法比例时返回 <c>null</c>。</summary>
    public static double? ReadToolbarY(string? filePath = null) => ReadRatio(ToolbarYKey, filePath);

    /// <summary>记住浮动工具栏的垂直位置比例。写失败不影响本次使用。</summary>
    public static void WriteToolbarY(double ratio, string? filePath = null)
        => WriteRatio(ToolbarYKey, ratio, filePath);

    // ---------------------------------------------------------------- 内部

    /// <summary>读一个键的值。读不出来（不存在 / 被锁 / 编码坏）一律返回 null。</summary>
    private static string? ReadValue(string key, string? filePath)
    {
        var path = filePath ?? FilePath;

        try
        {
            if (!File.Exists(path)) return null;

            string prefix = key + "=";
            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var value = text[prefix.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch
        {
            // 读不出来就当"没有记录"，回落到出厂默认
        }

        return null;
    }

    /// <summary>写一个键：只替换它那一行，其余行原样保留。</summary>
    private static void WriteValue(string key, string value, string? filePath)
    {
        var path = filePath ?? FilePath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string prefix = key + "=";
            var lines = new List<string>();
            bool replaced = false;

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // ★ 同一个键只留一行：出现过两次时，最后一次写入说了算
                        if (!replaced) lines.Add($"{key}={value}");
                        replaced = true;
                        continue;
                    }

                    lines.Add(line);
                }
            }

            if (!replaced) lines.Add($"{key}={value}");

            File.WriteAllLines(path, lines);
        }
        catch
        {
            // 记不住偏好不是功能问题，下次大不了按出厂默认来
        }
    }
}
