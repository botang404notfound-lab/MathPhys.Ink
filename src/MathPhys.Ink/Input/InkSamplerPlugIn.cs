using System.Windows;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Threading;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Views.Controls;

namespace MathPhys.Ink.Input;

/// <summary>
/// 在<b>笔输入线程</b>上采集原始笔迹、并在需要时改写采样点的 <see cref="StylusPlugIn"/>。
/// </summary>
/// <remarks>
/// <b>先把话说清楚：它不是加速器。</b>
/// <para>
/// <c>InkCanvas</c> 内部已经挂了一个 <c>DynamicRenderer</c>，而它<b>本来就是在笔线程上渲染实时墨</b>的
/// —— "墨跟着笔尖流动"这件事 WPF 早就做好了。再挂一个自定义插件<b>不会让墨流得更快</b>：
/// 它和 <c>DynamicRenderer</c> 是并列关系，不是替代关系。
/// </para>
/// <para>
/// <b>它的真实价值只有三条</b>，而这三条都无法用别的手段得到：
/// </para>
/// <list type="number">
/// <item><b>时间戳</b> —— 只有这里能拿到 <c>RawStylusInput.Timestamp</c>。
/// 而 <c>StylusPoint</c> <b>不含时间戳</b> ⇒ "速度"这个量在抬笔之后<b>无法从点集反推</b>，
/// 只能在采集时算。这是笔锋的地基。</item>
/// <item><b>抢在 <c>DynamicRenderer</c> 之前改点</b> —— <c>RawStylusInput.SetStylusPoints</c>。
/// 这是"实时可见的笔锋"唯一的实现位置（见 <see cref="SpeedPressureMap"/>）。</item>
/// <item>能在笔线程上做统计与换算，不占 UI 线程。</item>
/// </list>
///
/// <para>
/// <b>★ 坐标口径（整套设计的地基，已核实）</b>
/// </para>
/// <para>
/// <c>RawStylusInput.GetStylusPoints()</c> 返回的点在 <see cref="StylusPlugIn.Element"/> 的
/// <b>本地坐标系</b>里。这条不是推测：<c>InkCanvas</c> 内部的 <c>DynamicRenderer</c> 本身就是
/// 一个 <c>StylusPlugIn</c>，它拿同一批点直接渲染到墨迹层上、不做任何坐标变换 ——
/// 坐标系必须一致才可能成立。
/// </para>
/// <para>
/// 而我们的 <c>Element</c> 是墨迹层，它的 <c>Left/Top</c> 恒为 0、祖先里只有世界层一个矩阵
/// ⇒ <b>墨迹层的本地坐标 ≡ 世界坐标（PDF point）</b>。于是这里拿到的点可以直接当世界坐标用，
/// 不需要手写矩阵换算，也就不会出现"缩放后笔迹漂移、100% 时看不出来"这种最难查的 bug。
/// </para>
/// <para>
/// ★ 这条红利本身由 <c>InkSurfaceCanvas</c>（加载时）与 <c>CanvasViewportHost.UpdateInkSurface</c>
/// （文档变化时）在两处校验并报警。插件侧<b>不再重复校验</b>：笔线程读任何 WPF 依赖属性
/// （<c>Canvas.Left</c>、<c>ActualWidth</c>…）都会撞上线程亲和性而抛异常，那是自找麻烦。
/// </para>
///
/// <para>
/// <b>★ 线程纪律（违反任何一条都会得到极难查的故障）</b>
/// </para>
/// <list type="bullet">
/// <item><c>OnStylusDown/Move/Up</c> 跑在 <c>PenThreadWorker</c>（笔线程），<b>不是 UI 线程</b>。
/// 所以这里<b>绝对禁止</b>读笔属性、读工具表、碰 <c>Strokes</c>、动撤销栈 ——
/// 项目里那些状态（<c>InkHistory</c> / <c>BoardQuery</c> / <c>AutoSaveScheduler</c>）全部无锁。</item>
/// <item>同理<b>禁止 <c>Dispatcher.Invoke</c>（同步）</b>：笔线程等 UI 线程，而 UI 线程此刻可能
/// 正在等笔线程的事务 ⇒ RealTimeStylus 的经典死锁。回 UI 线程<b>只能用 <c>BeginInvoke</c></b>。</item>
/// <item>允许做：往私有缓冲写点、算数、置 <c>volatile</c> 标志、读 <c>Element</c> 引用本身。</item>
/// </list>
///
/// <para>
/// <b>★ 异常必须自己兜住</b>：插件里任何一个冒泡出去的异常都会在 RTI 层造成"笔画采集静默中断" ——
/// 也就是一体机上那条"墨看得见、抬笔就没"的经典现象。所以每个回调都是 try/catch 全包，
/// 失败只记一次日志（限流），绝不影响落墨。
/// </para>
/// </remarks>
public sealed class InkSamplerPlugIn : StylusPlugIn
{
    /// <summary>同一类失败最多多久报一次（毫秒），防止异常时刷爆日志。</summary>
    private const long FailureLogIntervalMs = 5000;

    private readonly InkSampleBuffer _buffer = new();

    // ------------------------------------------------------------------ 跨线程标志
    // 全部由 UI 线程写、笔线程读 ⇒ volatile。刻意用 bool / int 这种单字写，
    // 避免"笔线程读到半个对象"。真正的对象（委托）由 UI 线程一次性接好。
    private volatile bool _craftEnabled = true;
    private volatile bool _pressureEnabled;
    private volatile int _craftStrength = (int)CraftStrength.Medium;
    private volatile bool _smoothEnabled = true;                                  // M18 笔迹平滑总开关
    private volatile int _smoothStrength = (int)SmoothStrength.Light;             // M18 平滑档位（出厂 Light）
    private volatile bool _jumpDetected;                                          // M18 断点旗标（笔线程置位、UI 线程取走）

    /// <summary>一笔结束后的统计出口。<b>在 UI 线程上调用</b>（见 <see cref="Publish"/>）。</summary>
    private Action<InkSampleStats>? _sampled;

    // ------------------------------------------------------------------ 笔线程私有
    private bool _hasLastPacket;
    private double _lastPacketX;
    private double _lastPacketY;
    private int _lastPacketTimestamp;
    private double _smoothedFactor = SpeedPressureMap.MaxFactor;
    private long _lastFailureTick = long.MinValue;

    // M18 断点检测：必须看【原始点】—— 平滑会把跳变抹开、让检测失效，所以独立于 _lastPacket*
    private bool _hasRawLast;
    private double _rawLastX;
    private double _rawLastY;
    private int _rawLastTimestamp;

    // M18 平滑状态：指数低通的锚（_smX/_smY）+ 本笔首点（起笔甩尾抑制基准）
    private bool _smInit;
    private double _smX;
    private double _smY;
    private double _strokeDownX;
    private double _strokeDownY;
    private bool _isFirstMoveOfStroke;

    // M24 热修②：单触点书写 —— 全部回调都跑在笔线程 ⇒ 普通字段即可（无跨线程竞争）
    /// <summary>当前主触点 Id（-1 = 无主触点；-2 = 无设备对象的合成输入）。</summary>
    private int _primaryDeviceId = -1;
    /// <summary>主触点最后一次包（Down/Move/Up）的时间戳，用于「Up 丢失让位」判定。</summary>
    private int _primaryLastSeenTs;
    /// <summary>主触点闲置多久后新触点可接管（毫秒）：只兜「Up 丢失」，正常抬笔走 finally 释放。</summary>
    private const int PrimaryIdleReclaimMs = 500;

    // M24 热修③：诊断计数（笔线程 Interlocked 累加；关窗时 UI 线程取一次快照进汇总）
    private int _jumpBreaks;
    private int _badPointDrops;
    private int _foreignContacts;
    private int _foreignPackets;

    /// <summary>
    /// 是否采集（= 基类的 <c>StylusPlugIn.Enabled</c>）。
    /// </summary>
    /// <remarks>
    /// ★ 用基类这个开关而不是自己造一个：<c>Enabled = false</c> 时 <b>WPF 根本不会调用本插件的回调</b>，
    /// 于是"关掉采样"是真正的零开销，而不是"回调里早期返回"。这是官方机制，比自己判标志可靠。
    /// </remarks>
    public bool SamplerEnabled
    {
        get => Enabled;
        set => Enabled = value;
    }

    /// <summary>
    /// 是否把"速度"伪装成压感写回采样点（笔锋总开关）。
    /// </summary>
    /// <remarks>
    /// 关闭时插件<b>只采集、不改点</b> —— 等价于"没有笔锋"，落墨结果与 M10 完全一致。
    /// </remarks>
    public bool CraftEnabled
    {
        get => _craftEnabled;
        set => _craftEnabled = value;
    }

    /// <summary>设备原始压力是否也参与合成（对应界面上的「压感」复选框）。</summary>
    public bool PressureEnabled
    {
        get => _pressureEnabled;
        set => _pressureEnabled = value;
    }

    /// <summary>笔锋强度档位（见 <see cref="SpeedPressureMap"/>）。</summary>
    public CraftStrength CraftLevel
    {
        get => SpeedPressureMap.FromIndex(_craftStrength);
        set => _craftStrength = (int)value;
    }

    /// <summary>是否对采样点做保点数平滑（M18；关 = 原样落墨，与 M17 一致）。</summary>
    public bool SmoothEnabled
    {
        get => _smoothEnabled;
        set => _smoothEnabled = value;
    }

    /// <summary>平滑强度档位（Off / Light / Medium，见 <see cref="SmoothStrength"/>）。</summary>
    public SmoothStrength SmoothLevel
    {
        get
        {
            if (!_smoothEnabled) return SmoothStrength.Off;
            return (SmoothStrength)_smoothStrength;
        }
        set
        {
            _smoothEnabled = value != SmoothStrength.Off;
            _smoothStrength = (int)value;
        }
    }

    /// <summary>
    /// UI 线程取走"本笔检测到断点"旗标（M18）。
    /// </summary>
    /// <remarks>
    /// 笔线程检测到手掌并入/Down 丢失后置位；UI 线程在 <c>StrokeCollected</c> 里取走，
    /// 命中就把这整笔从 Strokes 移除 —— 断点混入的点列不进存档、不进撤销。
    /// volatile 单字读写，与既有跨线程标志同一套纪律。
    /// </remarks>
    public bool ConsumeJumpDetected()
    {
        bool value = _jumpDetected;
        _jumpDetected = false;
        return value;
    }

    /// <summary>接上统计出口（UI 线程调用）。</summary>
    public void AttachSampledSink(Action<InkSampleStats> sink) => _sampled = sink;

    /// <summary>把当前档位/开关写成一行（切换时由宿主记日志）。</summary>
    public string DescribeSettings()
    {
        string smooth = SmoothLevel switch
        {
            SmoothStrength.Medium => "中",
            SmoothStrength.Light => "轻",
            _ => "关",
        };

        return $"采样={(Enabled ? "开" : "关")} 笔锋={(CraftEnabled ? SpeedPressureMap.Name(CraftLevel) : "关")}"
               + $" 压感合成={(PressureEnabled ? "开" : "关")} 平滑={smooth}";
    }

    // ================================================================== RTI 回调（笔线程）

    /// <inheritdoc />
    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        try
        {
            if (!Enabled) return;

            // ---- M24 热修②：单触点书写 ----
            // 红外框偶发多触点（手掌边沿 / 双触误报）：只认先落下的触点，
            // 其余触点在主触点抬起前一律忽略（湿墨压成钉在笔尖的单点）。
            // ★ 例外：主触点长期无消息却来了新触点 ⇒ 主触点的 Up 八成丢了
            //   （现场日志实锤 down/up 不配对），让位给新触点 —— 否则此后所有
            //   触点都会被当成「第二触点」，表现为永远写不出字。
            //   代价：笔按住不动超 500ms 时来的新触点会接管 —— 现场可自愈（再落一次笔）。
            int deviceId = DeviceIdOf(rawStylusInput);
            if (_primaryDeviceId != -1 && deviceId != _primaryDeviceId)
            {
                if (rawStylusInput.Timestamp - _primaryLastSeenTs <= PrimaryIdleReclaimMs)
                {
                    Interlocked.Increment(ref _foreignContacts);
                    SuppressForeignContact(rawStylusInput);
                    return;
                }
                _primaryDeviceId = -1;   // 让位
            }
            _primaryDeviceId = deviceId;
            _primaryLastSeenTs = rawStylusInput.Timestamp;

            var points = rawStylusInput.GetStylusPoints();
            if (points.Count == 0) return;

            // ---- M24 热修③：Up 丢失防御 ----
            // 缓冲里还挂着上一笔就来了新 Down ⇒ 上一笔的 Up 丢了：旧湿墨再也无法
            // 正常提交，清掉残留（否则一直挂在屏上，直到下一次工具切换才被 ResetActiveRenderer 兜走）。
            if (_buffer.IsActive) TryResetRenderer();

            // M18 状态复位（自旧版上移到此处）：无论本包能否找到完好起点，
            // 旧笔的状态一概不带入 —— 否则「全坏落笔」路径会让旧锚/旧平滑锚漏进下一笔
            _smoothedFactor = SpeedPressureMap.MaxFactor;
            _hasLastPacket = false;
            _jumpDetected = false;
            _hasRawLast = false;
            _smInit = false;
            _isFirstMoveOfStroke = true;

            // M24 热修①：落笔包里的坏点（NaN/∞/(0,0)）不当作起点 —— 否则湿墨从坏点起笔，
            // 后续完好点会把「坏点 → 笔尖」连成一条斜线。取第一个完好点做本笔起点。
            int startIdx = -1;
            for (int i = 0; i < points.Count; i++)
            {
                if (!PacketPointFilter.IsBadPoint(points[i].X, points[i].Y))
                {
                    startIdx = i;
                    break;
                }
            }

            // 全坏：本包不当作落笔（没有任何有效起点）。湿墨不写回 —— 原始坏点最多
            // 让 DynamicRenderer 画出一个不可见的坏点、随后被 Reset 清掉；本笔直接按
            // 「断笔」处理：置旗标让 UI 层整笔丢弃（起点都没有的笔不可能是好笔）。
            if (startIdx < 0)
            {
                _jumpDetected = true;
                LogJumpOnce();
                TryResetRenderer();
                return;
            }

            StylusPoint first = points[startIdx];
            _buffer.Begin(new RawSample(first.X, first.Y, rawStylusInput.Timestamp, first.PressureFactor));

            // M18：平滑锚定在本笔首点（首点不被平滑，防起笔漂移）+ 记首点做甩尾抑制基准
            _smX = first.X;
            _smY = first.Y;
            _smInit = true;
            _strokeDownX = first.X;
            _strokeDownY = first.Y;

            // ★ M24 热修④（锚点补齐，2026-09-28）：落笔点必须登记成「原始锚」。
            //   漏掉这一句的后果极隐蔽：本笔第一个 Move 包的首点会以「无锚」身份过检
            //   （PacketPointFilter 对无锚点直接判完好），而「落笔之后马上搭上手掌」正是最常见的
            //   并入时机 —— 那一下瞬移就成了唯一没人查的跳变：湿墨从落笔点连到手掌，长线画出来、
            //   断笔旗标却还是 false，整笔照旧提交（这就是「修过仍有线」的残留口子之一）。
            _rawLastX = first.X;
            _rawLastY = first.Y;
            _rawLastTimestamp = rawStylusInput.Timestamp;
            _hasRawLast = true;

            // M24 热修①：起点之前混着坏点时，写回「只含完好点」的点集，
            // 让 DynamicRenderer 从完好点起笔（坏点不进湿墨、不进提交笔画）。
            // ★ M24 热修④：落笔包内部也要逐点查跳变（包内共享时间戳 ⇒ 判定退化为距离单条件）——
            //   落笔与手掌被合进同一包时，直线同样没机会上屏（截断到跳变前的完好前缀）。
            int downEnd = points.Count;
            double downPrevX = first.X;
            double downPrevY = first.Y;
            for (int i = startIdx + 1; i < points.Count; i++)
            {
                if (PacketPointFilter.IsBadPoint(points[i].X, points[i].Y)) continue;

                if (JumpDetector.IsJump(downPrevX, downPrevY, rawStylusInput.Timestamp,
                                        points[i].X, points[i].Y, rawStylusInput.Timestamp))
                {
                    downEnd = i;
                    break;
                }

                downPrevX = points[i].X;
                downPrevY = points[i].Y;
            }

            bool downJumped = downEnd < points.Count;
            if (downJumped)
            {
                // 与 Move 跳变同一口径：整笔作废（置旗标 + 清锚 + 清实时湿墨）
                OnBreakDetected(rawStylusInput);
            }

            if (startIdx > 0 || downJumped)
            {
                var kept = new StylusPointCollection(points.Description);
                for (int i = startIdx; i < downEnd; i++)
                {
                    if (!PacketPointFilter.IsBadPoint(points[i].X, points[i].Y)) kept.Add(points[i]);
                }
                WriteBackSet(rawStylusInput, kept);
            }
        }
        catch (Exception ex)
        {
            Fail("down", ex);
        }
    }

    /// <inheritdoc />
    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        try
        {
            if (!Enabled) return;

            // ---- M24 热修②：非主触点的移动一概忽略（湿墨压成笔尖单点，不采集）----
            int deviceId = DeviceIdOf(rawStylusInput);
            if (_primaryDeviceId != -1 && deviceId != _primaryDeviceId)
            {
                Interlocked.Increment(ref _foreignPackets);
                SuppressForeignContact(rawStylusInput);
                return;
            }
            if (_primaryDeviceId == -1)
            {
                // Down 丢失后的 Move 续画：收编为主触点（笔迹本身由 M24①/M18 的跳变检测兜底）
                _primaryDeviceId = deviceId;
            }
            _primaryLastSeenTs = rawStylusInput.Timestamp;

            var points = rawStylusInput.GetStylusPoints();
            if (points.Count == 0) return;

            // ⓪ M24 热修①：逐点分级（M18 边界检测的强化 —— 包内点与包边界点一视同仁）。
            //    分级先于平滑：平滑会把跳变抹开、让检测失效，所以只看原始点。
            //    完好点的原始锚逐点推进（包内 dt=0 ⇒ 跳变退化为距离单条件，见 PacketPointFilter）；
            //    本包首点对照上一包末完好点，M18 的「包边界」判定被天然覆盖，不是并列的两套逻辑。
            ScanPoints(points, rawStylusInput.Timestamp, out int jumpIndex, out List<int>? goodIdx);

            // ⓪′ 跳变：与 M18 同口径整包不采集；额外把写回<b>截断到跳变前的完好前缀</b> ——
            //    DynamicRenderer 连那条直线的机会都没有（M18 只靠异步 Reset 兜底，
            //    Reset 投递慢一两帧的间隙里直线照样上屏，这正是「修过仍有线」的残留口子）。
            if (jumpIndex >= 0)
            {
                // ★ M24 热修⑤：先置旗标 + 清锚 + 请求清实时湿墨，再做截断写回 ——
                //   写回万一失败（描述不兼容、点集异常）也不会把「本笔作废」一起跳过；
                //   顺序反了的话长线既画出来又提交下去，等于这次检测白做。
                OnBreakDetected(rawStylusInput);

                var prefix = goodIdx is null
                    ? SlicePrefix(points, jumpIndex)
                    : BuildKept(points, goodIdx);
                WriteBackPrefix(rawStylusInput, points, prefix);
                return;
            }

            // ⓪″ 坏点剔除：重组出「只含完好点」的点集参与后续平滑/采集/写回；
            //    全坏则整包作废（湿墨钉在锚点 —— 不喂原始坏点，否则连出线）。
            StylusPointCollection effective = points;
            if (goodIdx is not null)
            {
                if (goodIdx.Count == 0)
                {
                    if (_hasRawLast || _buffer.IsActive)
                    {
                        double px = _hasRawLast ? _rawLastX : _strokeDownX;
                        double py = _hasRawLast ? _rawLastY : _strokeDownY;
                        PinWet(rawStylusInput, points, px, py);
                    }
                    return;
                }
                effective = BuildKept(points, goodIdx);
            }

            // ① M18 平滑（保点数，只动 X/Y —— PressureFactor 原样保留，笔锋不磨平）
            ApplySmooth(rawStylusInput, effective);

            // ② 采集（永不因为后面的写回失败而丢）
            for (int i = 0; i < effective.Count; i++)
            {
                StylusPoint p = effective[i];
                _buffer.Add(new RawSample(p.X, p.Y, rawStylusInput.Timestamp, p.PressureFactor));
            }

            // ③ 写回：笔锋开着由 ApplyCraft 统一写回（吃到的已是平滑点）；
            //    笔锋关而平滑开时，平滑点也要自己写回 —— 否则实时与提交都还是原始点，平滑等于没做；
            //    坏点被剔除时同样要写回 —— 否则剔除等于白剔（坏点直通 DynamicRenderer）
            if (_craftEnabled)
            {
                ApplyCraft(rawStylusInput, effective);
            }
            else if (_smoothEnabled || goodIdx is not null)
            {
                try
                {
                    rawStylusInput.SetStylusPoints(effective);
                }
                catch (Exception ex2)
                {
                    Fail("smooth-writeback", ex2);
                }
            }

            _isFirstMoveOfStroke = false;
        }
        catch (Exception ex)
        {
            Fail("move", ex);
        }
    }

    /// <inheritdoc />
    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        // M24 热修②：设备 Id 在 try 外取好 —— finally 里的主触点释放要用
        int deviceId = DeviceIdOf(rawStylusInput);
        try
        {
            if (!Enabled) return;

            // ---- M24 热修②：非主触点的抬笔与主笔画无关 ----
            // 湿墨仍要压成单点：不写回的话原始抬笔点直通 DynamicRenderer，
            // 会从钉点连出一条线来。
            if (_primaryDeviceId != -1 && deviceId != _primaryDeviceId)
            {
                Interlocked.Increment(ref _foreignPackets);
                SuppressForeignContact(rawStylusInput);
                return;
            }
            if (_primaryDeviceId == -1)
            {
                // Down、Move 全丢、只有 Up 能来的极端情形：收编再正常收尾
                _primaryDeviceId = deviceId;
            }
            _primaryLastSeenTs = rawStylusInput.Timestamp;

            var points = rawStylusInput.GetStylusPoints();
            if (points.Count == 0)
            {
                Publish(_buffer.End());
                return;
            }

            // M24 热修①：抬笔包同样逐点分级 —— 跳变也可能恰好落在最后一包。
            ScanPoints(points, rawStylusInput.Timestamp, out int jumpIndex, out List<int>? goodIdx);

            // 跳变 ⇒ 本笔整笔作废（M18 口径）：不采集、不发布统计、缓冲整体作废；
            // 额外把写回截断到完好前缀，让「最后一包里的直线」也画不出来。
            if (jumpIndex >= 0)
            {
                // ★ M24 热修⑤：同上 —— 整笔作废先落地（旗标/清锚/清缓冲/清湿墨），
                //   截断写回排在后面，任何一个写回失败都不再牵连「本笔作废」。
                _jumpDetected = true;
                _hasRawLast = false;
                _buffer.Reset();
                TryResetRenderer();

                var prefix = goodIdx is null
                    ? SlicePrefix(points, jumpIndex)
                    : BuildKept(points, goodIdx);
                WriteBackPrefix(rawStylusInput, points, prefix);
                return;
            }

            // M24 热修①：坏点剔除 —— 抬笔包只收完好点，写回剔除后的点集
            //（否则坏点混进湿墨终点与提交笔画）。
            if (goodIdx is not null)
            {
                if (goodIdx.Count > 0)
                {
                    var kept = BuildKept(points, goodIdx);
                    for (int i = 0; i < kept.Count; i++)
                    {
                        StylusPoint p = kept[i];
                        _buffer.Add(new RawSample(p.X, p.Y, rawStylusInput.Timestamp, p.PressureFactor));
                    }
                    WriteBackSet(rawStylusInput, kept);
                }
                else if (_hasRawLast || _buffer.IsActive)
                {
                    // 全坏抬笔包：湿墨钉在锚点（不喂原始坏点，否则连出线），缓冲照常收尾
                    double px = _hasRawLast ? _rawLastX : _strokeDownX;
                    double py = _hasRawLast ? _rawLastY : _strokeDownY;
                    PinWet(rawStylusInput, points, px, py);
                }

                // 全坏：整包不采集，缓冲按原样收尾
                Publish(_buffer.End());
                return;
            }

            for (int i = 0; i < points.Count; i++)
            {
                StylusPoint p = points[i];
                _buffer.Add(new RawSample(p.X, p.Y, rawStylusInput.Timestamp, p.PressureFactor));
            }

            Publish(_buffer.End());
        }
        catch (Exception ex)
        {
            Fail("up", ex);
            _buffer.Reset();
        }
        finally
        {
            // ---- M24 热修②/③：主触点抬笔后无条件释放（成败都一样）----
            // 不释放的话，一次 Up 丢失/一场异常就让后续所有触点被当成「第二触点」；
            // 兜底回收（PrimaryIdleReclaimMs）只是让位策略，正常路径必须在这里干净归还。
            if (_primaryDeviceId == -1 || deviceId == _primaryDeviceId)
            {
                _primaryDeviceId = -1;
            }
        }
    }

    // ================================================================== M18 断点与平滑

    /// <summary>
    /// 断点命中处理（笔线程）：置旗标 + 清锚 + 清实时湿墨。
    /// </summary>
    /// <remarks>
    /// 整包丢弃（return 不采集不写回）而不是截断续画 —— 保点数红线（docs/16 S5）+ 不造"半笔"几何；
    /// UI 层 <c>StrokeCollected</c> 里再按旗标把整笔移除兜底。
    /// </remarks>
    private void OnBreakDetected(RawStylusInput rawStylusInput)
    {
        _jumpDetected = true;
        _hasRawLast = false;      // 下一包重新锚定（跳变后的新位置才是本笔续点）
        _hasLastPacket = false;   // 笔锋速度不跨跳变计算
        _smInit = false;          // 平滑锚同样重定

        LogJumpOnce();

        // 清 DynamicRenderer 的临时湿墨：跳变前已画的半截实时墨要收掉，
        // 否则抬笔前屏幕上仍挂着那截。★ Reset 只能在 UI 线程调（官方示例即 UI 线程；
        // 且其内部要读鼠标/笔的下落状态，笔线程读会撞线程亲和性）—— 用 BeginInvoke 投回，
        // 这是本插件唯一允许的回 UI 线程方式（Invoke 会 RTI 死锁）。
        TryResetRenderer();
    }

    /// <summary>断点日志限流：同一分钟只记一条，防手掌反复蹭框刷爆日志。</summary>
    private void LogJumpOnce()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastFailureTick < FailureLogIntervalMs) return;
            _lastFailureTick = now;

            AppLog.Warn("笔迹断点：检测到手掌并入/Down 丢失（空间瞬移），已丢弃当前笔并重置实时渲染。");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    // ================================================================== M24 热修①：包内逐点过滤

    /// <summary>
    /// 逐点分级：跳变扫描 + 坏点登记（笔线程，先于平滑 —— 平滑会把跳变抹开）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 出参约定：<c>jumpIndex</c> = 首个跳变点下标（无则 -1）；<c>goodIdx</c> = 完好点下标表，
    /// <b>null 表示「全部完好」</b>（常态零分配）。跳变点与它之后的点一概不进下标表 ——
    /// 整笔将被 UI 层丢弃，锚随 <see cref="OnBreakDetected"/> 重置。
    /// </para>
    /// <para>
    /// ★ 完好点的原始锚（<c>_rawLast*</c>）在这里<b>逐点</b>推进：本包首点对照上一包末完好点，
    /// M18 的「包边界」判定被天然覆盖；包内点间 dt=0，跳变退化为距离单条件（同包共享时间戳）。
    /// </para>
    /// </remarks>
    private void ScanPoints(StylusPointCollection points, int timestamp, out int jumpIndex, out List<int>? goodIdx)
    {
        jumpIndex = -1;
        goodIdx = null;

        for (int i = 0; i < points.Count; i++)
        {
            StylusPoint p = points[i];

            var verdict = PacketPointFilter.Classify(
                _hasRawLast, _rawLastX, _rawLastY, _rawLastTimestamp,
                p.X, p.Y, timestamp);

            if (verdict == PacketPointVerdict.BadPoint)
            {
                Interlocked.Increment(ref _badPointDrops);   // M24③ 诊断计数

                // 首个坏点出现才建表，并回填它之前的完好下标
                if (goodIdx is null)
                {
                    goodIdx = new List<int>(points.Count);
                    for (int j = 0; j < i; j++) goodIdx.Add(j);
                }
                continue;
            }

            if (verdict == PacketPointVerdict.Jump)
            {
                Interlocked.Increment(ref _jumpBreaks);      // M24③ 诊断计数
                jumpIndex = i;
                return;   // 锚停在最后一个完好点上（跳变点绝不作锚）
            }

            goodIdx?.Add(i);
            _rawLastX = p.X;
            _rawLastY = p.Y;
            _rawLastTimestamp = timestamp;
            _hasRawLast = true;
        }
    }

    /// <summary>按完好下标重组点集（笔线程；剔除/截断共用）。</summary>
    private StylusPointCollection BuildKept(StylusPointCollection points, List<int> goodIdx)
    {
        var kept = new StylusPointCollection(points.Description);
        foreach (int i in goodIdx) kept.Add(points[i]);
        return kept;
    }

    /// <summary>
    /// 写回过滤后的点集（坏点剔除 / 跳变截断共用；<b>空集也照写</b> —— 湿墨整段不画）。
    /// </summary>
    /// <remarks>
    /// ★ 这是「看得见的直线」的直接闸门：DynamicRenderer 拿到的点集就是我们写回的点集，
    /// 截断后直线根本没有原料可画 —— 不再依赖异步 Reset 抢在渲染之前。
    /// 写回失败只记日志（限流），退化为 M18 行为（Reset 兜底），绝不影响落墨。
    /// </remarks>
    private void WriteBackSet(RawStylusInput rawStylusInput, StylusPointCollection kept)
    {
        try
        {
            rawStylusInput.SetStylusPoints(kept);
        }
        catch (Exception ex)
        {
            Fail("filter-writeback", ex);
        }
    }

    /// <summary>
    /// 取触点 Id（笔线程）。官方多触点示例（GestureRecognizerPlugin）即用
    /// <c>RawStylusInput.StylusDeviceId</c> 区分触点 —— plain int，无线程亲和问题。
    /// </summary>
    private static int DeviceIdOf(RawStylusInput rawStylusInput)
        => rawStylusInput.StylusDeviceId;

    /// <summary>
    /// 把湿墨压成「钉在 (x,y) 的单点」—— SetStylusPoints 不收空集的替代闸门。
    /// </summary>
    /// <remarks>
    /// ★ 官方 <c>SetStylusPoints</c> 对空点集直接抛 ArgumentException
    /// （Stylus_StylusPointsCantBeEmpty）——「写回空集让湿墨无处可画」行不通。
    /// 改压成单点：钉点位置取「湿墨当前所在处」时增量段零长度 ⇒ 画不出新东西；
    /// 钉点坐标非法（NaN）时整段几何渲染为空 ⇒ 同样画不出东西。两种都是「没有直线」。
    /// <para>
    /// ★★ M24 热修⑤（描述兼容，2026-09-28）：钉点<b>必须从模板里取一个现成的点改坐标</b>，
    /// 不能 <c>new StylusPoint(x,y)</c>。真实数位板 / 触控框的 <c>StylusPointDescription</c>
    /// 常带额外包属性（X/Y/NormalPressure 之外还有 Width/Height、按钮、状态位…），而
    /// <c>new StylusPoint(x,y)</c> 用的是<b>默认三属性描述</b> —— 两者不兼容时
    /// <c>StylusPointCollection.Add</c> 直接抛 <c>ArgumentException</c>（描述不兼容）。
    /// 后果是被 <see cref="Fail"/> 静默吞掉：钉点没发生 ⇒ 原始坏点/跳变点直通
    /// <c>DynamicRenderer</c>，长线照画；更糟的是调用点里写回排在置旗标之前，
    /// 异常会把「本笔作废」一起跳过。取模板点则描述必然一致（模板就是同一包的
    /// <c>GetStylusPoints()</c>），这个失败模式被彻底消掉。
    /// </para>
    /// </remarks>
    private void PinWet(RawStylusInput rawStylusInput, StylusPointCollection template, double x, double y)
    {
        // 模板为空 ⇒ 这一包本来就没有点可写回，直接放弃（不构造空集，那会抛异常）
        if (template.Count == 0) return;

        var pin = new StylusPointCollection(template.Description);

        // 借模板首点（描述、包属性、包格式全同）⇒ 只改坐标，绝不新建默认描述的点
        StylusPoint pinned = template[0];
        pinned.X = x;
        pinned.Y = y;
        pin.Add(pinned);

        WriteBackSet(rawStylusInput, pin);
    }

    /// <summary>
    /// 抑制非主触点：把该触点的湿墨钉成单点。
    /// </summary>
    /// <remarks>
    /// 主触点有有效位置就钉在笔尖（与真笔迹重叠，肉眼不可见）；
    /// 否则退钉在本笔起点。DynamicRenderer 的增量连线在零长度段上不生长
    /// ⇒ 手掌拖动拖不出轨迹；提交笔画也只是一个点（OnStrokeCollected 口径照常走）。
    /// </remarks>
    private void SuppressForeignContact(RawStylusInput rawStylusInput)
    {
        var points = rawStylusInput.GetStylusPoints();
        if (points.Count == 0) return;

        double x = _hasRawLast ? _rawLastX : _strokeDownX;
        double y = _hasRawLast ? _rawLastY : _strokeDownY;
        if (double.IsFinite(x) && double.IsFinite(y))
        {
            PinWet(rawStylusInput, points, x, y);
        }
    }

    /// <summary>按下标切前缀（跳变点之前的部分；笔线程）。</summary>
    private static StylusPointCollection SlicePrefix(StylusPointCollection points, int exclusiveEnd)
    {
        var prefix = new StylusPointCollection(points.Description);
        for (int i = 0; i < exclusiveEnd; i++) prefix.Add(points[i]);
        return prefix;
    }

    /// <summary>
    /// 写回截断/剔除后的前缀；空前缀退化为「钉在锚点的单点」。
    /// </summary>
    /// <remarks>
    /// 前缀为空 = 跳变落在本包首点 —— 此时必有锚（跳变判定依赖锚），
    /// 锚点即湿墨当前位置，钉上去增量段零长度，直线画不出来。
    /// </remarks>
    private void WriteBackPrefix(RawStylusInput rawStylusInput, StylusPointCollection template, StylusPointCollection prefix)
    {
        if (prefix.Count > 0)
        {
            WriteBackSet(rawStylusInput, prefix);
            return;
        }

        PinWet(rawStylusInput, template, _rawLastX, _rawLastY);
    }

    /// <summary>
    /// M24 过滤计数快照（关窗时 UI 线程取一次，并进输入事件汇总）。
    /// </summary>
    /// <remarks>
    /// 诊断读法：断笔次数 ≈ 手掌并入/Down 丢失被拦下的次数；坏点丢弃与第二触点
    /// 长期不为 0，说明红外框工作不干净（软件已兜住，但值得留意硬件状态）。
    /// </remarks>
    public (int JumpBreaks, int BadPointDrops, int ForeignContacts, int ForeignPackets)
        GetFilterCounters()
        => (_jumpBreaks, _badPointDrops, _foreignContacts, _foreignPackets);

    /// <summary>
    /// 清实时湿墨（UI 线程执行，笔线程用 BeginInvoke 投递）。
    /// </summary>
    /// <remarks>
    /// <see cref="DynamicRenderer.Reset"/> 的官方签名是 (StylusDevice, StylusPointCollection)，
    /// 语义是"用给定点集<b>重画</b>当前湿墨" —— 传<b>空点集</b>即整截清掉；
    /// 文档示例为 UI 线程调用，且其内部要读笔/鼠标下落状态（线程亲和），
    /// 所以这里投回 UI 线程执行。投递后哪怕慢一两帧，直线也不再继续生长。
    /// </remarks>
    private void TryResetRenderer()
    {
        try
        {
            if (Element is not DispatcherObject dispatcherObject) return;

            var element = Element;   // 笔线程只读引用本身（既有纪律），闭包里只摸引用

            dispatcherObject.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    try
                    {
                        if (element is InkSurfaceCanvas ink) ink.ResetDynamicRenderer();
                    }
                    catch (Exception ex)
                    {
                        Fail("reset", ex);
                    }
                }));
        }
        catch (Exception ex)
        {
            Fail("reset", ex);
        }
    }

    /// <summary>
    /// 保点数指数平滑（笔线程，写回前调用）：逐点推进 X/Y 锚，压力因子不动。
    /// </summary>
    /// <remarks>
    /// 链序刻意安排为 断点检测 → 平滑 → 采集 → 笔锋：
    /// 采集吃【平滑后】的点 ⇒ 统计口径 = 存档口径 = 实时口径，三者永远一致；
    /// 笔锋的速度基于平滑点计算 ⇒ 宽度信号更稳。
    /// Off 档 α=1.0 是恒等映射，无需分支。
    /// </remarks>
    private void ApplySmooth(RawStylusInput rawStylusInput, StylusPointCollection points)
    {
        double alpha = PathSmoother.Alpha((SmoothStrength)_smoothStrength);
        if (!_smoothEnabled) alpha = 1.0;

        bool suppressJitter = _isFirstMoveOfStroke && _smInit;

        for (int i = 0; i < points.Count; i++)
        {
            StylusPoint sp = points[i];

            // M18 起笔甩尾抑制：首个 Move 包内、贴着落笔点抖的小点直接拉回与首点重合
            //（红外框起笔常见"顿一下甩出去"的小尾巴，保点数、只重合）
            if (suppressJitter && PathSmoother.IsWithinJitter(_strokeDownX, _strokeDownY, sp.X, sp.Y))
            {
                sp.X = _strokeDownX;
                sp.Y = _strokeDownY;
                points[i] = sp;
                continue;
            }

            // 指数低通：s_i = s_{i-1} + α·(p_i − s_{i-1})；锚无效时直接取当前点（首点锚定）
            if (_smInit)
            {
                _smX = PathSmoother.Next(_smX, sp.X, alpha);
                _smY = PathSmoother.Next(_smY, sp.Y, alpha);
            }
            else
            {
                _smX = sp.X;
                _smY = sp.Y;
                _smInit = true;
            }

            sp.X = _smX;
            sp.Y = _smY;
            points[i] = sp;
        }
    }

    // ================================================================== 笔锋（S2）

    /// <summary>
    /// 把速度映射成压力因子写回采样点。
    /// </summary>
    /// <remarks>
    /// 写的是<b>标准属性</b> <c>StylusPoint.PressureFactor</c>，所以实时墨迹与最终笔画用的是
    /// 同一批因子，而 ISF 序列化天然带上它 ⇒ <c>.tbink</c> / <c>.twb</c> 一行不用改。
    /// <para>
    /// ★ 写法照官方示例（<c>GetStylusPoints</c> 的文档示例）：取出 <c>StylusPoint</c>（它是 struct）
    /// → 改属性 → 用索引器放回 → <c>SetStylusPoints</c> 写回。
    /// </para>
    /// <para>
    /// ★ 一个 packet 内的多个点<b>共用同一个速度</b>：<c>Timestamp</c> 是"这一个包的采集时刻"，
    /// 同包内两点的时间差恒为 0，逐点算速度会得到 <c>Infinity</c>。
    /// 所以速度按<b>包间</b>算（用代表点），包内所有点用同一个值。
    /// </para>
    /// </remarks>
    private void ApplyCraft(RawStylusInput rawStylusInput, StylusPointCollection points)
    {
        try
        {
            StylusPoint representative = points[points.Count - 1];

            double speed = 0;
            if (_hasLastPacket)
            {
                double dx = representative.X - _lastPacketX;
                double dy = representative.Y - _lastPacketY;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                int dt = rawStylusInput.Timestamp - _lastPacketTimestamp;
                speed = distance / (dt > 0 ? dt : 1);
            }

            _lastPacketX = representative.X;
            _lastPacketY = representative.Y;
            _lastPacketTimestamp = rawStylusInput.Timestamp;
            _hasLastPacket = true;

            var level = SpeedPressureMap.FromIndex(_craftStrength);

            // 低通：单点速度抖动很大，直接用会让笔迹像锯齿
            _smoothedFactor = SpeedPressureMap.Smooth(_smoothedFactor, SpeedPressureMap.Width01(speed, level));

            float factor = (float)_smoothedFactor;
            if (_pressureEnabled && representative.PressureFactor > 0)
            {
                factor = (float)SpeedPressureMap.Blend(factor, representative.PressureFactor);
            }

            for (int i = 0; i < points.Count; i++)
            {
                StylusPoint sp = points[i];
                sp.PressureFactor = factor;
                points[i] = sp;
            }

            // ★ 必须写回：不写回的话，改的只是我们手里这份副本，
            //   DynamicRenderer 拿到的还是原始点 ⇒ 实时墨迹没有笔锋（而抬笔后的笔画有）——
            //   表现是"墨迹在抬笔瞬间跳一下"，正是约束 B 要避免的情形。
            rawStylusInput.SetStylusPoints(points);
        }
        catch (Exception ex)
        {
            // 写回失败只影响"这一包没有笔锋"，采集与落墨都照常。
            Fail("craft", ex);
        }
    }

    // ================================================================== 投递

    /// <summary>把一笔统计投递回 UI 线程。</summary>
    /// <remarks>
    /// ★ 只用 <c>BeginInvoke</c>（异步）：<c>Invoke</c> 会让笔线程等 UI 线程，
    /// 而 UI 线程此刻可能正在等笔线程的事务 ⇒ RTI 死锁。
    /// </remarks>
    private void Publish(InkSampleStats stats)
    {
        var sink = _sampled;
        if (sink is null) return;

        if (Element is not DispatcherObject dispatcherObject)
        {
            // 没有 Dispatcher（未挂到 UI 树上）时同步调用：这是测试路径，冒不起风险
            sink(stats);
            return;
        }

        dispatcherObject.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => sink(stats)));
    }

    /// <summary>限流记一次失败日志。绝不重新抛出。</summary>
    private void Fail(string phase, Exception ex)
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastFailureTick < FailureLogIntervalMs) return;
            _lastFailureTick = now;

            AppLog.Warn($"笔采样插件在 {phase} 阶段出错（已兜住，不影响落墨）：{ex.GetType().Name} {ex.Message}");
        }
        catch
        {
            // 连日志都失败就彻底放弃 —— 这里再抛出去就是 RTI 层的静默中断
        }
    }

    // ================================================================== 测试入口

    /// <summary>
    /// 直接喂一批合成点，<b>绕过 RTI</b>，按同一套口径算出统计。
    /// </summary>
    /// <remarks>
    /// ★ 存在的理由：本机没有笔、没有触摸屏，<c>RawStylusInput</c> 在验收 harness 里
    /// <b>不可能被伪造</b>。所以"统计口径对不对"这件事只能把逻辑抽出来单独验
    /// （见 <see cref="InkSampleBuffer"/>）。这里只是把 buffer 的三步串成一个入口。
    /// </remarks>
    public InkSampleStats Measure(IReadOnlyList<RawSample> samples)
    {
        _buffer.Reset();
        if (samples.Count == 0) return _buffer.End();

        _buffer.Begin(samples[0]);
        for (int i = 1; i < samples.Count; i++) _buffer.Add(samples[i]);

        return _buffer.End();
    }
}
