using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MathPhys.Ink.Plugins;
using MathPhys.Ink.Plugin.NativeSim.Kinematics;

namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// 原生物理仿真窗（单摆 / 弹簧振子 / 斜面）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是一个独立窗口，而不是画布上的一个图形对象。</b>仿真要 60 Hz 重绘，
/// 而画布上的图形对象<b>没有</b>"每帧重绘"的推送通道：硬塞进去会变成每帧一次
/// <c>UpdateNumbers</c> —— 又贵、又会把每一帧都写进撤销栈。
/// 独立窗口是原生 WPF 视觉，代价只有一次（导出时截一张图）。
/// </para>
/// <para>
/// <b>为什么不做成"模式"。</b>本窗口由 <see cref="NativeSimTool"/>（瞬态工具）打开，
/// 打开之后控制权立刻交回用户原来那支笔 —— 于是窗口开着的时候，
/// 老师照样能在没被挡住的地方写字。窗口关掉也不需要"复位输入"，
/// 因为从头到尾就没有谁把输入拿走过（这是与 Web 面板最大的区别：
/// 那边是 HWND 全屏窗，必须靠 <c>EnterPanelMode</c> 互斥）。
/// </para>
/// <para>
/// 「网页仿真」入口的可用性靠 <see cref="IWebPanelBridge.IsAvailable"/> 在<b>按下之前</b>决定：
/// 轻量版裁掉 Web 插件后，这两颗按钮是<b>置灰 + 一句中文原因</b>，
/// 而不是"点下去没反应"。
/// </para>
/// </remarks>
internal sealed class SimWindow : Window
{
    /// <summary>导出的像素放大倍数。</summary>
    /// <remarks>
    /// 1.5 是"落到卷面上放大看仍然清晰"与"PNG 字节数不至于太夸张"之间的平衡点。
    /// 960×560 的画布 ⇒ 1440×840 的图，约 0.3~0.9 MB。
    /// </remarks>
    private const double ExportScale = 1.5;

    /// <summary>一帧最多推进的仿真时间（s）。</summary>
    /// <remarks>
    /// ★ 必须有这条闸门：窗口被窗口管理器遮挡时 <c>CompositionTarget.Rendering</c> 会停发，
    /// 恢复时那一帧的间隔可能是好几秒。照实推进，屏幕上就是"仿真突然跳到很远的地方去了"。
    /// </remarks>
    private const double MaxFrameSeconds = 0.1;

    private readonly IWebPanelBridge? _webSim;
    private readonly Func<byte[], string, string?> _addImage;
    private readonly Action<string> _report;
    private readonly bool _canExport;

    private readonly PendulumModel _pendulum = new();
    private readonly SpringModel _spring = new();
    private readonly InclineModel _incline = new();

    private readonly SimSurface _surface;
    private readonly StackPanel _parameterPanel = new();
    private readonly StackPanel _webPanel = new();
    private readonly TextBlock _readout = new();
    private readonly TextBlock _hint = new();
    private readonly Dictionary<SimKind, Button> _kindButtons = new();
    private readonly Button _pauseButton = new();

    private SimKind _kind = SimKind.Pendulum;
    private bool _paused;
    private bool _hooked;
    private TimeSpan _lastFrame;

    /// <summary>缓存的"周期读数"：实测周期很贵（上万步积分），只在参数变化时算一次。</summary>
    private string _periodText = string.Empty;

    /// <param name="webSim">通用 Web 面板通道；<c>null</c> 表示这个版本没有网页仿真。</param>
    /// <param name="addImage">把 PNG 落到卷面上的委托（返回对象 Id；失败给 <c>null</c>）。</param>
    /// <param name="report">往状态栏写一句话。</param>
    /// <param name="canExport">
    /// 宿主有没有「图形对象通道」。没有时「导出到白板」置灰并说明原因——
    /// 按下去毫无反应是故障，灰着并说清楚原因是「少个便利」。
    /// </param>
    public SimWindow(
        IWebPanelBridge? webSim,
        Func<byte[], string, string?> addImage,
        Action<string> report,
        bool canExport = true)
    {
        _webSim = webSim;
        _addImage = addImage ?? throw new ArgumentNullException(nameof(addImage));
        _report = report ?? (_ => { });
        _canExport = canExport;

        Title = "物理仿真";
        Width = 1000;
        Height = 660;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        Background = SimPalette.Background;

        _surface = new SimSurface(this);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 顶栏：模型切换 + 动作 ----
        var header = new WrapPanel { Margin = new Thickness(14, 12, 14, 6) };
        foreach (var kind in SimKindInfo.All)
        {
            var button = new Button
            {
                Content = SimKindInfo.DisplayName(kind),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 6),
                MinWidth = 92,
            };
            button.ToolTip = SimKindInfo.Summary(kind);
            SimKind captured = kind;
            button.Click += (_, _) => SwitchKind(captured);
            _kindButtons[kind] = button;
            header.Children.Add(button);
        }

        header.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4, 12, 8), Background = SimPalette.Muted });

        _pauseButton.Content = "暂停";
        _pauseButton.FontSize = 15;
        _pauseButton.Padding = new Thickness(16, 6, 16, 6);
        _pauseButton.Margin = new Thickness(0, 0, 8, 6);
        _pauseButton.ToolTip = "暂停 / 继续（暂停后可以慢慢讲，滑杆照常可调）";
        _pauseButton.Click += (_, _) => TogglePause();
        header.Children.Add(_pauseButton);

        var reset = MakeAction("重置", "回到初始状态（时间归零）", () =>
        {
            ResetCurrent();
            RefreshPeriodText();
            UpdateReadout();
            _surface.InvalidateVisual();
        });
        header.Children.Add(reset);

        var export = MakeAction("导出到白板", "把当前画面落成卷面上的一张图（可拖动、可缩放到题目旁边）", OnExport);
        if (!_canExport)
        {
            export.IsEnabled = false;
            export.ToolTip = "当前程序不支持图形对象，画面没处可放（仿真照常可看）";
            ToolTipService.SetShowOnDisabled(export, true);
        }

        header.Children.Add(export);

        var close = MakeAction("关闭", "关闭仿真窗（Esc 同效）", Close);
        header.Children.Add(close);

        Grid.SetRow(header, 0);
        Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);

        // ---- 左侧：画布 ----
        var surfaceHost = new Border
        {
            Margin = new Thickness(14, 0, 8, 8),
            BorderBrush = SimPalette.Muted,
            BorderThickness = new Thickness(1),
            Background = SimPalette.Background,
            Child = _surface,
        };
        Grid.SetRow(surfaceHost, 1);
        Grid.SetColumn(surfaceHost, 0);
        grid.Children.Add(surfaceHost);

        // ---- 右侧：参数 + 读数 + 网页入口 ----
        _readout.FontSize = 14;
        _readout.FontFamily = new FontFamily("Microsoft YaHei");
        _readout.TextWrapping = TextWrapping.Wrap;
        _readout.Foreground = SimPalette.Ink;
        _readout.Margin = new Thickness(0, 4, 0, 10);

        var side = new StackPanel { Margin = new Thickness(4, 0, 14, 8) };
        side.Children.Add(MakeSectionTitle("参数"));
        side.Children.Add(_parameterPanel);
        side.Children.Add(MakeSectionTitle("读数"));
        side.Children.Add(_readout);
        side.Children.Add(MakeSectionTitle("网页仿真"));
        side.Children.Add(_webPanel);

        var sideScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = side,
        };
        Grid.SetRow(sideScroll, 1);
        Grid.SetColumn(sideScroll, 1);
        grid.Children.Add(sideScroll);

        // ---- 底部：提示 ----
        _hint.FontSize = 13;
        _hint.FontFamily = new FontFamily("Microsoft YaHei");
        _hint.Foreground = SimPalette.Muted;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(16, 0, 16, 10);
        _hint.Text = "仿真窗开着的时候，画布照样能写（本窗口只挡住它自己那一块）。"
                     + "「导出到白板」把当前画面落成卷面上的一张图，随后本窗自动关闭。";
        Grid.SetRow(_hint, 2);
        Grid.SetColumnSpan(_hint, 2);
        grid.Children.Add(_hint);

        Content = grid;

        BuildWebEntries();
        SwitchKind(SimKind.Pendulum);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };

        Loaded += (_, _) =>
        {
            if (_hooked) return;
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
            _lastFrame = TimeSpan.Zero;
        };

        Closed += (_, _) =>
        {
            if (!_hooked) return;
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        };
    }

    // ---------------------------------------------------------------- 模型

    /// <summary>
    /// 把当前模型摆回它的初始状态（摆角 / 振幅 / 初速度那一组值）。
    /// </summary>
    /// <remarks>
    /// ★ <b>切模型时必须调它。</b>三个模型的"当前位置"字段初值都是 0（摆角 0、位移 0、速度 0），
    /// 而"初始状态"是另一组值（初始摆角 20°、初始振幅 0.25 m）。只切 <c>_kind</c> 不重置，
    /// 表现就是「打开仿真窗，摆和弹簧一动不动地钉在最低点」——而右侧滑杆上的数字看着都对，
    /// 最容易被当成"坏了 / 没反应"。
    /// <para>
    /// 斜面其实不需要它也能自起动（<c>µ &lt; tan θ</c> 时加速度为正，一推进就动），
    /// 但三个模型统一走这一步，界面行为才一致：「按模型名 ⇒ 从它的初始状态开始演」。
    /// </para>
    /// <para>
    /// ★ 这里必须是 switch <b>语句</b>：<c>Reset()</c> 返回 <c>void</c>，
    /// 而 switch 表达式要求每个分支都有值 —— 写成表达式是编译不过的。
    /// </para>
    /// </remarks>
    private void ResetCurrent()
    {
        switch (CurrentModel())
        {
            case PendulumModel pendulum: pendulum.Reset(); break;
            case SpringModel spring: spring.Reset(); break;
            case InclineModel incline: incline.Reset(); break;
        }
    }

    /// <summary>当前选中的模型（给 <see cref="SimScene"/> 用，故是 <c>object</c>）。</summary>
    private object CurrentModel() => _kind switch
    {
        SimKind.Spring => _spring,
        SimKind.Incline => _incline,
        _ => _pendulum,
    };

    /// <summary>切模型：换参数滑杆、换读数、重算周期。</summary>
    private void SwitchKind(SimKind kind)
    {
        _kind = kind;
        _lastFrame = TimeSpan.Zero;   // 切换那一刻的时间差不可信，丢掉

        // ★ 必须重置：切模型只换 _kind 是不够的（理由见 ResetCurrent 的注释）
        ResetCurrent();

        foreach (var pair in _kindButtons)
        {
            bool active = pair.Key == kind;
            pair.Value.Background = active ? SimPalette.Accent : Brushes.Transparent;
            pair.Value.Foreground = active ? Brushes.White : SimPalette.Ink;
            pair.Value.BorderBrush = active ? SimPalette.Accent : SimPalette.Muted;
            pair.Value.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
        }

        BuildParameters();
        RefreshPeriodText();
        UpdateReadout();
        _surface.InvalidateVisual();
    }

    /// <summary>按当前模型重建参数滑杆（滑杆值的文字跟着走）。</summary>
    private void BuildParameters()
    {
        _parameterPanel.Children.Clear();

        switch (_kind)
        {
            case SimKind.Pendulum:
                AddSlider("摆长 L", " m", PendulumModel.MinLength, PendulumModel.MaxLength,
                          _pendulum.Length, 0.05, "F2", value => _pendulum.Length = value);
                AddSlider("重力 g", " m/s²", PendulumModel.MinGravity, PendulumModel.MaxGravity,
                          _pendulum.Gravity, 0.1, "F2", value => _pendulum.Gravity = value);
                AddSlider("初始摆角 θ₀", "°", PendulumModel.MinStartAngleDegrees, PendulumModel.MaxStartAngleDegrees,
                          _pendulum.StartAngleDegrees, 1.0, "F0", value => _pendulum.StartAngleDegrees = value);
                AddSlider("阻尼 b", " 1/s", PendulumModel.MinDamping, PendulumModel.MaxDamping,
                          _pendulum.Damping, 0.01, "F2", value => _pendulum.Damping = value);
                break;

            case SimKind.Spring:
                AddSlider("质量 m", " kg", SpringModel.MinMass, SpringModel.MaxMass,
                          _spring.Mass, 0.05, "F2", value => _spring.Mass = value);
                AddSlider("劲度系数 k", " N/m", SpringModel.MinStiffness, SpringModel.MaxStiffness,
                          _spring.Stiffness, 0.5, "F1", value => _spring.Stiffness = value);
                AddSlider("振幅 A", " m", SpringModel.MinStartDisplacement, SpringModel.MaxStartDisplacement,
                          _spring.StartDisplacement, 0.01, "F2", value => _spring.StartDisplacement = value);
                AddSlider("阻尼 b", " N·s/m", SpringModel.MinDamping, SpringModel.MaxDamping,
                          _spring.Damping, 0.02, "F2", value => _spring.Damping = value);
                break;

            default:
                AddSlider("倾角 θ", "°", InclineModel.MinAngleDegrees, InclineModel.MaxAngleDegrees,
                          _incline.AngleDegrees, 1.0, "F0", value => _incline.AngleDegrees = value);
                AddSlider("动摩擦 µ", string.Empty, InclineModel.MinFriction, InclineModel.MaxFriction,
                          _incline.Friction, 0.01, "F2", value => _incline.Friction = value);
                AddSlider("重力 g", " m/s²", 1.0, 20.0, _incline.Gravity, 0.1, "F2",
                          value => _incline.Gravity = value);
                AddSlider("初速度 v₀", " m/s", -InclineModel.MaxStartSpeed, InclineModel.MaxStartSpeed,
                          _incline.StartSpeed, 0.1, "F1", value => _incline.StartSpeed = value);
                break;
        }
    }

    /// <summary>一行参数 = 一个说明文字 + 一根滑杆；改值立即生效（并按需重算周期）。</summary>
    /// <remarks>
    /// ★ <b>改参数不自动重置。</b>这是刻意的：老师拧"重力 g"的时候，
    /// 想看的是"同一个摆到了月球上周期变长"，而不是"画面从头再来一遍"。
    /// 想重来按「重置」。
    /// </remarks>
    private void AddSlider(
        string label, string unit, double min, double max, double value,
        double tick, string format, Action<double> apply)
    {
        var caption = new TextBlock
        {
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = SimPalette.Ink,
            Margin = new Thickness(0, 8, 0, 2),
            Text = $"{label} = {value.ToString(format)}{unit}",
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            SmallChange = tick,
            LargeChange = tick * 10,
            TickFrequency = tick,
            IsSnapToTickEnabled = false,
        };

        slider.ValueChanged += (_, e) =>
        {
            apply(e.NewValue);
            caption.Text = $"{label} = {e.NewValue.ToString(format)}{unit}";
            RefreshPeriodText();
            UpdateReadout();
            _surface.InvalidateVisual();
        };

        _parameterPanel.Children.Add(caption);
        _parameterPanel.Children.Add(slider);
    }

    /// <summary>重算"周期"那一行的读数（实测周期很贵，不能每帧算）。</summary>
    private void RefreshPeriodText()
    {
        _periodText = _kind switch
        {
            SimKind.Pendulum =>
                $"小角周期 T₀ = {_pendulum.SmallAnglePeriod:F3} s"
                + $"　大角修正 = {_pendulum.CorrectedPeriod:F3} s"
                + $"　实测 = {_pendulum.EstimatePeriod():F3} s",
            SimKind.Spring =>
                $"周期 T = {_spring.Period:F3} s（与振幅无关）　实测 = {_spring.EstimatePeriod():F3} s",
            _ => $"临界摩擦 tan θ = {_incline.CriticalFriction:F3}"
                 + $"　临界倾角 = {_incline.CriticalAngleDegrees:F1}°",
        };
    }

    /// <summary>每帧刷新读数（都是纯计算，没有积分）。</summary>
    private void UpdateReadout()
    {
        string text = _kind switch
        {
            SimKind.Pendulum =>
                $"t = {_pendulum.Time:F2} s\n"
                + $"θ = {_pendulum.Angle * 180.0 / Math.PI:F1}°　ω = {_pendulum.AngularVelocity:F2} rad/s\n"
                + $"速率 = {Math.Abs(_pendulum.TangentialSpeed):F2} m/s\n"
                + $"E/m = {_pendulum.SpecificEnergy:F4} J/kg\n"
                + _periodText,

            SimKind.Spring =>
                $"t = {_spring.Time:F2} s\n"
                + $"x = {_spring.Displacement:F3} m　v = {_spring.Velocity:F3} m/s\n"
                + $"E = {_spring.Energy:F4} J\n"
                + $"静伸长 Δ = mg/k = {_spring.StaticExtension * 1000:F0} mm\n"
                + $"{_spring.DampingRegime}\n"
                + _periodText,

            _ =>
                $"t = {_incline.Time:F2} s\n"
                + $"a = {_incline.Acceleration:F2} m/s²\n"
                + $"v = {_incline.Speed:F2} m/s　s = {_incline.Travel:F2} m\n"
                + $"g·sinθ = {_incline.GravityAlong:F2}　µ·g·cosθ = {_incline.FrictionAlong:F2}\n"
                + _incline.DescribeMotion() + "\n"
                + _periodText,
        };

        _readout.Text = text;
    }

    // ---------------------------------------------------------------- 动画

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseButton.Content = _paused ? "继续" : "暂停";
        _lastFrame = TimeSpan.Zero;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;

        if (_lastFrame == TimeSpan.Zero)
        {
            _lastFrame = now;
            return;
        }

        double dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        if (_paused || dt <= 0) return;

        // ★ 掉帧闸门：间隔异常大时只推进一小步（见 MaxFrameSeconds 的说明）
        if (dt > MaxFrameSeconds) dt = MaxFrameSeconds;

        Advance(dt);
        UpdateReadout();
        _surface.InvalidateVisual();
    }

    private void Advance(double dt)
    {
        switch (_kind)
        {
            case SimKind.Pendulum:
                _pendulum.Advance(dt);
                break;

            case SimKind.Spring:
                _spring.Advance(dt);
                break;

            default:
                _incline.Advance(dt);

                // 滑出画面就回到起点重新来：课堂上这个演示要能一直循环下去，
                // 而不是"滑到底之后就不动了，得伸手去按重置"。
                if (_incline.Travel > SimScene.InclineMetersOnSlope)
                {
                    _incline.Reset();
                }

                break;
        }
    }

    // ---------------------------------------------------------------- 网页仿真入口

    /// <summary>把「网页仿真」入口摆出来（可用性在按下之前就决定）。</summary>
    private void BuildWebEntries()
    {
        _webPanel.Children.Clear();

        var bridge = _webSim;
        bool available = WebSimAvailability.IsUsable(bridge);

        // ★ 按板块分组渲染（M23 S6）：45 个 PhET 仿真不分组就是一堵按钮墙。
        //   GroupBy 保持首见顺序 —— 入口表本身已按「电路 → 力学 → …」排好。
        foreach (var group in WebSimEntries.All.GroupBy(e => e.Category))
        {
            if (group.Key.Length > 0 && group.Count() > 1)
            {
                _webPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 13.5,
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontWeight = FontWeights.Bold,
                    Foreground = SimPalette.Muted,
                    Margin = new Thickness(0, 8, 0, 4),
                });
            }

            foreach (var entry in group)
            {
            var button = new Button
            {
                Content = entry.Label,
                FontSize = 14,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = available,
                ToolTip = available ? entry.Tip : WebSimAvailability.Reason(bridge),
            };

            // ★ 禁用的按钮默认收不到鼠标事件、因而也不显示提示 ——
            //   而"为什么是灰的"恰恰是这时候最需要说清楚的一句话。
            ToolTipService.SetShowOnDisabled(button, true);

            WebSimEntry captured = entry;
            button.Click += (_, _) => OpenWebSim(captured);
            _webPanel.Children.Add(button);
            }
        }

        var note = new TextBlock
        {
            FontSize = 12.5,
            FontFamily = new FontFamily("Microsoft YaHei"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = available ? SimPalette.Muted : SimPalette.Warn,
            Text = available
                ? "网页仿真会用全屏面板打开（退出后回到白板）。它和左边的原生仿真是两套东西，参数互不影响。"
                : "网页仿真不可用：" + WebSimAvailability.Reason(bridge),
        };
        _webPanel.Children.Add(note);
    }

    private void OpenWebSim(WebSimEntry entry)
    {
        var bridge = _webSim;
        if (bridge is null || !bridge.IsAvailable)
        {
            _report("网页仿真" + WebSimAvailability.Reason(bridge));
            return;
        }

        // ★ 先关本窗、再开面板：本窗是 Topmost，而 Web 面板是 HWND 全屏窗。
        //   两个 Topmost 叠在一起时谁在上面由激活顺序决定 —— 那是没法保证的，
        //   而"面板被仿真窗挡了一半"在现场是没法解释的故障。
        Close();

        _report($"正在打开{entry.Label}…");
        _ = bridge.OpenAsync(entry.PageId);
    }

    // ---------------------------------------------------------------- 导出

    private void OnExport()
    {
        string caption = CaptionText();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        byte[] png;
        try
        {
            png = SimScene.RenderPng(
                _kind, CurrentModel(),
                _surface.ActualWidth, _surface.ActualHeight,
                ExportScale, dpi, caption);
        }
        catch (Exception ex)
        {
            _hint.Text = "导出失败：没能把画面渲染成图片（" + ex.Message + "）。可以再点一次「导出到白板」重试。";
            _hint.Foreground = SimPalette.Warn;
            return;
        }

        string label = SimKindInfo.DisplayName(_kind) + "仿真";
        string? objectId = _addImage(png, label);

        if (string.IsNullOrEmpty(objectId))
        {
            // 失败时**不关窗**：什么都没被破坏，老师按一下重试就是了
            _hint.Text = "画面截好了，但没能落到卷面上 —— 先确认已经打开了一份试卷，再点「导出到白板」。";
            _hint.Foreground = SimPalette.Warn;
            _report("物理仿真：画面截好了，但没能落到卷面上（先打开一份试卷再试一次）。");
            return;
        }

        _report($"物理仿真：{label}画面已落到卷面上（{png.Length / 1024} KB），"
                + "可以直接拖动或缩放到题目旁边");
        Close();
    }

    /// <summary>
    /// 印在导出图左上角的参数标题。
    /// </summary>
    /// <remarks>
    /// 它同时是"这张图是什么"的说明 —— 图印到卷面上之后，
    /// 师生只能看到画面，看不到滑杆。
    /// </remarks>
    private string CaptionText() => _kind switch
    {
        SimKind.Pendulum =>
            $"单摆仿真　L = {_pendulum.Length:F2} m　g = {_pendulum.Gravity:F2} m/s²"
            + $"　θ₀ = {_pendulum.StartAngleDegrees:F0}°"
            + (_pendulum.Damping > 0 ? $"　b = {_pendulum.Damping:F2} 1/s" : string.Empty),

        SimKind.Spring =>
            $"弹簧振子仿真　m = {_spring.Mass:F2} kg　k = {_spring.Stiffness:F0} N/m"
            + $"　A = {_spring.StartDisplacement:F2} m"
            + (_spring.Damping > 0 ? $"　b = {_spring.Damping:F2} N·s/m" : string.Empty),

        _ =>
            $"斜面仿真　θ = {_incline.AngleDegrees:F0}°　µ = {_incline.Friction:F2}"
            + $"　g = {_incline.Gravity:F2} m/s²"
            + (Math.Abs(_incline.StartSpeed) > 0 ? $"　v₀ = {_incline.StartSpeed:F1} m/s" : string.Empty),
    };

    // ---------------------------------------------------------------- 界面小件

    private static Button MakeAction(string text, string tip, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 6),
            ToolTip = tip,
        };

        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock MakeSectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        FontFamily = new FontFamily("Microsoft YaHei"),
        Foreground = SimPalette.Ink,
        Margin = new Thickness(0, 6, 0, 2),
    };

    /// <summary>画布本体：把绘制转交给 <see cref="SimScene"/>。</summary>
    /// <remarks>
    /// 尺寸由布局给（在 Grid 的星号列里 ⇒ 拿到的是格子尺寸，不是无穷）。
    /// <c>MeasureOverride</c> 里给无穷兜一个默认值，免得放进
    /// <c>StackPanel</c> 之类的容器时量出 <c>Infinity</c> 而炸掉布局。
    /// </remarks>
    private sealed class SimSurface : FrameworkElement
    {
        private readonly SimWindow _owner;

        internal SimSurface(SimWindow owner)
        {
            _owner = owner;
            ClipToBounds = true;
            MinWidth = 240;
            MinHeight = 180;
        }

        protected override Size MeasureOverride(Size availableSize)
            => new(
                double.IsInfinity(availableSize.Width) ? SimScene.DefaultSurfaceWidth : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? SimScene.DefaultSurfaceHeight : availableSize.Height);

        protected override void OnRender(DrawingContext drawingContext)
            => _owner.DrawScene(drawingContext, RenderSize);
    }

    /// <summary>给 <see cref="SimSurface"/> 的绘制入口。</summary>
    private void DrawScene(DrawingContext dc, Size size)
        => SimScene.Draw(dc, _kind, CurrentModel(), size,
                         VisualTreeHelper.GetDpi(this).PixelsPerDip, CaptionText());
}
