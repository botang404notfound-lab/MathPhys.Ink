using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MathPhys.Ink.Infrastructure;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Gfx;

/// <summary>
/// 画布上全部图形对象的<b>唯一真相源</b>：集合、Z 序、选中、命中测试、增删改。
/// </summary>
/// <remarks>
/// 与 <c>InkHistory</c> 一样是自包含的：自己管集合、自己算命中，只把结果通知出去
/// （视觉层订阅 <see cref="Changed"/> 去同步，界面订阅 <see cref="SelectionChanged"/> 去刷新）。
/// <para>
/// <b>Z 序 = 列表顺序</b>（末尾在最上层），不额外维护 Z 字段：
/// 两个排序来源迟早会不一致，而"选中时临时提到最上层"只是显示层的把戏（见 <see cref="GfxObjectLayer"/>），
/// 不该改动真实顺序。
/// </para>
/// <para>
/// 所有对渲染器的调用都包了异常隔离 —— 渲染器是插件代码，
/// 它出错只能损失它自己那一个图形，绝不能让老师正在用的白板崩掉。
/// </para>
/// </remarks>
public sealed class GfxObjectStore
{
    /// <summary>点击容差（世界单位）。约 0.07 cm —— 抓图形时按得住，又不至于把旁边的也抓上。</summary>
    public const double HitToleranceWorld = 2.0;

    private readonly GfxRendererCatalog _catalog;
    private readonly List<GfxObject> _objects = new();

    private string? _selectedId;

    /// <summary>平移折算的锚点：正在折算的对象 Id 与"上一帧请求的中心"。</summary>
    private string? _rebaseAnchorId;
    private Point _rebaseAnchorCenter;

    public GfxObjectStore(GfxRendererCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>集合、位姿或参数发生变化（视觉层据此同步）。</summary>
    public event EventHandler? Changed;

    /// <summary>选中项发生变化。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>新增了一个对象（宿主据此实现"创建型工具用完自动切回选择"）。</summary>
    public event EventHandler<IGfxObjectRef>? ObjectAdded;

    /// <summary>全部对象（只读视图，按 Z 序）。</summary>
    public IReadOnlyList<IGfxObjectRef> Objects
    {
        get
        {
            // 直接暴露 IGfxObjectRef 列表：GfxObject 是 internal 的，插件拿不到它，也就改不了别人。
            var view = new List<IGfxObjectRef>(_objects.Count);
            foreach (var obj in _objects) view.Add(obj);
            return view;
        }
    }

    /// <summary>对象个数。</summary>
    public int Count => _objects.Count;

    /// <summary>当前选中的对象 Id。</summary>
    public string? SelectedId => _selectedId;

    /// <summary>当前选中的对象。</summary>
    public IGfxObjectRef? Selected => FindObject(_selectedId);

    /// <summary>
    /// 认不出 Kind、因而只能显示占位框的对象个数。
    /// </summary>
    /// <remarks>
    /// 这个数字必须让用户看见：它代表"板书里有东西还在、但你没装插件看不懂"。
    /// 悄悄不显示等于让人以为图形丢了。
    /// </remarks>
    public int MissingRendererCount
    {
        get
        {
            int count = 0;
            foreach (var obj in _objects)
            {
                if (!_catalog.Contains(obj.Kind)) count++;
            }

            return count;
        }
    }

    /// <summary>缺插件的对象都是谁（去重后的插件名或 Kind），供状态栏说明。</summary>
    public IReadOnlyList<string> MissingNames
    {
        get
        {
            var names = new List<string>();

            foreach (var obj in _objects)
            {
                if (_catalog.Contains(obj.Kind)) continue;

                string name = string.IsNullOrEmpty(obj.PluginName) ? obj.Kind : obj.PluginName;
                if (!names.Contains(name)) names.Add(name);
            }

            return names;
        }
    }

    /// <summary>生成一个短 Id（存档里要稳定、日志里要念得出来）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 落成一个新对象（末尾 = 最上层），并自动选中它。
    /// </summary>
    /// <remarks>
    /// 自动选中是有意的：创建型工具用完就切回选择工具，
    /// 老师下一个动作十有八九是"挪一下/转一下"，选中框先亮着才顺手。
    /// </remarks>
    public string Add(GfxDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        double scale = draft.Scale;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

        var obj = new GfxObject
        {
            Id = NewId(),
            Kind = draft.Kind ?? string.Empty,
            PluginName = _catalog.OwnerOf(draft.Kind),
            Center = draft.Center,
            RotationDegrees = double.IsNaN(draft.RotationDegrees) ? 0 : draft.RotationDegrees,
            Scale = scale,
            Color = draft.Color,
            LineWorldWidth = draft.LineWorldWidth > 0 ? draft.LineWorldWidth : 1.5,
        };

        foreach (var pair in draft.Numbers) obj.Numbers[pair.Key] = pair.Value;
        foreach (var pair in draft.Texts) obj.Texts[pair.Key] = pair.Value;

        obj.LocalSize = MeasureSize(obj);

        _objects.Add(obj);
        _selectedId = obj.Id;

        if (!_catalog.Contains(obj.Kind))
        {
            AppLog.Warn($"落成的对象 Kind=「{obj.Kind}」没有注册渲染器，只能显示占位框"
                        + "（数据仍在，装上对应插件后会正常显示）。");
        }

        RaiseChanged();
        ObjectAdded?.Invoke(this, obj);
        RaiseSelectionChanged();
        return obj.Id;
    }

    /// <summary>删除一个对象。</summary>
    public bool Remove(string id)
    {
        var obj = FindObject(id);
        if (obj is null) return false;

        _objects.Remove(obj);

        if (string.Equals(_selectedId, id, StringComparison.Ordinal))
        {
            _selectedId = null;
            RaiseSelectionChanged();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>清空全部对象（换文档时）。</summary>
    public void Clear()
    {
        if (_objects.Count == 0 && _selectedId is null) return;

        _objects.Clear();
        _selectedId = null;

        RaiseChanged();
        RaiseSelectionChanged();
    }

    /// <summary>按 Id 取对象（宿主内部用；插件拿不到可变对象）。</summary>
    internal GfxObject? FindObject(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        foreach (var obj in _objects)
        {
            if (string.Equals(obj.Id, id, StringComparison.Ordinal)) return obj;
        }

        return null;
    }

    /// <summary>某个 Id 是不是某个组的成员；是则返回那个组（否则 <c>null</c>）。</summary>
    /// <remarks>
    /// 点选时用它把"点到一根箭头"翻译成"选中整组"。
    /// 多个组同时含同一个成员时返回<b>最先遇到</b>的那个（正常情况下不会发生 —— 打组时排除了已有组）。
    /// </remarks>
    internal IGfxObjectRef? FindGroupOf(string? memberId)
    {
        if (string.IsNullOrEmpty(memberId)) return null;

        foreach (var obj in _objects)
        {
            string raw = obj.GetText(GroupOfKey, "");
            if (string.IsNullOrEmpty(raw)) continue;

            foreach (var id in SplitIds(raw))
            {
                if (string.Equals(id, memberId, StringComparison.Ordinal)) return obj;
            }
        }

        return null;
    }

    /// <summary>组里现有多少个还能找到的成员（状态栏说明用）。</summary>
    internal int CountGroupMembers(string? groupId)
    {
        var group = FindObject(groupId);
        if (group is null) return 0;

        int count = 0;
        foreach (var id in SplitIds(group.GetText(GroupOfKey, "")))
        {
            if (FindObject(id) is not null) count++;
        }

        return count;
    }

    /// <summary>
    /// 选中一个对象；<c>null</c> 表示取消选中。
    /// </summary>
    /// <remarks>
    /// 传一个不存在的 Id 时<b>取消选中</b>而不是保持原样：
    /// 只有这样，"撤销删掉了一个对象"之后界面才不会继续显示它的选中框。
    /// </remarks>
    public void Select(string? id)
    {
        var target = FindObject(id);
        string? next = target?.Id;

        if (string.Equals(_selectedId, next, StringComparison.Ordinal)) return;

        _selectedId = next;
        RaiseSelectionChanged();
    }

    /// <summary>按"最上层优先"找被点中的对象。</summary>
    public IGfxObjectRef? HitTest(Point world, double toleranceWorld = HitToleranceWorld)
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (GfxTransform.HitTest(_objects[i], world, toleranceWorld)) return _objects[i];
        }

        return null;
    }

    /// <summary>改位姿。三个值一起给，避免出现"只转了一半"的中间态。</summary>
    /// <remarks>
    /// 被<b>绑定</b>的对象（<c>Texts[<see cref="BindKey"/>]</c> 指向别人）不自己动：抓它 = 抓整幅图，
    /// 位姿改动转嫁给它绑定的目标，曲线随即跟着目标被重算回来。
    /// <para>
    /// 若不这么做，用户抓住曲线一拖，<see cref="RecomputeAttachments"/> 下一帧就把它拉回去 ——
    /// 表现成"选中框在、手柄也在，就是拖不动"，比干脆不让拖更让人困惑。
    /// </para>
    /// <para>
    /// ★ <b>例外（M12 S7.2）</b>：渲染器实现了 <see cref="IGfxPoseRebaser"/> 的对象（函数曲线），
    /// 纯平移优先<b>折算进参数</b>（曲线跟手、坐标轴原地不动），折算不了才转嫁。
    /// </para>
    /// </remarks>
    public bool UpdatePose(string id, Point center, double rotationDegrees, double scale)
    {
        var obj = FindObject(id);
        if (obj is null) return false;

        if (FindBindTarget(obj) is { } bindTarget)
        {
            // 纯平移（旋转/缩放没动）且渲染器会折算 ⇒ 拖曲线改参数，而不是拖走整张图
            if (IsPureTranslation(obj, rotationDegrees, scale)
                && TryRebaseIntoParams(obj, id, center))
            {
                return true;
            }

            // 缩放按"倍数"转嫁：曲线的 Scale = 目标的 unitWorld × 目标的 Scale，
            // 所以这里要转的是比例（拖动中每帧给的都是绝对 Scale，取增量比例累乘即可）。
            double ratio = obj.Scale > 0 && scale > 0 ? scale / obj.Scale : 1.0;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0) ratio = 1.0;

            bool changed = ApplyPose(bindTarget, center, rotationDegrees, bindTarget.Scale * ratio);
            if (changed) RaiseChanged();
            return changed;
        }

        bool moved = ApplyPose(obj, center, rotationDegrees, scale);
        if (moved)
        {
            // ★ 拖动"矢量组" ⇒ 把位姿变化传播给成员（成员不是"跟着看"，是被主机搬过去）
            PropagateGroup(obj);

            // 组动了、成员也动了 ⇒ 合力这类"取决于别人"的对象同样要重算
            RaiseChanged();
        }

        return moved;
    }

    /// <summary>拖动是否为纯平移（旋转与缩放都和对象当前值一致）。</summary>
    private static bool IsPureTranslation(GfxObject obj, double rotationDegrees, double scale)
        => obj.RotationDegrees.Equals(rotationDegrees) && obj.Scale.Equals(scale);

    /// <summary>
    /// 把这一帧的平移折算进对象参数。成功返回 <c>true</c>（位姿保持不动）。
    /// </summary>
    /// <remarks>
    /// ★ <b>增量口径</b>：选择工具每帧传的是"绝对中心"（<c>起始中心 + 累计位移</c>），
    /// 而折算要的是"这一帧走了多少"。锚点在 <see cref="BeginRebaseDrag"/>（拖动开始）复位到
    /// 对象中心，此后每帧 <c>增量 = 请求中心 − 上一帧中心</c>；
    /// 换了对象（<c>_rebaseAnchorId</c> 不符）就当场重新按对象中心起算 ——
    /// 折算路径从不动位姿，所以锚点基准全程稳定。
    /// </remarks>
    private bool TryRebaseIntoParams(GfxObject obj, string id, Point requestedCenter)
    {
        // 换了拖动对象 ⇒ 锚点重新起算（对象中心就是基准，因为折算从不改位姿）
        if (!string.Equals(_rebaseAnchorId, id, StringComparison.Ordinal))
        {
            _rebaseAnchorId = id;
            _rebaseAnchorCenter = obj.Center;
        }

        var delta = new Vector(requestedCenter.X - _rebaseAnchorCenter.X,
                               requestedCenter.Y - _rebaseAnchorCenter.Y);
        _rebaseAnchorCenter = requestedCenter;

        // 没动就不折腾（也不许把"零平移"误报成成功）
        if (delta.LengthSquared < 1e-12) return false;

        if (_catalog.Find(obj.Kind) is not IGfxPoseRebaser rebaser) return false;

        IReadOnlyDictionary<string, double> numbers;
        IReadOnlyDictionary<string, string> texts;

        try
        {
            if (!rebaser.TryRebase(obj, delta, out numbers, out texts)) return false;
        }
        catch (Exception ex)
        {
            // 渲染器是插件代码：折算失败只损失"这次拖动改参数"，照旧转嫁，绝不能崩
            AppLog.Warn($"渲染器 {_catalog.Find(obj.Kind)?.GetType().Name}（{obj.Kind}）平移折算抛异常："
                        + $"{ex.GetType().Name} {ex.Message}");
            return false;
        }

        foreach (var pair in numbers) obj.Numbers[pair.Key] = pair.Value;
        foreach (var pair in texts) obj.Texts[pair.Key] = pair.Value;

        // ★ 折算不改窗口尺寸 ⇒ LocalSize 不变，视觉层的"尺寸判据"看不见这次变化，
        //   必须递增内容版本号让曲线重画（否则表现成"拖了没反应"）。
        obj.ContentVersion++;
        obj.LocalSize = MeasureSize(obj);

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 声明"一次针对某对象的新拖动开始"：平移折算的锚点复位到该对象当前中心。
    /// </summary>
    /// <remarks>
    /// 由选择工具在移动拖动起步时调用。不复位的后果：第二次拖同一根曲线时，
    /// 锚点还停在上一次松手的位置，第一帧就把整段位移差一次性折进参数 —— 曲线猛跳。
    /// </remarks>
    public void BeginRebaseDrag(string id)
    {
        var obj = FindObject(id);
        if (obj is null) return;

        _rebaseAnchorId = id;
        _rebaseAnchorCenter = obj.Center;
    }

    /// <summary>
    /// 把组对象的位姿变化传播给它的成员。
    /// </summary>
    /// <remarks>
    /// ★ 与插件 <c>VectorMath.PlaceGroupMember</c> 是同一个公式的两份实现。
    /// 为什么必须写两份：<b>契约不能改</b>（否则已分发的插件 dll 会 TypeLoadException），
    /// 于是宿主无法调用插件的数学；而"拖动时必须真的搬动成员"这件事又必须发生在宿主
    /// （拖动的每一帧都在宿主手里）。公式只有六行，重复的代价远小于改契约的风险。
    /// <para>
    /// 若公式的一侧被改动，harness 有一条断言专门比对"宿主传播"与"插件纯函数"结果一致 ——
    /// 两份实现不同步会当场被判失败，而不是变成"拖起来怪怪的"这种查不出的现象。
    /// </para>
    /// <para>
    /// <b>找不到的成员跳过</b>：老师删掉一根是正常操作，组按剩下的继续动。
    /// </para>
    /// <para>
    /// <b>只改字段、不抛事件</b>（由 <see cref="RaiseChanged"/> 统一抛一次），避免递归。
    /// </para>
    /// </remarks>
    private void PropagateGroup(GfxObject group)
    {
        string raw = group.GetText(GroupOfKey, "");
        if (string.IsNullOrEmpty(raw)) return;

        double baseRotation = group.GetNumber(GroupBaseRotationKey, 0.0);
        double baseScale = SafeScale(group.GetNumber(GroupBaseScaleKey, 1.0));
        double groupScale = SafeScale(group.Scale);
        double ratio = groupScale / baseScale;
        double deltaDegrees = group.RotationDegrees - baseRotation;

        int index = 0;

        foreach (var id in SplitIds(raw))
        {
            var member = FindObject(id);
            if (member is null)
            {
                index++;
                continue;
            }

            double bx = group.GetNumber(GroupBaseXPrefix + index, 0.0) * ratio;
            double by = group.GetNumber(GroupBaseYPrefix + index, 0.0) * ratio;
            double brot = group.GetNumber(GroupBaseRotPrefix + index, 0.0);

            var offset = RotateVector(bx, by, deltaDegrees);

            member.Center = new Point(group.Center.X + offset.X, group.Center.Y + offset.Y);
            member.RotationDegrees = NormalizeDegrees(group.RotationDegrees + brot);

            index++;
        }
    }

    /// <summary>向量绕原点旋转（度，世界 y 向下的屏幕坐标）。</summary>
    private static Vector RotateVector(double x, double y, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new Vector(x * cos + y * sin, -x * sin + y * cos);
    }

    /// <summary>切分逗号分隔的 Id 列表（去空、去重；与插件侧 ParseIdList 同一套规矩）。</summary>
    private static IEnumerable<string> SplitIds(string raw)
    {
        var seen = new List<string>();

        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string id = piece.Trim();
            if (id.Length == 0 || seen.Contains(id, StringComparer.Ordinal)) continue;

            seen.Add(id);
            yield return id;
        }
    }

    private static double SafeScale(double scale)
        => scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale) ? 1.0 : scale;

    /// <summary>真正写位姿（含夹紧与"没变就不动"判断）；返回是否发生了变化。</summary>
    private static bool ApplyPose(GfxObject obj, Point center, double rotationDegrees, double scale)
    {
        double safeScale = double.IsNaN(scale) || double.IsInfinity(scale) ? obj.Scale : Math.Clamp(scale, 0.1, 20.0);
        double safeRotation = double.IsNaN(rotationDegrees) || double.IsInfinity(rotationDegrees)
            ? obj.RotationDegrees
            : NormalizeDegrees(rotationDegrees);

        if (obj.Center == center && obj.Scale.Equals(safeScale) && obj.RotationDegrees.Equals(safeRotation))
        {
            return false;
        }

        obj.Center = center;
        obj.Scale = safeScale;
        obj.RotationDegrees = safeRotation;
        return true;
    }

    /// <summary>改数值参数（合并语义）；尺寸会按新参数重算。</summary>
    public bool UpdateNumbers(string id, IReadOnlyDictionary<string, double> numbers)
    {
        var obj = FindObject(id);
        if (obj is null || numbers is null || numbers.Count == 0) return false;

        foreach (var pair in numbers) obj.Numbers[pair.Key] = pair.Value;

        // 数值参数可能只改"画成什么样"而不改窗口尺寸（例如圆规圆弧的起止角，
        // LocalSize 恒为 2R×2R）⇒ 必须递增内容版本号，否则视觉层判"没变"不重画。
        obj.ContentVersion++;
        obj.LocalSize = MeasureSize(obj);

        RaiseChanged();
        return true;
    }

    /// <summary>改文本参数（合并语义）。文本参数可能影响尺寸（比如坐标系的标签），所以也重算。</summary>
    public bool UpdateTexts(string id, IReadOnlyDictionary<string, string> texts)
    {
        var obj = FindObject(id);
        if (obj is null || texts is null || texts.Count == 0) return false;

        foreach (var pair in texts) obj.Texts[pair.Key] = pair.Value;

        // 同 UpdateNumbers：文本可能只影响内容不影响 LocalSize，递增版本号保证重画。
        obj.ContentVersion++;
        obj.LocalSize = MeasureSize(obj);

        RaiseChanged();
        return true;
    }

    /// <summary>导出全部对象（落盘与撤销快照共用一种形态）。</summary>
    public IReadOnlyList<GfxObjectData> Snapshot()
    {
        var list = new List<GfxObjectData>(_objects.Count);
        foreach (var obj in _objects) list.Add(obj.ToData());
        return list;
    }

    /// <summary>
    /// 用给定数据整体替换当前对象集合。
    /// </summary>
    /// <remarks>
    /// 装载存档、撤销快照都走这里。<b>整体替换而不是逐条增删</b>，
    /// 与 <c>ReplaceStrokes</c> 同一个理由：一次装载不该在历史里留下几十步噪音。
    /// <para>
    /// 选中项的处理是"能保就保"：还原后如果原来选中的 Id 还在，就保持选中（撤销一次拖动后
    /// 选中框不该消失）；不在了（比如撤销了一次新增）就取消选中。
    /// </para>
    /// </remarks>
    public void Restore(IReadOnlyList<GfxObjectData>? data)
    {
        _objects.Clear();

        if (data is not null)
        {
            foreach (var item in data)
            {
                if (item is null || string.IsNullOrEmpty(item.Kind))
                {
                    AppLog.Warn("跳过一个 Kind 为空的图形对象（存档里可能有手改过的内容）。");
                    continue;
                }

                var obj = GfxObject.FromData(item);
                obj.LocalSize = MeasureSize(obj);
                _objects.Add(obj);
            }
        }

        bool selectionSurvives = FindObject(_selectedId) is not null;

        RaiseChanged();

        if (!selectionSurvives)
        {
            _selectedId = null;
            RaiseSelectionChanged();
        }
    }

    // ================================================================ 绑定 / 跟随
    //
    // 「一个图形挂在另一个图形上」是本层新增的一等能力：被绑对象的 Texts 里放一个 BindKey
    // （值 = 目标对象 Id），它的位姿就不由用户拖动决定，而是每次从目标推导出来。
    //
    // 为什么必须落在宿主这一层：渲染器只拿得到"自己这一个对象"（IGfxObjectRef），
    // 看不见画布上的别人 —— "我要跟着谁、他现在在哪"只有掌握全部对象的存储层答得出来。
    //
    // 为什么用 Texts 字符串键，而不是给契约加个成员：契约一改，已经分发出去的插件 dll
    // 就有 TypeLoadException 的风险（M7.4 Step 1 起立的规矩）。字符串键是插件与宿主之间
    // 早已存在的约定形式（Kind=="coordsystem"、Numbers["unitWorld"] 都是这么来的）。

    /// <summary>绑定键：<c>Texts[BindKey]</c> = 目标对象的 Id。</summary>
    public const string BindKey = "bindTo";

    /// <summary>相对目标原点的偏移（目标本地系、世界长度），会跟着目标一起转。</summary>
    public const string BindRelXKey = "bindRelX";
    public const string BindRelYKey = "bindRelY";

    /// <summary>相对目标的额外旋转（度）。</summary>
    public const string BindRelRotKey = "bindRelRot";

    /// <summary>目标身上"一数学单位 = 多少世界长度"的参数键（坐标系插件约定的键名）。</summary>
    public const string TargetUnitKey = "unitWorld";

    /// <summary>
    /// 合力键：<c>Texts[SumOfKey]</c> = 逗号分隔的"分矢量 Id"列表。
    /// </summary>
    /// <remarks>
    /// ★ 为什么需要它、以及为什么<b>不是</b>给契约加成员：
    /// 绑定区（<see cref="BindKey"/>）只认<b>单个</b>目标 Id，而合力天生是"一对多"——
    /// 一根合力要跟着 <b>N</b> 根分矢量走。给契约加个"多个父对象"的概念会动到已分发插件的
    /// 类型布局（TypeLoadException 风险），而字符串键是插件与宿主之间早已存在的约定形式。
    /// <para>
    /// 本层的职责只有一件事：<b>每次集合变动都把合力对象"碰一下"</b>（重算 LocalSize），
    /// 让它对应的渲染器有机会用最新的分矢量重建几何。具体怎么求和是插件的事。
    /// </para>
    /// </remarks>
    public const string SumOfKey = "sumOf";

    /// <summary>
    /// 矢量组键：<c>Texts[GroupOfKey]</c> = 逗号分隔的"成员 Id"列表。
    /// </summary>
    /// <remarks>
    /// ★ 与 <see cref="SumOfKey"/> 同一套设计：组也是"一对多"，而且比合力更进一步 ——
    /// 拖动组时宿主必须把组的位姿变化<b>传播给成员</b>（不是让成员自己看组）。
    /// <para>
    /// 为什么传播放在宿主、而"怎么传播"的数学放在插件：宿主只认得字符串键与数字键，
    /// 它不知道"成员位姿 = 组位姿 ⊗ 基准"这件事 —— 那是矢量插件的语义。
    /// 于是插件把算好的<b>基准增量</b>写在自己的 Numbers 里（前缀 <c>bx/by/brot</c>），
    /// 宿主只管按同一套公式搬运。公式本身很短，两处都写一份是刻意的：
    /// 契约不能改，而宿主必须能在不知道插件存在的情况下驱动它。
    /// </para>
    /// </remarks>
    public const string GroupOfKey = "groupOf";

    /// <summary>组对象上"打组时组自身的旋转"。</summary>
    public const string GroupBaseRotationKey = "baseRot";

    /// <summary>组对象上"打组时组自身的缩放"。</summary>
    public const string GroupBaseScaleKey = "baseScale";

    // 成员基准键的前缀（与插件 GroupRenderer 的同名常量一致）
    private const string GroupBaseXPrefix = "bx";
    private const string GroupBaseYPrefix = "by";
    private const string GroupBaseRotPrefix = "brot";

    /// <summary>被绑定对象的目标对象；没绑定、或目标已被删掉时返回 <c>null</c>（此时它自由摆放）。</summary>
    internal GfxObject? FindBindTarget(GfxObject obj)
    {
        string id = obj.GetText(BindKey, "");
        return string.IsNullOrEmpty(id) ? null : FindObject(id);
    }

    /// <summary>
    /// 把所有"绑定了别人"的对象位姿重算一遍，让它们跟上目标（移动 / 旋转 / 缩放）。
    /// </summary>
    /// <remarks>
    /// 放在 <see cref="RaiseChanged"/> 里跑，而不是让每个改位姿的地方自己记得调 ——
    /// 漏掉一处的表现是"偶尔有几帧曲线没跟上轴"，这种 bug 靠肉眼几乎抓不住。
    /// 这里只改字段、不抛事件，所以不会递归。
    /// <para>
    /// <b>目标是缺失的</b>（坐标系被删了）时<b>保持现状、不静默丢</b>：曲线留在原地，
    /// 老师还能把它删掉或再画一张坐标系。
    /// </para>
    /// </remarks>
    private void RecomputeAttachments()
    {
        foreach (var obj in _objects)
        {
            var target = FindBindTarget(obj);
            if (target is null) continue;

            double unit = target.GetNumber(TargetUnitKey, obj.GetNumber(TargetUnitKey, 1.0));
            if (double.IsNaN(unit) || double.IsInfinity(unit) || unit <= 0) continue;

            double targetScale = target.Scale > 0 && !double.IsNaN(target.Scale) && !double.IsInfinity(target.Scale)
                ? target.Scale
                : 1.0;

            // 曲线的本地几何是"数学坐标"，缩放必须让 1 数学单位 = 目标的世界单位长
            obj.Scale = unit * targetScale;

            obj.RotationDegrees = NormalizeDegrees(
                target.RotationDegrees + obj.GetNumber(BindRelRotKey, 0));

            // 位置：把"相对偏移"从目标本地系搬到世界（偏移 0 时就是目标的原点）
            obj.Center = GfxTransform.ToWorld(target, new Point(
                obj.GetNumber(BindRelXKey, 0),
                obj.GetNumber(BindRelYKey, 0)));

            // 线宽也跟着目标：轴的粗细会随目标缩放变化，曲线得一样粗，
            // 否则"图像和坐标轴一样粗"只在没缩放过时成立。
            obj.LineWorldWidth = target.LineWorldWidth * targetScale;
        }

        RefreshSumObjects();
        SolveAttachments();
    }

    /// <summary>
    /// 附件解算（M15）：渲染器声明"我的位姿由别人决定"（<see cref="IGfxAttachmentSolver"/>，
    /// 电路导线两端绑元件接线柱）时，把全部对象的只读视图递给它解算，并写回位姿与数值参数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★ 与 bindTo（单目标绑定）同一套"宿主在重算窗口里写回"的做法：解算放在
    /// <see cref="RaiseChanged"/> 的路径上，任何一次集合 / 位姿 / 参数变动都会把导线叫醒 ——
    /// 漏掉一处的表现就是"元件挪走了，导线还钉在原地"。
    /// </para>
    /// <para>
    /// <b>只改字段、不抛事件</b>（在通知路径上，再抛就是递归）。
    /// <b>写回前逐项比对</b>：什么都没变就不动 LocalSize —— 视觉层的重建判据是
    /// "LocalSize 变了"，无意义重建会让拖动每帧白做一次视觉树操作。
    /// 但<b>真变了就必须重算 LocalSize</b>：导线长度存在 Numbers[halfLen] 里，
    /// 只改位姿不改尺寸，视觉层根本不会重建（RefreshSumObjects 踩过的同款坑）。
    /// </para>
    /// <para>
    /// 渲染器异常只损失它自己的跟随（记日志跳过），与 <see cref="BoardQuery.SnapToGfx"/> 同一政策。
    /// </para>
    /// </remarks>
    private void SolveAttachments()
    {
        // List<GfxObject> 经协方差直接当只读视图用（GfxObject : IGfxObjectRef）—— 零拷贝
        IReadOnlyList<IGfxObjectRef> board = _objects;

        foreach (var obj in _objects)
        {
            if (_catalog.Find(obj.Kind) is not IGfxAttachmentSolver solver) continue;

            GfxSolvedPose solved;
            try
            {
                if (!solver.TrySolve(obj, board, out solved)) continue;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"渲染器（{obj.Kind}）解算对象 {obj.Id} 的位姿时抛异常，已跳过："
                            + $"{ex.GetType().Name} {ex.Message}");
                continue;
            }

            bool changed = false;

            if ((obj.Center - solved.Center).Length > 1e-9)
            {
                obj.Center = solved.Center;
                changed = true;
            }

            double rotation = NormalizeDegrees(solved.RotationDegrees);
            if (Math.Abs(NormalizeDegrees(obj.RotationDegrees) - rotation) > 1e-9)
            {
                obj.RotationDegrees = rotation;
                changed = true;
            }

            if (solved.Numbers is not null)
            {
                foreach (var pair in solved.Numbers)
                {
                    if (obj.Numbers.TryGetValue(pair.Key, out double old)
                        && Math.Abs(old - pair.Value) <= 1e-9)
                    {
                        continue;
                    }

                    obj.Numbers[pair.Key] = pair.Value;
                    changed = true;
                }
            }

            if (changed)
            {
                obj.LocalSize = MeasureSize(obj);
            }
        }
    }

    /// <summary>
    /// 把所有"合力对象"重算一遍 —— 它们的几何取自<b>别人</b>（分矢量），任何一次变动都可能改变它们。
    /// </summary>
    /// <remarks>
    /// ★ 为什么必须放在 <see cref="RaiseChanged"/> 的路径上（经由 <see cref="RecomputeAttachments"/>）：
    /// 拖一根分矢量走，合力线得跟着动。而合力对象<b>自己</b>的位姿与参数一个字节都没变，
    /// 于是"对象参数变才重建几何"那条路永远不会被触发（<c>GfxObjectLayer.Sync</c> 只在
    /// 视觉缺失时重建）—— 表现成"分矢量拖走了，合力线还钉在原地"。
    /// <para>
    /// 唯一能在"别人变了"时被叫醒的地方，就是这里：全部对象都在手上。
    /// </para>
    /// <para>
    /// <b>只重算尺寸、不抛事件</b>（与上面的绑定重算同一个理由：避免递归）。
    /// 尺寸变了 ⇒ 视觉层下一帧自然会按新尺寸摆位；几何内容则由渲染器在
    /// <c>CreateVisual</c> 里重新读分矢量 —— 所以尺寸还必须真的会变，
    /// 否则视觉层根本不会重建（这就是"重算必须影响 LocalSize"的原因）。
    /// </para>
    /// </remarks>
    private void RefreshSumObjects()
    {
        foreach (var obj in _objects)
        {
            // 合力与矢量组都是"几何取自别人"的对象，任何一次集合变动都可能改变它们的尺寸。
            // 成员被拖走 ⇒ 并集/矢量和变了 ⇒ LocalSize 必须跟着变，
            // 否则视觉层收不到"几何签名变了"的信号，屏幕上就是旧线、旧框。
            bool derived = !string.IsNullOrEmpty(obj.GetText(SumOfKey, ""))
                        || !string.IsNullOrEmpty(obj.GetText(GroupOfKey, ""));

            if (!derived) continue;

            obj.LocalSize = MeasureSize(obj);
        }
    }

    /// <summary>
    /// 用渲染器量出对象的本地尺寸。
    /// </summary>
    /// <remarks>
    /// 认不出 Kind ⇒ 占位尺寸（这样"缺插件的对象"照样能选中、拖动、删除，用户能把它挪开）。
    /// 渲染器抛异常 ⇒ 同样退回占位尺寸并记日志，<b>不</b>向上抛。
    /// </remarks>
    private Size MeasureSize(GfxObject obj)
    {
        var renderer = _catalog.Find(obj.Kind);
        if (renderer is null) return GfxPlaceholder.Size;

        // ★ 先递画布快照再量：合力这类"取决于别人"的图形，不算这一步会拿不到分矢量。
        //   递的是"当前已入库的对象"，所以 Add 的瞬间新对象还不在里面 —— 而它本来也不是自己的分矢量。
        FeedBoard(renderer);

        try
        {
            var size = renderer.Measure(obj);

            if (size.Width <= 0 || size.Height <= 0 || double.IsNaN(size.Width) || double.IsNaN(size.Height)
                || double.IsInfinity(size.Width) || double.IsInfinity(size.Height))
            {
                AppLog.Warn($"渲染器 {renderer.GetType().Name} 对 {obj.Kind} 量出了非法尺寸"
                            + $"（{size.Width}×{size.Height}），改用占位尺寸。");
                return GfxPlaceholder.Size;
            }

            return size;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"渲染器 {renderer.GetType().Name}（{obj.Kind}）量尺寸时抛异常，改用占位尺寸："
                        + $"{ex.GetType().Name} {ex.Message}");
            return GfxPlaceholder.Size;
        }
    }

    /// <summary>
    /// 把"当前画布上的全部对象"递给<b>需要看别人</b>的渲染器。
    /// </summary>
    /// <remarks>
    /// ★ 只有实现了 <see cref="IGfxBoardAwareRenderer"/> 的渲染器会收到（本工程目前只有合力箭头）。
    /// 这是个<b>可选</b>能力：不实现它的渲染器完全不受影响，宿主连一次多余调用都不会发生。
    /// <para>
    /// 用 <c>is</c> 判断而不是反射，也不给契约加"父对象"概念 ——
    /// 后者会动到已分发插件的类型布局，而本工程的铁律是"契约只加新接口"。
    /// </para>
    /// <para>
    /// <b>传 <c>null</c> 表示没有画布上下文</b>（离屏渲染的情形）：渲染器应当降级成
    /// "用自己存档里的冗余参数画"，而不是抛异常。
    /// </para>
    /// </remarks>
    private void FeedBoard(IGfxObjectRenderer renderer)
    {
        if (renderer is not IGfxBoardAwareRenderer aware) return;

        try
        {
            aware.SetBoard(Objects);
        }
        catch (Exception ex)
        {
            // 渲染器是插件代码，它出错只能损失它自己（与 Measure/CreateVisual 同一个政策）
            AppLog.Warn($"渲染器 {renderer.GetType().Name} 接收画布快照时抛异常，已忽略："
                        + $"{ex.GetType().Name} {ex.Message}");
        }
    }

    private static double NormalizeDegrees(double degrees)
    {
        double value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value == 0 ? 0 : value;
    }

    /// <remarks>
    /// <b>先重算绑定、再通知</b>：视觉层收到 <see cref="Changed"/> 就立刻按位姿摆位，
    /// 若重算排在通知之后，跟随别人的图形会永远慢一帧。
    /// </remarks>
    private void RaiseChanged()
    {
        RecomputeAttachments();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);
}
