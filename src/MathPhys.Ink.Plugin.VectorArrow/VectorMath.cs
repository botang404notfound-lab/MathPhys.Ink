using System;
using System.Collections.Generic;
using System.Windows;
using MathPhys.Ink.Plugin.Ruler;

namespace MathPhys.Ink.Plugin.VectorArrow;

/// <summary>
/// 矢量箭头的<b>全部数学</b>：方向角（物理习惯）、模长换算、吸附常量同源。
/// </summary>
/// <remarks>
/// 与 <see cref="RulerGeometry"/> 同一条原则：不依赖任何 WPF 控件、不依赖 <c>IToolContext</c>，
/// 只吃坐标、只吐数字 —— 于是验收 harness 能把"方向角算对没有"一条条钉死。
/// <b>算错角度这种事肉眼看不出来</b>（偏了 15° 的箭头也是箭头），只能靠断言。
/// <para>
/// <b>为什么吸附常量要从直尺"借用"而不是各写一份</b>：两个工具如果各存一套 15°/5°，
/// 将来改一处忘另一处，卷面上就会出现"直尺吸 15°、箭头吸 10°"的诡异不一致。
/// 这里用 <c>const = RulerGeometry.常量</c> 的写法把"同源"这件事写进代码，
/// harness 再断言一次"两个常量确实相等"。
/// </para>
/// </remarks>
public static class VectorMath
{
    // ---------------------------------------------------------------- 吸附常量（与直尺同源）

    /// <summary>吸附粒度：每 15° 一根"刻度线"（= <see cref="RulerGeometry.SnapStepDegrees"/>）。</summary>
    public const double SnapStepDegrees = RulerGeometry.SnapStepDegrees;

    /// <summary>吸附容差：只有"离最近的 15° 倍数 ≤ 5°"才吸（= <see cref="RulerGeometry.SnapToleranceDegrees"/>）。</summary>
    public const double SnapToleranceDegrees = RulerGeometry.SnapToleranceDegrees;

    /// <summary>误触阈值：箭头长度小于此值视为误触，不落对象（世界单位）。</summary>
    /// <remarks>
    /// 与直尺共用 2 world 的理由完全一样：老师切工具时习惯在卷面上点一下，
    /// 没有这一条，卷面会被一个个零长箭头（看着是个点）铺满。
    /// </remarks>
    public const double MinDragWorld = RulerGeometry.MinDragWorld;

    /// <summary>1 inch = 72 PDF point；1 inch = 2.54 cm（与直尺同一套换算）。</summary>
    public const double PointsPerInch = RulerGeometry.PointsPerInch;
    public const double CentimetersPerInch = RulerGeometry.CentimetersPerInch;

    /// <summary>默认比例：1 cm 长度代表 1 N。</summary>
    /// <remarks>
    /// 物理题的默认读法：画一条 3 cm 的力箭头，读作 3 N。比例以后可由面板改
    /// （本 Step 先只做这个默认值，"面板可改"留到下一轮）。
    /// </remarks>
    public const double DefaultNewtonsPerCentimeter = 1.0;

    // ---------------------------------------------------------------- 方向角（物理习惯）

    /// <summary>
    /// 两点连线的<b>方向角</b>：以 +x 轴为 0°、<b>逆时针为正</b>、结果在 <c>[0, 360)</c>。
    /// </summary>
    /// <remarks>
    /// ★ <b>世界坐标 y 轴向下</b>，而物理/数学里"向上是 90°"。所以这里必须对 dy 取负：
    /// 屏幕上方（世界 y 更小）算出的是 +90°，与老师读题时的直觉一致。
    /// 若不取负，屏幕上"向上的力"会被读成 270° —— 这正是"算错角度肉眼看不出来"的典型。
    /// <para>
    /// 与直尺的 <c>AxisDegrees</c> 的区别是刻意的：直尺画的是<b>直线</b>（没有正反方向，故归到 [0,180)），
    /// 而矢量箭头画的是<b>有向矢量</b>（180° 与 0° 是两根相反的箭头，必须区分）。
    /// </para>
    /// </remarks>
    public static double DirectionDegrees(Point from, Point to)
        => DirectionDegrees(to.X - from.X, to.Y - from.Y);

    /// <summary>方向角（按分量给）。见 <see cref="DirectionDegrees(Point, Point)"/> 的说明。</summary>
    public static double DirectionDegrees(double dx, double dy)
    {
        if (dx == 0 && dy == 0) return 0.0;

        // y 取负：世界 y 向下 ⇒ 数学 y 向上，物理读法才是"逆时针为正"
        double degrees = Math.Atan2(-dy, dx) * 180.0 / Math.PI;

        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;

        // -0.0 会一路传到界面上显示成 "-0°"
        return degrees == 0.0 ? 0.0 : degrees;
    }

    /// <summary>把角度规整到 [0, 360)。</summary>
    public static double NormalizeDegrees(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value == 0.0 ? 0.0 : value;
    }

    /// <summary>
    /// 吸附方向角：离最近的 15° 倍数 ≤ 容差才吸，否则原样返回。
    /// </summary>
    /// <remarks>
    /// <b>与直尺共用同一套常量、同一套"跨 0° 环绕"处理</b>（350° 离 0° 只有 10°、离 345° 只有 5°）。
    /// 这里不直接调 <c>RulerGeometry.SnapAxisDegrees</c>，是因为那个函数把角度归到 [0,180)（直线语义），
    /// 而矢量是 [0,360) —— 复用常量与思路，不复用函数。
    /// </remarks>
    public static double SnapDirectionDegrees(
        double degrees,
        double step = SnapStepDegrees,
        double tolerance = SnapToleranceDegrees)
    {
        double angle = NormalizeDegrees(degrees);
        double nearest = Math.Round(angle / step) * step;

        // 跨 360° 环绕：359° 的最近倍数是 360°（≡ 0°），归一化后回去
        return Math.Abs(nearest - angle) <= tolerance
            ? NormalizeDegrees(nearest)
            : angle;
    }

    /// <summary>
    /// 求某处的方向单位向量（世界坐标方向；<paramref name="degrees"/> 用物理读法 = 逆时针为正）。
    /// </summary>
    /// <remarks>与 <see cref="DirectionDegrees(Point, Point)"/> 互为逆运算（y 再取负回去）。</remarks>
    public static Vector UnitVector(double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new Vector(Math.Cos(radians), -Math.Sin(radians));
    }

    /// <summary>
    /// 把"手指拖到的点"约束成箭头的落点：<b>尾部永远不动</b>，箭头只沿吸附后的方向摆正。
    /// </summary>
    /// <param name="tail">尾部（按下点，绝不挪动）。</param>
    /// <param name="rawHead">手指当前所在的点。</param>
    /// <param name="snap">是否吸附。</param>
    /// <param name="snapped">本次是否真的发生了吸附。</param>
    /// <remarks>
    /// <b>保留长度、只转方向</b>：箭头尖落在"以尾部为心、半径 = 手指距离"的圆上，
    /// 滑到 15° 方向上。这样吸附生效/失效的瞬间只有旋转、没有长度跳动，手感最稳
    /// （与直尺 <c>ConstrainEnd</c> 同一条理由）。
    /// <para>
    /// 这里比直尺简单：矢量没有"轴角带符号歧义"（[0,360) 已经区分了正反方向），
    /// 所以不需要"取同向解"那一步。
    /// </para>
    /// </remarks>
    public static Point ConstrainHead(Point tail, Point rawHead, bool snap, out bool snapped)
    {
        var delta = rawHead - tail;
        double length = delta.Length;

        snapped = false;
        if (length <= double.Epsilon) return rawHead;

        double raw = DirectionDegrees(delta.X, delta.Y);
        double angle = snap ? SnapDirectionDegrees(raw) : raw;

        snapped = snap && Math.Abs(angle - raw) > 1e-9;

        var unit = UnitVector(angle);
        return new Point(tail.X + unit.X * length, tail.Y + unit.Y * length);
    }

    // ---------------------------------------------------------------- 模长换算

    /// <summary>世界坐标长度 → 厘米。</summary>
    public static double ToCentimeters(double worldLength)
        => worldLength * CentimetersPerInch / PointsPerInch;

    /// <summary>世界坐标长度 → 牛顿（按默认比例 1 cm = 1 N）。</summary>
    public static double ToNewtons(double worldLength, double newtonsPerCentimeter = DefaultNewtonsPerCentimeter)
        => ToCentimeters(worldLength) * newtonsPerCentimeter;

    /// <summary>数值的显示格式：整数不带小数点，其余保留 1 位。</summary>
    /// <remarks>"6.0 N" 比 "6 N" 更像物理题的答案（有效数字），所以这里用 F1。</remarks>
    public static string FormatNumber(double value) => value.ToString("F1");

    /// <summary>
    /// 给状态栏/箭头读数用的一句话：「大小 6.0 N　方向 37°」。
    /// </summary>
    /// <remarks>中间用全角空格分隔：避免在不同字号下两个数字粘成一片。</remarks>
    public static string Describe(double worldLength, double directionDegrees, string unit = "N")
        => $"大小 {FormatNumber(ToNewtons(worldLength))} {unit}　方向 {Math.Round(directionDegrees):F0}°";

    // ---------------------------------------------------------------- 矢量求和（合力）

    /// <summary>
    /// 多个矢量（模长 + 方向角）的<b>矢量和</b>，返回合矢量。
    /// </summary>
    /// <param name="vectors">各分矢量：<c>模长（N）</c> 与 <c>方向角（度，逆时针为正）</c>。</param>
    /// <remarks>
    /// ★ 为什么<b>单独</b>把这个函数拎出来、且只吃数字不吃对象：
    /// 3-4-5 这类经典组合必须能被断言钉死（3 N 向东 + 4 N 向北 ⇒ 5 N、36.87°）。
    /// "合力算错了"是最难在卷面上被发现的一类错误 —— 老师看到的只是"斜着的一根箭头"，
    /// 它到底该多长、该朝哪儿，只有算过的人才知道。
    /// <para>
    /// 世界 y 向下的坑在这里也要过一次：本函数全程用<b>物理读法</b>（逆时针为正），
    /// 由 <see cref="UnitVector"/> 负责与屏幕坐标互转。
    /// </para>
    /// <para>
    /// 分矢量一个都没有时返回 <c>(0, 0)</c>；全部相互抵消时同样返回零矢量 ——
    /// 调用方据此决定"不落对象"（卷面上不该出现一根零长箭头）。
    /// </para>
    /// </remarks>
    public static (double Magnitude, double DirectionDegrees) VectorSum(
        IEnumerable<(double Magnitude, double DirectionDegrees)> vectors)
    {
        // ★ 全程用【世界坐标分量】累加，不要在中途"翻译"成物理读法：
        //   UnitVector 给的就是世界向（y = −sin），而 DirectionDegrees(dx, dy) 收的
        //   也正是世界分量（它内部自己会对 dy 取负）。两者本来配套。
        //   中间多做一次语义转换（例如误以为要传"物理分量"）会让 y 被取负两次，
        //   角度沿 x 轴镜像 —— 3 东 + 4 北 会算成 53.13° 而不是 36.87°（实测踩过，
        //   模长还是对的，所以肉眼只会觉得"箭头怎么斜反了"）。
        double sx = 0, sy = 0;   // 世界坐标 x / y 分量（y 轴向下）

        foreach (var (magnitude, degrees) in vectors)
        {
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude)) continue;

            var unit = UnitVector(degrees);      // 世界向：y 已取负
            sx += unit.X * magnitude;
            sy += unit.Y * magnitude;
        }

        double net = Math.Sqrt(sx * sx + sy * sy);

        // 世界分量直接喂回去（DirectionDegrees 会对 dy 取负，得到物理读法）
        return (net, DirectionDegrees(sx, sy));
    }

    /// <summary>
    /// 合力的大小/方向 → 状态栏读数：「合力 5.0 N　方向 37°」。
    /// </summary>
    /// <remarks>与 <see cref="Describe"/> 同一个格式，只把前缀换成"合力"，好让老师一眼认出这句说的是谁。</remarks>
    public static string SumDescribe(double magnitudeNewtons, double directionDegrees, string unit = "N")
        => $"合力 {FormatNumber(magnitudeNewtons)} {unit}　方向 {Math.Round(directionDegrees):F0}°";

    /// <summary>合矢量是否"小到不该落对象"（零矢量、或几乎完全抵消）。</summary>
    /// <remarks>
    /// 阈值取"1/10 N"量级：两个 5 N 反向的力相减，浮点结果可能是 1e-15 而不是 0，
    /// 用 <c>== 0</c> 判断会漏掉它 —— 表现成卷面上多出一根看不见的零长箭头。
    /// </remarks>
    public const double ZeroMagnitudeN = 0.05;

    /// <summary>合矢量是否应视为零（不落对象）。</summary>
    public static bool IsZeroSum(double magnitudeNewtons)
        => double.IsNaN(magnitudeNewtons) || magnitudeNewtons < ZeroMagnitudeN;

    // ---------------------------------------------------------------- 正交分解

    /// <summary>
    /// 把一个矢量<b>正交分解</b>成两个分量（按给定的数学基矢量）。
    /// </summary>
    /// <param name="magnitude">被分解的矢量模长（N）。</param>
    /// <param name="directionDegrees">被分解的矢量方向角（度，逆时针为正）。</param>
    /// <param name="baseXDegrees">x 分量所沿的方向角（默认 0° = 水平向右，即坐标系 +x 轴）。</param>
    /// <param name="baseYDegrees">y 分量所沿的方向角（默认 90° = 竖直向上，即坐标系 +y 轴）。</param>
    /// <returns>两个分量：<c>(沿基 A 的模长, 沿基 B 的模长)</c>；<b>带符号</b>（负值 = 沿该基的反方向）。</returns>
    /// <remarks>
    /// ★ 返回值<b>带符号</b>是刻意的，也是本次实现的关键决定：
    /// <list type="bullet">
    /// <item>物理课上分解一个斜向力，两个分量常常一个是负的（例如斜向左上的力，
    ///       "水平分量"指向 −x）。若强行取绝对值，箭头方向就丢了 —— 而那正是要讲的东西。</item>
    /// <item>带符号才能让 <b>可逆</b>成立：分量矢量和必须精确回到原矢量。
    ///       取绝对值会把"向左"折成"向右"，合成回来就错了，且<b>肉眼看不出来</b>。</item>
    /// </list>
    /// <para>
    /// 算法：把被分解矢量转成世界分量 <c>(vx, vy)</c>，再分别<b>投影</b>到两条基方向上
    /// （单位基矢量点乘）。基不要求正交 —— 斜交分解（沿斜面 + 垂直斜面）也走这一个函数，
    /// 只要两条基不共线。
    /// </para>
    /// <para>
    /// 全程用<b>世界坐标分量</b>（与 <see cref="VectorSum"/> 同一条规矩，见那里的详细说明）：
    /// <see cref="UnitVector"/> 给世界向、<see cref="DirectionDegrees(double,double)"/> 收世界分量，
    /// 中间不再做任何语义转换。
    /// </para>
    /// </remarks>
    public static (double AlongX, double AlongY) Decompose(
        double magnitude, double directionDegrees,
        double baseXDegrees = 0.0, double baseYDegrees = 90.0)
    {
        var unitX = UnitVector(baseXDegrees);
        var unitY = UnitVector(baseYDegrees);

        double det = unitX.X * unitY.Y - unitX.Y * unitY.X;   // 2×2 基矩阵行列式

        // 两基共线 ⇒ 退化：此时无法分解（信息不足）。返回"全给第一条基"，
        // 让调用方画出一根分量、另一根为零，而不是抛异常（工具不该因为拖歪了而崩）。
        if (Math.Abs(det) < 1e-12)
            return (magnitude, 0.0);

        // 被分解矢量 → 世界分量（UnitVector 给的就是世界向）
        var unitV = UnitVector(directionDegrees);
        double vx = unitV.X * magnitude;
        double vy = unitV.Y * magnitude;

        // 解 [uX uY] · (a,b)ᵀ = v ：a 沿基 X、b 沿基 Y
        double alongX = (vx * unitY.Y - vy * unitY.X) / det;
        double alongY = (unitX.X * vy - unitX.Y * vx) / det;

        return (alongX, alongY);
    }

    /// <summary>
    /// 两个分量（沿基的带符号模长）复原成原矢量 —— <b>分解的可逆性验证</b>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Decompose"/> 配对使用：<c>Compose(Decompose(v)) == v</c>（误差 &lt; 1e-9）。
    /// harness 用这一条钉死"分解没算错" —— 分解错了在卷面上看不出来（两根分量箭头
    /// 看起来都挺合理），只有合成回去对不上才暴露。
    /// </remarks>
    public static (double Magnitude, double DirectionDegrees) Compose(
        double alongX, double alongY,
        double baseXDegrees = 0.0, double baseYDegrees = 90.0)
    {
        var unitX = UnitVector(baseXDegrees);
        var unitY = UnitVector(baseYDegrees);

        double sx = unitX.X * alongX + unitY.X * alongY;
        double sy = unitX.Y * alongX + unitY.Y * alongY;

        double net = Math.Sqrt(sx * sx + sy * sy);
        return (net, DirectionDegrees(sx, sy));
    }

    /// <summary>分解的分量表读数：「x 分量 3.0 N（→）　y 分量 4.0 N（↑）」。</summary>
    /// <remarks>
    /// 方向词（→ ↑ ← ↓）是给老师一眼对表用的：分量带符号，光看数字还要在脑子里
    /// 换算"−3 是向左还是向下"，加了箭头就不用想了。
    /// </remarks>
    public static string DecomposeDescribe(double alongX, double alongY, string unit = "N")
    {
        string arrowX = alongX >= 0 ? "→" : "←";
        string arrowY = alongY >= 0 ? "↑" : "↓";

        return $"分量 {FormatNumber(Math.Abs(alongX))} {unit}（{arrowX}）　"
             + $"{FormatNumber(Math.Abs(alongY))} {unit}（{arrowY}）";
    }

    // ---------------------------------------------------------------- 矢量组（打组）

    /// <summary>
    /// 把一组成员位姿从"组的基准系"搬到"组的当前系"：
    /// <c>成员位姿 = 组位姿 ⊗ 基准位姿</c>。
    /// </summary>
    /// <param name="memberBaseX">打组时成员中心相对组中心的偏移（组本地系，世界长度）。</param>
    /// <param name="memberBaseY">同上（y）。</param>
    /// <param name="groupBaseRotation">打组时组自身的旋转（度）。</param>
    /// <param name="groupBaseScale">打组时组自身的缩放。</param>
    /// <param name="groupCenter">组当前中心（世界）。</param>
    /// <param name="groupRotation">组当前旋转（度）。</param>
    /// <param name="groupScale">组当前缩放。</param>
    /// <param name="memberRotationOffset">成员自身相对组的额外旋转（度，打组时记下）。</param>
    /// <returns>成员应处的中心（世界），以及它应处的旋转角（度）。</returns>
    /// <remarks>
    /// ★ 为什么必须"记基准 + 现算"而不是"每帧给每个成员加同一个位移增量"：
    /// 平移好办（人人加同一个向量），但<b>旋转与缩放</b>不是 —— 绕组中心旋转 30° 时，
    /// 每根成员各自绕<b>自己</b>的中心转 30° 是不够的，它们还得沿圆弧挪位置。
    /// 用"基准位姿 ⊗ 组的变换增量"一次算出来，平移/旋转/缩放三种情况就自动统一了，
    /// 而且<b>多帧累乘不会漂移</b>（每帧都从基准重算，而不是在上一帧结果上叠加误差）。
    /// <para>
    /// 缩放对"偏移"是按比例放大的：组放大 1.5 倍，成员之间的间距也该变成 1.5 倍，
    /// 否则图形会挤成一团（这正是"只改成员各自 Scale、不管间距"的典型错法）。
    /// </para>
    /// <para>
    /// 缩放因子取<b>比例</b> <c>groupScale / groupBaseScale</c>：
    /// 组的 Scale 是绝对值，而成员要的是"相对打组时刻变化了多少"。
    /// </para>
    /// </remarks>
    public static (Point Center, double RotationDegrees) PlaceGroupMember(
        double memberBaseX, double memberBaseY,
        double groupBaseRotation, double groupBaseScale,
        Point groupCenter, double groupRotation, double groupScale,
        double memberRotationOffset)
    {
        double safeBaseScale = SafeScale(groupBaseScale);
        double safeScale = SafeScale(groupScale);
        double ratio = safeScale / safeBaseScale;

        // 组自己转过的角度 = 当前 − 基准
        double deltaDegrees = groupRotation - groupBaseRotation;

        // 偏移先按"组的缩放比"放大，再随"组转过的角"转 —— 顺序不能反：
        // 基准偏移是在<b>组的基准朝向</b>下量的，所以先缩放（同一坐标系内），再旋转。
        var offset = Rotate(new Vector(memberBaseX * ratio, memberBaseY * ratio), deltaDegrees);

        var center = new Point(groupCenter.X + offset.X, groupCenter.Y + offset.Y);
        return (center, NormalizeDegrees(groupRotation + memberRotationOffset));
    }

    /// <summary>
    /// 从一组成员位姿算出"包围盒中心 + 对角线尺度"，供打组时定组的基准。
    /// </summary>
    /// <param name="corners">各成员的四个世界角点（或包围盒四角）。</param>
    /// <returns>中心，以及外接矩形的宽与高（世界长度）。</returns>
    /// <remarks>
    /// 空集合返回 <c>(0,0)</c> 与零尺寸：调用方应据此判定"没有成员、不打组"，
    /// 而不是让一个 NaN 中心流到画面上。
    /// </remarks>
    public static (Point Center, double Width, double Height) UnionBounds(IEnumerable<Point> corners)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var p in corners)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)
                || double.IsInfinity(p.X) || double.IsInfinity(p.Y)) continue;

            any = true;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        if (!any) return (new Point(0, 0), 0.0, 0.0);

        return (new Point((minX + maxX) / 2.0, (minY + maxY) / 2.0), maxX - minX, maxY - minY);
    }

    /// <summary>接收一个向量绕原点旋转给定角度（度）。</summary>
    public static Vector Rotate(Vector v, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        // 世界 y 向下 ⇒ 屏幕上的"逆时针"对应数学上的顺时针，sin 取负与 UnitVector 同一套约定
        return new Vector(v.X * cos + v.Y * sin, -v.X * sin + v.Y * cos);
    }

    /// <summary>把缩放值夹到一个"能算"的范围（0 或负数会让比例爆炸）。</summary>
    private static double SafeScale(double scale)
        => scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;

    /// <summary>一组矢量的大小/方向读数：「矢量组：3 根，合计 12.0 N」。</summary>
    /// <remarks>合计是"模长之和"（不是矢量和）—— 这里只是给老师一个"这堆力大概多大"的量纲感。</remarks>
    public static string GroupDescribe(int count, double totalMagnitude, string unit = "N")
        => $"矢量组：{count} 根，合计 {FormatNumber(totalMagnitude)} {unit}";
}
