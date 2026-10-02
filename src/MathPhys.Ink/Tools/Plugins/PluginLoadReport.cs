using System.IO;
using System.Text;

namespace MathPhys.Ink.Tools.Plugins;

/// <summary>单个插件的加载结果。</summary>
public enum PluginLoadStatus
{
    /// <summary>加载并注册成功。</summary>
    Loaded,

    /// <summary>是我们的插件，但加载/注册过程中出错（dll 损坏、缺少依赖、代码抛异常…）。</summary>
    Failed,

    /// <summary>不是（或不该是）本程序的插件：没有契约引用、契约版本不匹配、目录里没有 dll。</summary>
    Rejected,
}

/// <summary>一个插件的加载结果明细。</summary>
/// <param name="Directory">插件子目录（<c>plugins\xxx</c>）。</param>
/// <param name="AssemblyPath">实际加载的 dll；没找到时为空串。</param>
/// <param name="Status">结果。</param>
/// <param name="Reason">人能看懂的原因（成功时是"注册了几个工具"）。</param>
/// <param name="ToolCount">本次注册成功的工具数。</param>
/// <param name="RendererCount">本次登记的图形渲染器数（M7.4）。</param>
public sealed record PluginLoadEntry(
    string Directory,
    string AssemblyPath,
    PluginLoadStatus Status,
    string Reason,
    int ToolCount,
    int RendererCount = 0);

/// <summary>
/// 一次插件扫描的完整结果 —— 状态栏显示一句话，日志里留全文。
/// </summary>
/// <remarks>
/// 做成"报告对象"而不是"边加载边往界面写字"：加载发生在界面还没生成按钮的时候，
/// 此刻只有一份数据是可靠的；怎么展示（状态栏 / 日志 / 测试断言）留给调用方决定。
/// </remarks>
public sealed class PluginLoadReport
{
    private readonly List<PluginLoadEntry> _entries = new();

    /// <summary>被扫描的目录（不存在也算，便于日志说明"我们找过哪儿"）。</summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>按目录名排序后的明细。</summary>
    public IReadOnlyList<PluginLoadEntry> Entries => _entries;

    /// <summary>成功加载的插件数。</summary>
    public int LoadedCount => _entries.Count(e => e.Status == PluginLoadStatus.Loaded);

    /// <summary>本次由插件新增的工具总数。</summary>
    public int ToolCount => _entries.Sum(e => e.ToolCount);

    /// <summary>本次由插件登记的图形渲染器总数。</summary>
    public int RendererCount => _entries.Sum(e => e.RendererCount);

    /// <summary>有问题（失败或被拒）的插件数。</summary>
    public int ProblemCount => _entries.Count(e => e.Status != PluginLoadStatus.Loaded);

    /// <summary>是否有需要让用户知道的问题。</summary>
    public bool HasProblems => ProblemCount > 0;

    /// <summary>状态栏用的短句。</summary>
    public string Summary
    {
        get
        {
            if (_entries.Count == 0) return "未安装插件";

            // 图形渲染器数只在真有的时候才报：没有渲染器的里程碑里，
            // 状态栏多一句"图形 0 个"只会占地方。
            string graphics = RendererCount > 0 ? $"，图形 {RendererCount} 类" : string.Empty;

            if (!HasProblems) return $"已加载插件 {LoadedCount} 个（新增工具 {ToolCount} 个{graphics}）";

            return LoadedCount > 0
                ? $"⚠ 插件：{LoadedCount} 个已加载，{ProblemCount} 个未加载（详见日志）"
                : $"⚠ {ProblemCount} 个插件未加载（详见日志）";
        }
    }

    /// <summary>日志用的多行明细。</summary>
    public string Details
    {
        get
        {
            if (_entries.Count == 0) return $"目录 {PluginsDirectory} 里没有插件";

            var sb = new StringBuilder();
            sb.Append($"目录 {PluginsDirectory}；");

            foreach (var entry in _entries)
            {
                sb.Append(Environment.NewLine);
                sb.Append($"  [{entry.Status}] {Path.GetFileName(entry.Directory)}"
                          + $"（工具 {entry.ToolCount} 个）— {entry.Reason}");
            }

            return sb.ToString();
        }
    }

    internal void Add(PluginLoadEntry entry) => _entries.Add(entry);
}
