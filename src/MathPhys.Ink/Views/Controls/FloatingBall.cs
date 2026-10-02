using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Design;

namespace MathPhys.Ink.Views.Controls;

/// <summary>
/// 悬浮球（M12 引入，M13 重构）：工具栏隐藏时（或 always 档）的常驻快捷入口。
/// </summary>
/// <remarks>
/// <para>
/// 结构：一个铺满宿主的<b>透明 Grid</b>（Background=null ⇒ 空白区不截胡输入），
/// 里面一颗 48×48 圆球，位置由 <see cref="XRatio"/> / <see cref="YRatio"/>
/// 两个 0~1 的比例值决定 —— <b>画布内任意位置</b>（M13 起不再贴右缘）。
/// 比例存取让跨分辨率还原天然无漂移。
/// </para>
/// <para>
/// 菜单是 <see cref="Popup"/>（M13 起不再内嵌 StackPanel）：
/// 球拖到画布中央时菜单朝左弹出，越界由 WPF 自动翻转到屏幕内。
/// </para>
/// <para>
/// ★ 输入归属：球是画布<b>之上</b>的独立元素，命中测试天然先于墨迹层 ——
/// 球上的触摸不会落墨，这是元素树的既有语义，不写一行拦截代码；真机冒烟会钉住它。
/// 拖动与点击的判据是位移 &gt; 6 DIP：按住不动松手 = 点按（开/关菜单），拖了 = 挪位置。
/// </para>
/// </remarks>
public sealed class FloatingBall : Grid
{
    /// <summary>球直径（DIP）。取 <c>Touch.Large</c> 档 —— 球是唯一的，按得住比省地方重要。</summary>
    public const double BallSize = 48.0;

    /// <summary>菜单列宽度。</summary>
    public const double MenuWidth = 156.0;

    /// <summary>菜单项高度（≥ Touch.Min=40，一体机的手指按得中）。</summary>
    private const double MenuItemHeight = 42.0;

    /// <summary>拖动超过这个位移（DIP）就算"拖"而不是"点"。</summary>
    private const double DragThreshold = 6.0;

    private readonly Border _ball;
    private readonly Popup _menuPopup;
    private readonly StackPanel _menu;

    private bool _dragging;
    private Point _pressPoint;
    private double _xRatio = 0.9;
    private double _yRatio = 0.5;

    // M18 性能改法（照 M16 FloatingDrag 的先例）：拖动过程只改渲染变换，松手才落 Margin。
    // 以前每次 TouchMove 改 Margin，真机 213~281Hz 触摸率下每帧触发容器全量布局
    // （FloatingBall 铺满宿主，布局失效会传播到 CanvasOverlayGrid，连带重测工具栏与画布）——
    // 这就是"悬浮球拖动很卡"的根因。
    private TranslateTransform? _translate;   // 拖动期间的临时视觉偏移（纯渲染队列，零布局）
    private Point _startMargin;               // 按下那一刻球的 Margin 基准（左上角，容器坐标）
    private Point _current;                   // 拖动中的目标位置（容器坐标，左上基准）

    /// <summary>球中心在容器宽度上的比例位置（0~1）。</summary>
    public double XRatio
    {
        get => _xRatio;
        set
        {
            _xRatio = Math.Clamp(double.IsNaN(value) ? 0.5 : value, 0.0, 1.0);
            ApplyPosition();
        }
    }

    /// <summary>球中心在容器高度上的比例位置（0~1）。</summary>
    public double YRatio
    {
        get => _yRatio;
        set
        {
            _yRatio = Math.Clamp(double.IsNaN(value) ? 0.5 : value, 0.0, 1.0);
            ApplyPosition();
        }
    }

    /// <summary>球被拖到新位置（宿主据此持久化 X / Y 两个比例）。</summary>
    public event EventHandler<(double X, double Y)>? PositionCommitted;

    /// <summary>球被单击（宿主据此决定开/关菜单或执行默认动作）。</summary>
    public event EventHandler? BallClicked;

    public FloatingBall()
    {
        Background = null;                       // 空白区域不截胡画布输入
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = true;

        // 球体：三颗点（自绘，不依赖字体符号）；位置全靠 Margin（左上基准）
        _ball = new Border
        {
            Width = BallSize,
            Height = BallSize,
            CornerRadius = new CornerRadius(BallSize / 2.0),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Opacity = 0.92,
            Child = BuildDotsGlyph(),
        }
        // ★ M20 步 2b：接令牌而不是取快照（换肤后球会留着旧主题的主色）
        .FollowTheme(Border.BackgroundProperty, "Ui.Accent")
        .FollowTheme(Border.BorderBrushProperty, "Ui.OverlayBorder");

        // 触摸在球上就归球（Handled 掐断触摸 → 鼠标提升，避免一次触摸走两套处理）
        _ball.TouchDown += OnBallPress;
        _ball.TouchMove += OnBallDrag;
        _ball.TouchUp += OnBallRelease;
        _ball.MouseLeftButtonDown += OnBallPress;
        _ball.MouseLeftButtonUp += OnBallRelease;
        _ball.MouseMove += OnBallDrag;
        _ball.LostMouseCapture += (_, _) =>
        {
            // 捕获被抢走（弹窗/系统手势）：视觉退回 Margin 位置，变换归零，避免悬在半空
            if (_dragging)
            {
                _dragging = false;
                ClearTranslate();
            }
        };

        Children.Add(_ball);

        // 菜单（M13）：Popup 承载 —— 球在哪都能展开，越界自动翻回屏幕内
        _menu = new StackPanel { Width = MenuWidth };
        _menuPopup = new Popup
        {
            PlacementTarget = _ball,
            Placement = PlacementMode.Left,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 8, 8),
                Child = _menu,
            }
            .FollowTheme(Border.BackgroundProperty, "Ui.OverlayPanel")
            .FollowTheme(Border.BorderBrushProperty, "Ui.OverlayBorder"),
        };

        SizeChanged += (_, _) => ApplyPosition();
        Loaded += (_, _) => ApplyPosition();
    }

    /// <summary>设置菜单项（宿主装配一次即可；M13 起菜单固定，不再随状态重建）。</summary>
    public void SetMenuItems(IReadOnlyList<(string Label, Action Run)> items)
    {
        _menu.Children.Clear();

        foreach (var (label, run) in items)
        {
            var item = new Button
            {
                Content = label,
                Style = (Style)FindResource("Style.ToolbarButton"),
                Height = MenuItemHeight,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // 菜单要左对齐才像菜单（工具栏那套是居中）
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = Tokens.Number("Text.Label"),
                FontWeight = FontWeights.SemiBold,
            };
            item.Click += (_, _) =>
            {
                CloseMenu();
                run();
            };
            _menu.Children.Add(item);
        }
    }

    /// <summary>开 / 关菜单。</summary>
    public void ToggleMenu()
    {
        if (_menuPopup.IsOpen) CloseMenu();
        else OpenMenu();
    }

    public void OpenMenu()
    {
        _menuPopup.IsOpen = true;
    }

    public void CloseMenu()
    {
        _menuPopup.IsOpen = false;
    }

    /// <summary>外部（窗口尺寸变化 / 工具栏显隐）通知重算位置。</summary>
    public void ReapplyPosition() => ApplyPosition();

    // ---------------------------------------------------------------- 按下 / 拖动 / 抬起

    private void OnBallPress(object? sender, InputEventArgs e)
    {
        _pressPoint = e is TouchEventArgs touch
            ? touch.GetTouchPoint(this).Position
            : ((MouseEventArgs)e).GetPosition(this);
        _dragging = false;

        // M18：记下按下那一刻的 Margin 基准，拖动位移都相对它算（松手前 Margin 不动）
        _startMargin = new Point(_ball.Margin.Left, _ball.Margin.Top);

        // 触摸与鼠标两条链路各自捕获：触摸按设备捕获，鼠标按鼠标捕获
        if (e is TouchEventArgs touchArgs)
        {
            _ball.CaptureTouch(touchArgs.TouchDevice);
        }
        else
        {
            _ball.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnBallDrag(object? sender, InputEventArgs e)
    {
        bool pressed = e is TouchEventArgs
            ? _ball.AreAnyTouchesCaptured || _dragging
            : _ball.IsMouseCaptured;

        if (!pressed) return;

        var point = e is TouchEventArgs touch
            ? touch.GetTouchPoint(this).Position
            : ((MouseEventArgs)e).GetPosition(this);

        if (!_dragging)
        {
            if ((point - _pressPoint).Length < DragThreshold) return;
            _dragging = true;
            CloseMenu();          // M18：拖动开始即收菜单 —— 分层 HWND 不再跟着球每帧重定位重合成
            EnsureTranslate();
        }

        // M13：双自由度 —— 球心跟随手指，clamp 在画布内。
        // M18：只算目标位置，绝不碰 XRatio/YRatio/Margin（布局零失效），
        // 位移全部体现在渲染变换上 —— 213~281Hz 触摸率下也流畅。
        double availX = Math.Max(ActualWidth - BallSize, 0.0);
        double availY = Math.Max(ActualHeight - BallSize, 0.0);
        _current = new Point(
            Math.Clamp(point.X - BallSize / 2.0, 0.0, Math.Max(availX, 1.0)),
            Math.Clamp(point.Y - BallSize / 2.0, 0.0, Math.Max(availY, 1.0)));

        _translate!.X = _current.X - _startMargin.X;
        _translate!.Y = _current.Y - _startMargin.Y;

        e.Handled = true;
    }

    private void OnBallRelease(object? sender, InputEventArgs e)
    {
        bool wasDragging = _dragging;
        _dragging = false;
        _ball.ReleaseMouseCapture();

        if (!wasDragging)
        {
            // 按住没动 = 点按
            BallClicked?.Invoke(this, EventArgs.Empty);
            ToggleMenu();
        }
        else
        {
            // M18：松手才落 Margin（位置的唯一事实），变换归零 —— 此刻二者精确对齐，画面不跳
            _ball.Margin = new Thickness(_current.X, _current.Y, 0, 0);
            ClearTranslate();

            // 由最终 Margin 反推比例（Margin 是唯一事实，比例只是存档口径）
            double availX = Math.Max(ActualWidth - BallSize, 1.0);
            double availY = Math.Max(ActualHeight - BallSize, 1.0);
            _xRatio = Math.Clamp(_current.X / availX, 0.0, 1.0);
            _yRatio = Math.Clamp(_current.Y / availY, 0.0, 1.0);

            // 拖完了：位置交给宿主持久化（每次拖动只写一次盘，无每帧 IO）
            PositionCommitted?.Invoke(this, (_xRatio, _yRatio));
        }

        e.Handled = true;
    }

    // ---------------------------------------------------------------- 布局

    /// <summary>按比例把球摆到容器内的任意位置（拖动中不执行 —— M18 拖动位移走渲染变换，布局基准备不动）。</summary>
    private void ApplyPosition()
    {
        if (_dragging) return;

        double availX = Math.Max(ActualWidth - BallSize, 0.0);
        double availY = Math.Max(ActualHeight - BallSize, 0.0);

        _ball.Margin = new Thickness(_xRatio * availX, _yRatio * availY, 0, 0);
    }

    /// <summary>确保球挂了渲染变换（M18 拖动专用）。</summary>
    private void EnsureTranslate()
    {
        if (_ball.RenderTransform is TranslateTransform existing)
        {
            _translate = existing;
            return;
        }

        _translate = new TranslateTransform();
        _ball.RenderTransform = _translate;
    }

    /// <summary>渲染变换归零（松手后 Margin 已是最终位置，变换必须清零避免双重偏移）。</summary>
    private void ClearTranslate()
    {
        if (_translate is not null)
        {
            _translate.X = 0;
            _translate.Y = 0;
        }

        _translate = null;
    }

    /// <summary>球面图形：三颗点（垂直居中的一行）。</summary>
    private static UIElement BuildDotsGlyph()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (int i = 0; i < 3; i++)
        {
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(3, 0, 3, 0),
            }
            .FollowTheme(System.Windows.Shapes.Shape.FillProperty, "Ui.OnAccent"));
        }

        return row;
    }
}
