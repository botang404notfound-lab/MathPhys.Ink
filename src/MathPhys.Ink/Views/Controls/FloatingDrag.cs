using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 浮动元素拖动助手（M13 S4 引入；M16 S1 改为渲染变换驱动）：
/// 给一个"把手"元素挂触摸/鼠标双链路的捕获式拖动，
/// 被移动元素的左上角位置以容器坐标回调（已按容器尺寸 clamp）。
/// 浮动工具栏与计算器面板共用。
/// </summary>
/// <remarks>
/// 只有把手响应拖动 —— 面板里的按钮点击与拖动天然分离，触屏误拖率为零。
/// 判据与悬浮球同源：位移 &gt; 6 DIP 才算拖，按住不动不算。
/// <para>
/// <b>M16 性能改法</b>：拖动过程只改 <see cref="TranslateTransform"/>（RenderTransform，
/// 纯渲染队列，零布局），松手才把最终位置写回 Margin。以前每次 TouchMove 改 Margin，
/// 真机 213Hz 触摸率下每帧触发容器全量布局（工具栏里 40 来个按钮全部重新测量）——
/// 这就是"拖动很卡"的根因。Margin 仍是位置的唯一事实（持久化 / 窗口缩放重摆都读它），
/// 变换只是拖动期间的临时视觉偏移，二者在松手那一刻对齐。
/// </para>
/// <para>
/// <b>M19 后修闪烁</b>：拖动读数一律以 <see cref="ResolveReference"/> 给出的
/// <b>目标的父级</b>为基准，绝不以把手（目标的子元素）为基准 —— 缘由见该方法上的说明。
/// </para>
/// </remarks>
public static class FloatingDrag
{
    /// <summary>
    /// 拖动坐标的基准元素 = 目标的父级（也就是 Margin 所在的坐标系）；父级取不到时退回容器。
    /// </summary>
    /// <remarks>
    /// ★ <b>为什么不能以把手（或目标自身）为基准</b>：<c>GetPosition</c> /
    /// <c>GetTouchPoint</c> 的量距是在<b>指定元素的本地坐标系</b>里做的，而这个坐标系包含
    /// 该元素及其祖先的 <c>RenderTransform</c>。拖动中我们刚给目标挂上
    /// <see cref="TranslateTransform"/>，把手里的读数就被这次位移抵消掉一部分：
    /// <code>
    /// 读数 = 真位移 − 当前位移   ⇒   下一帧位移 = 读数 = 真位移 − 当前位移
    /// </code>
    /// 这是「位置 → 读数 → 位置」的正反馈环：真位移恒定时它的解就是两点之间来回翻
    /// （实测序列 140 0 140 0 140 0 …）。观感 = <b>拖动时面板剧烈闪烁、不跟手</b>，
    /// 且松手位置有一半概率落回起点。本机真窗口实测（真光标 + 真 GetPosition）：
    /// 光标不动、给目标挂 <c>TranslateTransform(60,30)</c> 后，<c>GetPosition(把手)</c>
    /// 当场偏移 (−60,−30)，而 <c>GetPosition(容器)</c> 纹丝不动。
    /// <para>
    /// 以目标的父级为基准天然免疫：父级不在这次拖动的影响范围内。
    /// 悬浮球（<see cref="FloatingBall"/>）从一开始就以宿主 Grid 为基准，所以它没有这个毛病；
    /// 两处口径从此一致。
    /// </para>
    /// </remarks>
    public static FrameworkElement ResolveReference(FrameworkElement target, FrameworkElement container)
        => target.Parent as FrameworkElement ?? container;

    /// <summary>
    /// 挂拖动。把手上的按下/移动/抬起（触摸与鼠标各一条链路）驱动
    /// <paramref name="target"/> 的位置；拖动中走渲染变换，松手落 Margin 并回调
    /// <paramref name="onCommitted"/>（容器是 clamp 的参照）。
    /// </summary>
    public static void Attach(
        FrameworkElement handle,
        FrameworkElement target,
        FrameworkElement container,
        Action<Point>? onCommitted = null)
    {
        var drag = new DragContext(handle, target, container, onCommitted);

        handle.TouchDown += drag.OnPress;
        handle.TouchMove += drag.OnMove;
        handle.TouchUp += drag.OnRelease;
        handle.LostTouchCapture += (_, _) => drag.Abort();
        handle.MouseLeftButtonDown += drag.OnPress;
        handle.MouseLeftButtonUp += drag.OnRelease;
        handle.MouseMove += drag.OnMove;
        handle.LostMouseCapture += (_, _) => drag.Abort();
    }

    /// <summary>一次拖动的全部状态（按下起点 + 面板起点 + 是否成行）。</summary>
    private sealed class DragContext
    {
        /// <summary>位移超过这个值（DIP）才算"拖"而不是"点"。</summary>
        private const double DragThreshold = 6.0;

        private readonly FrameworkElement _handle;
        private readonly FrameworkElement _target;
        private readonly FrameworkElement _container;
        private readonly Action<Point>? _onCommitted;

        private bool _dragging;
        private Point _pressPoint;
        private Point _startMargin;
        private Point _current;

        /// <summary>本次拖动的坐标基准（按下那一刻定下，整段手势不换）。</summary>
        private FrameworkElement _reference;

        /// <summary>拖动期间的临时视觉偏移（松手清零，Margin 接管）。</summary>
        private TranslateTransform? _translate;

        public DragContext(FrameworkElement handle, FrameworkElement target, FrameworkElement container,
            Action<Point>? onCommitted)
        {
            _handle = handle;
            _target = target;
            _container = container;
            _onCommitted = onCommitted;
            _reference = ResolveReference(target, container);
        }

        internal void OnPress(object? sender, InputEventArgs e)
        {
            // ★ 基准每次按下重取一次（挂法由宿主决定，可能刚换过父级），但整段手势内必须固定：
            //   起点与移动点若量在不同的坐标系里，拖动会当场错位。
            _reference = ResolveReference(_target, _container);

            if (e is TouchEventArgs touch)
            {
                _pressPoint = touch.GetTouchPoint(_reference).Position;
                _handle.CaptureTouch(touch.TouchDevice);
            }
            else
            {
                _pressPoint = ((MouseEventArgs)e).GetPosition(_reference);
                _handle.CaptureMouse();
            }

            _startMargin = new Point(_target.Margin.Left, _target.Margin.Top);
            _dragging = false;
            e.Handled = true;
        }

        internal void OnMove(object? sender, InputEventArgs e)
        {
            bool pressed = e is TouchEventArgs
                ? _handle.AreAnyTouchesCaptured
                : _handle.IsMouseCaptured;
            if (!pressed) return;

            // ★ 量距基准 = 目标的父级（不受本次拖动影响）；用把手量的话，读数会被目标
            //   自己的位移抵消，形成正反馈环（见 ResolveReference 的说明）。
            var point = e is TouchEventArgs touch
                ? touch.GetTouchPoint(_reference).Position
                : ((MouseEventArgs)e).GetPosition(_reference);

            if (!_dragging)
            {
                if ((point - _pressPoint).Length < DragThreshold) return;
                _dragging = true;
                BeginDragVisual();
            }

            _current = Clamp(new Point(
                _startMargin.X + point.X - _pressPoint.X,
                _startMargin.Y + point.Y - _pressPoint.Y));

            // ★ 只动渲染变换：不触发 Measure/Arrange，213Hz 触摸率下也流畅
            if (_translate is not null)
            {
                _translate.X = _current.X - _startMargin.X;
                _translate.Y = _current.Y - _startMargin.Y;
            }
            e.Handled = true;
        }

        internal void OnRelease(object? sender, InputEventArgs e)
        {
            bool wasDragging = _dragging;
            if (wasDragging)
            {
                // 松手才落 Margin（位置的唯一事实），变换与缓存归零 —— 二者在此刻对齐，画面不跳
                _target.Margin = new Thickness(_current.X, _current.Y, 0, 0);
                EndDragVisual();
                _dragging = false;
                _onCommitted?.Invoke(_current);
            }

            if (e is TouchEventArgs touchRelease) _handle.ReleaseTouchCapture(touchRelease.TouchDevice);
            else _handle.ReleaseMouseCapture();

            e.Handled = true;
        }

        /// <summary>捕获丢失（被系统打断）：视觉退回 Margin 位置，不提交。</summary>
        internal void Abort()
        {
            EndDragVisual();
            _dragging = false;
        }

        /// <summary>
        /// 拖动开始：先把目标整体光栅化为位图缓存，再挂平移变换。
        /// 工具栏有 40 来个按钮 + 文字，M16 虽已改 RenderTransform（不再每帧全量布局），
        /// 但大元素在 213Hz 触摸率下仍要每帧重绘矢量内容 —— 一体机集显/软件回退时就是"拖动卡"的根因。
        /// BitmapCache 让平移只移动一张位图（GPU 合成），UI 线程零矢量重绘。
        /// </summary>
        private void BeginDragVisual()
        {
            EnsureTranslate();
            double scale = 1.0;
            try { scale = Math.Max(VisualTreeHelper.GetDpi(_target).DpiScaleX, 1.0); }
            catch { /* 取不到 DPI 就按 1 走，至少不崩 */ }
            _target.CacheMode = new BitmapCache { RenderAtScale = scale };
        }

        /// <summary>拖动结束：变换归零（Margin 已接管位置），并撤掉位图缓存恢复矢量清晰渲染。</summary>
        private void EndDragVisual()
        {
            ClearTranslate();
            // 静止时不需要缓存：避免长期占用显存，也避免高 DPI 下长期模糊。
            _target.CacheMode = null;
        }

        /// <summary>拿到（或造出）目标上的平移变换；已有非平移变换时不覆盖 —— 现状两种调用都没有变换。</summary>
        private void EnsureTranslate()
        {
            if (_target.RenderTransform is TranslateTransform existing)
            {
                _translate = existing;
                return;
            }

            _translate = new TranslateTransform();
            _target.RenderTransform = _translate;
        }

        private void ClearTranslate()
        {
            if (_translate is not null)
            {
                _translate.X = 0;
                _translate.Y = 0;
            }
        }

        /// <summary>clamp 到容器内（面板比容器还大时取 0，宁可贴角也不出界）。</summary>
        private Point Clamp(Point point) => new(
            Math.Clamp(point.X, 0.0, Math.Max(_container.ActualWidth - _target.ActualWidth, 0.0)),
            Math.Clamp(point.Y, 0.0, Math.Max(_container.ActualHeight - _target.ActualHeight, 0.0)));
    }
}
