namespace MathPhys.Ink.Input;

/// <summary>
/// 笔线程采集到的<b>一个原始点</b>。
/// </summary>
/// <remarks>
/// 刻意是 <c>readonly record struct</c>：它是笔线程与 UI 线程之间唯一的传递物，
/// 值类型拷贝出去之后双方各持一份，不需要锁、也不存在"UI 线程读到半个对象"。
/// </remarks>
/// <param name="X">世界坐标 X（= PDF point，见 <c>InkSamplerPlugIn</c> 的坐标说明）。</param>
/// <param name="Y">世界坐标 Y。</param>
/// <param name="TimestampMs">采集时刻，单位毫秒（来自 <c>RawStylusInput.Timestamp</c>）。</param>
/// <param name="RawPressure">设备原始压力因子（伪造之前的值，留作对账）。</param>
public readonly record struct RawSample(double X, double Y, int TimestampMs, float RawPressure);

/// <summary>
/// 一笔的采样统计 —— <b>值类型快照</b>。
/// </summary>
/// <remarks>
/// 做成不可变值类型不是为了好看，是为了跨线程：笔线程算完 <see cref="InkSampleBuffer.End"/>
/// 得到一个快照，交给 UI 线程去写日志。UI 线程摸不到那个环形缓冲，也就不会与笔线程抢。
/// </remarks>
/// <param name="PointCount">这一笔的点数。</param>
/// <param name="DurationMs">这一笔从第一个点到最后一点的时长（毫秒）。</param>
/// <param name="PathLength">这一笔的累计路程（world 单位）。</param>
/// <param name="MaxSpeed">这一笔里最快的一点（world 单位 / 毫秒）。</param>
public readonly record struct InkSampleStats(
    int PointCount,
    int DurationMs,
    double PathLength,
    double MaxSpeed)
{
    /// <summary>
    /// 推算采样率（Hz）。
    /// </summary>
    /// <remarks>
    /// ★ 这是 M11 最要紧的一个数：它回答"这台一体机把笔报成了什么、多快"。
    /// <para>
    /// 用 <c>点数 - 1</c> 而不是点数：n 个点把时长分成 <c>n-1</c> 段，段数除以时长才是频率。
    /// 点数不足 2 或时长非正时返回 <b>0</b>（表示"算不出来"），调用方应当把它排除在平均之外 ——
    /// 一个孤零零的墨点没有采样率可言，混进平均只会把整体拉低。
    /// </para>
    /// </remarks>
    public double Hertz => DurationMs > 0 && PointCount >= 2
        ? (PointCount - 1) * 1000.0 / DurationMs
        : 0.0;

    /// <summary>平均速度（world 单位 / 毫秒）。</summary>
    public double MeanSpeed => DurationMs > 0 ? PathLength / DurationMs : 0.0;

    /// <summary>这一笔的采样是否够算采样率（否则只当"点了一下"）。</summary>
    public bool IsUsable => PointCount >= 2 && DurationMs > 0;
}

/// <summary>
/// 一笔的采样缓冲 —— <b>纯数据 + 纯计算，不依赖任何 RTI 类型</b>。
/// </summary>
/// <remarks>
/// ★ 它存在的理由是可测性，值得说清楚：
/// <para>
/// 本机（开发机）没有触摸屏、也没有笔，<c>RawStylusInput</c> 在验收 harness 里
/// <b>不可能被伪造</b>。所以凡是"想被断言"的逻辑都必须落在这一层 ——
/// 统计口径、采样率公式、速度计算、空/单点的兜底，全在这里；
/// 而真正的 RTI 回调里只留"取点 + 转交"两行，那两行靠真机日志验证。
/// </para>
/// <para>
/// <b>线程归属</b>：<see cref="Begin"/>/<see cref="Add"/>/<see cref="End"/> 只在笔线程调用，
/// 所以内部<b>一个锁都没有</b>；跨线程出去的是 <see cref="End"/> 返回的值拷贝。
/// </para>
/// </remarks>
public sealed class InkSampleBuffer
{
    private int _pointCount;
    private int _firstTimestampMs;
    private int _lastTimestampMs;
    private double _lastX;
    private double _lastY;
    private double _pathLength;
    private double _maxSpeed;

    /// <summary>当前缓冲里是否已经装了一笔（供调用方判断"要不要先 Begin"）。</summary>
    public bool IsActive => _pointCount > 0;

    /// <summary>当前已装点数（诊断用）。</summary>
    public int PointCount => _pointCount;

    /// <summary>
    /// 开始新的一笔。
    /// </summary>
    /// <remarks>
    /// 落笔即重置：这样即使上一笔因为异常没走到 <see cref="End"/>，
    /// 新的一笔也不会把上一笔的点数累进来（表现为"采样率虚高"）。
    /// </remarks>
    public void Begin(in RawSample sample)
    {
        _pointCount = 1;
        _firstTimestampMs = sample.TimestampMs;
        _lastTimestampMs = sample.TimestampMs;
        _lastX = sample.X;
        _lastY = sample.Y;
        _pathLength = 0;
        _maxSpeed = 0;
    }

    /// <summary>追加一个点。</summary>
    public void Add(in RawSample sample)
    {
        // 没 Begin 就先 Begin：宁可把它当第一个点，也不要凭空算出一段距离
        if (_pointCount == 0)
        {
            Begin(sample);
            return;
        }

        double dx = sample.X - _lastX;
        double dy = sample.Y - _lastY;
        double step = Math.Sqrt(dx * dx + dy * dy);

        // 防御：驱动在丢帧时可能报出 NaN 坐标。一个 NaN 会把整笔的"累计路程"永久污染成 NaN，
        // 而汇总里那一行「平均速度」就彻底没有意义了 —— 宁可信它没动过。
        // 点数照计：它确实是一个采样点，只是位置不可信。
        if (double.IsNaN(step) || double.IsInfinity(step)) step = 0;

        // 时间戳相同时用 1ms 兜底：同一毫秒里报多个点是一体机上的常见情形，
        // 直接相除会得到 Infinity，那个数会污染整笔的最大速度。
        int dt = sample.TimestampMs - _lastTimestampMs;
        double speed = step / (dt > 0 ? dt : 1);

        if (speed > _maxSpeed) _maxSpeed = speed;

        _pathLength += step;
        _pointCount++;
        _lastTimestampMs = sample.TimestampMs;
        _lastX = sample.X;
        _lastY = sample.Y;
    }

    /// <summary>收尾并取出统计快照；同时把缓冲置空。</summary>
    public InkSampleStats End()
    {
        var stats = new InkSampleStats(
            _pointCount,
            _pointCount > 0 ? _lastTimestampMs - _firstTimestampMs : 0,
            _pathLength,
            _maxSpeed);

        _pointCount = 0;
        _pathLength = 0;
        _maxSpeed = 0;
        return stats;
    }

    /// <summary>丢弃当前这一笔（换文档 / 清空 / 插件被禁用时用）。</summary>
    public void Reset()
    {
        _pointCount = 0;
        _pathLength = 0;
        _maxSpeed = 0;
    }
}
