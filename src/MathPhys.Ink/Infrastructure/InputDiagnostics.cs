using System.Text;
using System.Windows;
using System.Windows.Input;
using MathPhys.Ink.Input;

namespace MathPhys.Ink.Infrastructure;

/// <summary>
/// 笔 / 触摸输入链路的现场记录仪。
/// </summary>
/// <remarks>
/// 存在的唯一理由：<b>一体机上发生的事，本机一件都复现不出来</b>
/// （没有触摸屏、没有笔、没有红外屏）。策略对不对、笔画有没有被提交、
/// 输入序列有没有被截断，全靠这几行日志在事后判定。
/// <para>
/// 与"每次事件写一行"的做法不同，这里刻意<b>先累加、后汇总</b>：
/// 一次笔画会产生几十个 move 事件，逐条落盘会把真正有用的信息淹掉；
/// 而"down 与 up 数量不相等"这类信号，只有在汇总时才看得出来。
/// </para>
/// <para>
/// 首次出现的<b>设备签名</b>与<b>接触面积</b>属于一次性事实，立即记录（去重）。
/// </para>
/// </remarks>
public sealed class InputDiagnostics
{
    /// <summary>一次性事实（设备签名、接触面积）最多记多少条，防止异常情况下刷爆日志。</summary>
    private const int OnceLimit = 40;

    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- M11 笔采样（S1 / S3）
    // 刻意只累加**聚合值**而不是把每一笔存进 List：一节课能写上几千笔，
    // 而汇总里真正要看的只有"平均 / 最快 / 最慢"三个数。

    private int _sampledStrokes;
    private long _sampledPoints;
    private double _sampledSpeedMax;

    // 只把"点数 ≥ 2 且时长 > 0"的笔算进采样率与平均速度 —— 一个孤零零的墨点没有采样率可言，
    // 混进平均只会把整体拉低（见 InkSampleStats.Hertz）。
    private int _usableStrokes;
    private long _usablePoints;
    private long _usableDurationMs;
    private double _usablePathSum;
    private double _usableHertzFastest;
    private double _usableHertzSlowest;
    private int _fastestPoints;
    private int _fastestMs;
    private int _slowestPoints;
    private int _slowestMs;

    // 抬笔 → UI 线程处理完 的耗时（S3 的尺子：本机测不出书写延迟，
    // 但"UI 线程每笔要忙多久"在一体机上跑一遍就有答案）。
    private int _uiSettleCount;
    private long _uiSettleSumMs;
    private int _uiSettleMaxMs;

    // M24 热修③：笔线程过滤计数（关窗时从采样插件取一次快照并进汇总）
    private int _jumpBreaks;
    private int _badPointDrops;
    private int _foreignContacts;
    private int _foreignPackets;

    // 设备签名的缓存（笔输入每个 move 都会走 NoteStylus，而签名几乎永远不变）
    private TabletDeviceType _lastStylusType;
    private bool _lastStylusInverted;
    private string? _lastStylusName;

    /// <summary>
    /// 设备签名命中缓存的次数。
    /// </summary>
    /// <remarks>
    /// 暴露出来是为了让"每点少拼一个字符串"这件事<b>可断言</b> ——
    /// 否则优化了没有、有没有被绕过，全靠肉眼读代码。
    /// </remarks>
    public int StylusSignatureReuses { get; private set; }

    // ---- 下面几个只读视图是给验收 harness 用的：汇总文本在真机日志里看，
    //      但"聚合口径对不对"必须能在没有日志的 harness 里断言。

    /// <summary>已记录的笔数（含点数不足、不算采样率的那种）。</summary>
    public int SampledStrokeCount => _sampledStrokes;

    /// <summary>点数 ≥ 2 且时长 > 0、真正参与采样率计算与平均速度的笔数。</summary>
    public int UsableSampledStrokes => _usableStrokes;

    /// <summary>UI 收尾延迟的样本数。</summary>
    public int UiSettleSampleCount => _uiSettleCount;

    /// <summary>UI 收尾延迟的最大值（毫秒）。</summary>
    public int UiSettleMaxMs => _uiSettleMaxMs;

    /// <summary>UI 收尾延迟的平均值（毫秒）；无样本时为 0。</summary>
    public double UiSettleMeanMs => _uiSettleCount > 0 ? (double)_uiSettleSumMs / _uiSettleCount : 0.0;

    /// <summary>
    /// 启动时把系统的输入设备清单原样记下来。
    /// </summary>
    /// <remarks>
    /// 这是判定"策略该怎么写"的第一手依据：
    /// <list type="bullet">
    /// <item>清单里出现 <c>Stylus</c> ⇒ 有真正的笔数字化仪，软件能区分笔与手指；</item>
    /// <item>清单里只有 <c>Touch</c> ⇒ 红外触摸框 + 被动笔，<b>笔在系统看来就是一根手指</b>，
    /// 任何"按设备类型区分笔与手指"的策略在这台机器上都不成立。</item>
    /// </list>
    /// </remarks>
    public static void LogEnvironment()
    {
        try
        {
            var names = new List<string>();
            bool hasStylus = false;

            foreach (TabletDevice device in Tablet.TabletDevices)
            {
                names.Add($"  id={device.Id} type={device.Type} name=\"{device.Name}\"");
                if (device.Type == TabletDeviceType.Stylus) hasStylus = true;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"===== M4.2 输入设备清单（共 {names.Count} 个）=====");
            foreach (string name in names) sb.AppendLine(name);

            sb.Append(names.Count == 0
                ? "  清单为空：此刻没有枚举到任何输入设备（本机没有触摸屏、也没有插笔时属正常）。"
                  + "真正的判定要在一体机上做。"
                : hasStylus
                    ? "  判定：存在 Stylus 类型的数字化仪 ⇒ 系统能区分笔与手指。"
                    : "  判定：只有 Touch、没有 Stylus ⇒ 这台机器没有笔数字化仪，"
                      + "笔与手指在系统看来完全一样，程序无法从设备类型上区分它们。");

            AppLog.Info(sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取输入设备清单失败", ex);
        }
    }

    /// <summary>累加一次事件计数（不立即落盘）。</summary>
    public void Count(string key)
    {
        _counters.TryGetValue(key, out int current);
        _counters[key] = current + 1;
    }

    /// <summary>记录一条"只会发生一次"的事实（同 key 去重，有总量上限）。</summary>
    public void LogOnce(string key, string message)
    {
        if (_seen.Count >= OnceLimit) return;
        if (!_seen.Add(key)) return;

        AppLog.Info(message);
    }

    /// <summary>记录一次笔输入事件，并把设备签名（首次出现时）落盘。</summary>
    /// <remarks>
    /// ★ 这个方法在<b>每一次笔 move</b> 上都会跑（UI 线程），所以它自己必须是省油的灯：
    /// 签名没变时<b>既不拼字符串也不查哈希表</b>，只做一次计数（见 <see cref="StylusKey"/>）。
    /// 一体机上一次长横线就是几百个 move，省下来的是实打实的 UI 线程时间。
    /// </remarks>
    public void NoteStylus(string phase, TabletDeviceType deviceType, bool inverted,
                           string deviceName, string actionText)
    {
        Count(StylusKey(phase));

        if (deviceType == _lastStylusType
            && inverted == _lastStylusInverted
            && string.Equals(deviceName, _lastStylusName, StringComparison.Ordinal))
        {
            StylusSignatureReuses++;
            return;
        }

        _lastStylusType = deviceType;
        _lastStylusInverted = inverted;
        _lastStylusName = deviceName;

        LogOnce($"stylus.{deviceType}.{inverted}.{deviceName}",
                $"stylus: type={deviceType} inverted={inverted} name=\"{deviceName}\""
                + $" 当前工具判定={actionText}"
                + (deviceType == TabletDeviceType.Touch
                    ? "  ← 这台机器把笔也报成 Touch（红外触摸框 + 被动笔）"
                    : string.Empty));
    }

    /// <summary>把常见 phase 直接映射到固定键，避免每次插值新建字符串。</summary>
    private static string StylusKey(string phase) => phase switch
    {
        "down" => "stylus.down",
        "move" => "stylus.move",
        "up" => "stylus.up",
        _ => "stylus." + phase,
    };

    /// <summary>触摸事件的固定键（同上，手指拖动也是每帧一次）。</summary>
    private static string TouchKey(string phase) => phase switch
    {
        "down" => "touch.down",
        "up" => "touch.up",
        _ => "touch." + phase,
    };

    /// <summary>记录一次触摸事件；落笔时把<b>接触面积</b>记一次。</summary>
    /// <remarks>
    /// 接触面积是软件层做手掌拒绝的唯一可能依据（笔尖面积小、手掌面积大）。
    /// 若日志里一直是「无面积数据」，说明驱动不上报尺寸，这条路走不通 ——
    /// 那就只能接受"手指也会落墨"，靠「手」工具与一体机自带的手掌拒绝规避。
    /// </remarks>
    public void NoteTouch(string phase, int id, Size size)
    {
        Count(TouchKey(phase));

        if (phase != "down") return;

        string shape = size.IsEmpty || size.Width <= 0 || size.Height <= 0
            ? "无面积数据"
            : $"{size.Width:F1}×{size.Height:F1}";

        LogOnce($"touch.size.{shape}",
                $"touch: id={id} 接触面积={shape}"
                + (shape == "无面积数据"
                    ? "  ← 驱动不报尺寸，软件层无法做手掌拒绝"
                    : string.Empty));
    }

    /// <summary>记录一次鼠标按下/抬起（用于判断"触摸被提升成鼠标事件"是否发生）。</summary>
    public void NoteMouse(string phase, string button) => Count($"mouse.{phase}.{button}");

    /// <summary>记录一次 Manipulation 阶段。</summary>
    public void NoteManipulation(string phase) => Count($"manipulation.{phase}");

    /// <summary>记录一次笔画提交（或历史变更）。这条为 0 就说明"画的墨一笔都没提交"。</summary>
    public void NoteStrokeCollected() => Count("stroke.collected");

    /// <summary>
    /// 记录一笔的采样统计（M11 S1）。
    /// </summary>
    /// <remarks>
    /// ★ 汇总里那一行「推算采样率 ≈ XXX Hz」是 S1 <b>最值钱的产出</b>：
    /// 它回答一个此前只能猜的问题 —— <b>这台一体机把笔报成了什么、多快</b>。
    /// 有了它，"要不要开 Pointer 支持"（官方文档明确会取消实时墨迹）与
    /// "要不要降采样"这两个决策才有依据。
    /// <para>
    /// 由 UI 线程调用（笔线程通过 <c>BeginInvoke</c> 把值类型快照投递过来），
    /// 所以这里不需要锁。
    /// </para>
    /// </remarks>
    public void NoteSampledStroke(in InkSampleStats stats)
    {
        _sampledStrokes++;
        _sampledPoints += stats.PointCount;
        if (stats.MaxSpeed > _sampledSpeedMax) _sampledSpeedMax = stats.MaxSpeed;

        if (!stats.IsUsable) return;

        _usableStrokes++;
        _usablePoints += stats.PointCount;
        _usableDurationMs += stats.DurationMs;
        _usablePathSum += stats.PathLength;

        double hertz = stats.Hertz;

        if (_usableStrokes == 1 || hertz > _usableHertzFastest)
        {
            _usableHertzFastest = hertz;
            _fastestPoints = stats.PointCount;
            _fastestMs = stats.DurationMs;
        }

        if (_usableStrokes == 1 || hertz < _usableHertzSlowest)
        {
            _usableHertzSlowest = hertz;
            _slowestPoints = stats.PointCount;
            _slowestMs = stats.DurationMs;
        }
    }

    /// <summary>
    /// 记录"抬笔 → UI 线程把这一笔处理完"的耗时（M11 S3 的尺子）。
    /// </summary>
    /// <remarks>
    /// 本机没有笔、没有触摸屏，书写延迟<b>测不出来</b>。但"UI 线程每一笔要忙多久"
    /// 是一件在一体机上跑一遍就有答案的事 —— 而这个数正是"抬笔顿一下"的直接证据。
    /// 测量不改任何行为，只是把盲区变成日志里的一行。
    /// </remarks>
    public void NoteUiSettleMs(int milliseconds)
    {
        _uiSettleCount++;
        _uiSettleSumMs += milliseconds;
        if (milliseconds > _uiSettleMaxMs) _uiSettleMaxMs = milliseconds;
    }

    /// <summary>
    /// 记录 M24 笔线程过滤计数（关窗时从采样插件取一次快照）。
    /// </summary>
    public void NoteSamplerFilterCounters(int jumpBreaks, int badPointDrops, int foreignContacts, int foreignPackets)
    {
        _jumpBreaks = jumpBreaks;
        _badPointDrops = badPointDrops;
        _foreignContacts = foreignContacts;
        _foreignPackets = foreignPackets;
    }

    /// <summary>把累计的计数汇总落盘，并做交叉校验。</summary>
    public void FlushSummary()
    {
        // 事件计数为空但采到了笔（理论上不该发生）也要出汇总 ——
        // 否则"插件在工作、但事件计数被别的路径挡住"这种情形会变成一片空白。
        if (_counters.Count == 0 && _sampledStrokes == 0 && _uiSettleCount == 0) return;

        var sb = new StringBuilder("===== M4.2 输入事件汇总 =====");
        foreach (var pair in _counters.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.Append($"  {pair.Key} = {pair.Value}");
        }

        sb.AppendLine();
        sb.Append("  校验：");
        sb.Append(DescribeSkew("stylus.down", "stylus.up", "笔输入序列",
                              "落笔数远多于抬笔数 ⇒ 采集被打断过（笔画不会被提交）"));
        sb.AppendLine();
        sb.Append("        ");
        sb.Append(DescribeSkew("touch.down", "touch.up", "触摸序列",
                              "按下数远多于抬起数 ⇒ 触点表会残留，手势与按钮会失灵"));

        AppendSamplingSummary(sb);

        AppLog.Info(sb.ToString());
    }

    /// <summary>
    /// 追加「M11 笔采样」段。
    /// </summary>
    /// <remarks>
    /// 这一段是**离线判据**：一体机不在手边时，全靠它回答"这台机器把笔报成了什么、
    /// 多快、UI 线程每笔忙多久"。所以宁可多写几个数，也不要在现场才发现少了一个关键量。
    /// </remarks>
    private void AppendSamplingSummary(StringBuilder sb)
    {
        if (_sampledStrokes == 0 && _uiSettleCount == 0) return;

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("===== M11 笔采样 =====");

        if (_sampledStrokes == 0)
        {
            sb.Append("  一笔都没采到 —— 笔采样插件没有被调用。");
            sb.Append("若确实用笔写了字，先查日志里「墨迹层插件链」那一行确认插件挂上了。");
        }
        else
        {
            sb.AppendLine();
            sb.Append($"  笔画数 = {_sampledStrokes}（其中 {_usableStrokes} 笔够长、可算采样率）");
            sb.AppendLine();
            sb.Append($"  平均点数 = {(double)_sampledPoints / _sampledStrokes:F1}");

            if (_usableDurationMs > 0)
            {
                double meanMs = (double)_usableDurationMs / _usableStrokes;

                // 用 (点数-1) 而不是点数：n 个点把时长分成 n-1 段，段数除以时长才是频率
                double hertz = (_usablePoints - _usableStrokes) * 1000.0 / _usableDurationMs;

                sb.Append($"   平均时长 = {meanMs:F0} ms   推算采样率 ≈ {hertz:F0} Hz");
                sb.AppendLine();
                sb.Append($"  最快一笔 = {_fastestPoints} 点 / {_fastestMs} ms（{_usableHertzFastest:F0} Hz）"
                          + $"   最慢一笔 = {_slowestPoints} 点 / {_slowestMs} ms（{_usableHertzSlowest:F0} Hz）");
                sb.AppendLine();
                sb.Append($"  平均速度 = {_usablePathSum / _usableDurationMs:F3} world/ms"
                          + $"   最大瞬时速度 = {_sampledSpeedMax:F3} world/ms");
            }
            else
            {
                sb.Append("（没有一笔长到能算采样率：点数 ≥ 2 且时长 > 0 才计入）");
            }
        }

        if (_uiSettleCount > 0)
        {
            sb.AppendLine();
            sb.Append($"  UI 收尾延迟（抬笔 → UI 线程处理完）：平均 {_uiSettleSumMs / _uiSettleCount} ms"
                      + $" / 最大 {_uiSettleMaxMs} ms（{_uiSettleCount} 笔）");
            sb.Append("   ← 这个数偏大（> 50 ms）就是「抬笔顿一下」的直接证据");
        }

        // M24 热修③：笔线程过滤计数 —— 现场排查「异常长线」的第一手证据
        if (_jumpBreaks > 0 || _badPointDrops > 0 || _foreignContacts > 0 || _foreignPackets > 0)
        {
            sb.AppendLine();
            sb.Append($"  M24 过滤：断笔 {_jumpBreaks} 次 / 坏点丢弃 {_badPointDrops} 个"
                      + $" / 第二触点 {_foreignContacts} 个（忽略其数据包 {_foreignPackets} 个）");
        }
    }

    private string DescribeSkew(string downKey, string upKey, string label, string meaning)
    {
        int down = Get(downKey);
        int up = Get(upKey);

        if (down == 0 && up == 0) return $"{label}：没有事件";
        if (down == up) return $"{label}：down={down} up={up}（配对正常）";

        return $"{label}：down={down} up={up}（不配对）— {meaning}";
    }

    private int Get(string key) => _counters.TryGetValue(key, out int value) ? value : 0;
}
