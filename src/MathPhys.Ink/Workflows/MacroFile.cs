using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MathPhys.Ink.Workflows;

/// <summary>一个自定义宏：名字 + 可选热键 + 一串命令 Id。</summary>
public sealed record MacroDefinition(string Name, string? Hotkey, IReadOnlyList<string> Commands);

/// <summary>
/// 自定义宏文件（<c>%LOCALAPPDATA%\MathPhys.Ink\macros.txt</c>）的解析。
/// </summary>
/// <remarks>
/// <para>
/// 格式：一行一个宏，<b>记事本可编辑</b>（与 ui-state 同一套"人能读、坏了不影响启动"的哲学）：
/// <code>
/// # 井号开头是注释
/// 讲下一题=mark.next;tool.pen
/// 清屏翻页=layer.clear.draft;page.next
/// 一键红笔|Ctrl+Shift+1=ink.color.default;tool.pen
/// </code>
/// 可选的 <c>|热键</c> 段只接受 <c>Ctrl+Shift+数字</c>（1~9），其余写法整行作废并进警告列表。
/// </para>
/// <para>
/// 解析<b>绝不抛异常</b>：坏行进警告列表（宿主状态栏提示"第几条没看懂"），其余照常生效。
/// 文件不存在 = 没有自定义宏（内置模式不受影响）。
/// </para>
/// </remarks>
public static class MacroFile
{
    /// <summary>宏文件的默认路径（与 ui-state.txt 同一目录）。</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MathPhys.Ink", "macros.txt");

    /// <summary>解析结果：宏列表 + 坏行警告（人类可读，直接可上状态栏）。</summary>
    public static (IReadOnlyList<MacroDefinition> Macros, IReadOnlyList<string> Warnings) Parse(string path)
        => Parse(File.Exists(path) ? ReadAllLinesSafe(path) : Array.Empty<string>());

    /// <summary>纯解析入口（harness 用）：给什么行解析什么行，绝不碰文件系统。</summary>
    public static (IReadOnlyList<MacroDefinition> Macros, IReadOnlyList<string> Warnings) Parse(IReadOnlyList<string> lines)
    {
        var macros = new List<MacroDefinition>();
        var warnings = new List<string>();

        if (lines is null) return (macros, warnings);

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                warnings.Add($"宏文件：这一行没有「=」，看不懂就先跳过了 —— {line}");
                continue;
            }

            string head = line[..eq].Trim();
            string body = line[(eq + 1)..].Trim();

            // 头部：名称（|热键 可选）
            string name = head;
            string? hotkey = null;

            int bar = head.IndexOf('|');
            if (bar >= 0)
            {
                name = head[..bar].Trim();
                hotkey = head[(bar + 1)..].Trim();

                if (!IsValidHotkey(hotkey))
                {
                    warnings.Add($"宏「{name}」的热键写法不受支持（只认 Ctrl+Shift+数字1~9），宏保留但不绑热键。");
                    hotkey = null;
                }
            }

            if (name.Length == 0)
            {
                warnings.Add($"宏文件：这一行没有名字，跳过 —— {line}");
                continue;
            }

            var commands = body
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToList();

            if (commands.Count == 0)
            {
                warnings.Add($"宏「{name}」没有命令（=右边是空的），跳过。");
                continue;
            }

            macros.Add(new MacroDefinition(name, hotkey, commands));
        }

        return (macros, warnings);
    }

    /// <summary>只接受 Ctrl+Shift+数字1~9（宿主按键处理只在这条路上查宏，收窄范围 = 收窄误解）。</summary>
    public static bool IsValidHotkey(string? hotkey)
        => hotkey is { Length: > 0 }
           && hotkey.StartsWith("Ctrl+Shift+", StringComparison.OrdinalIgnoreCase)
           && hotkey[^1] is >= '1' and <= '9';

    private static string[] ReadAllLinesSafe(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception)
        {
            // 文件被锁 / 编码坏：当"没有宏"处理，绝不影响启动
            return Array.Empty<string>();
        }
    }
}
