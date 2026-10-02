using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using MathPhys.Ink.Plugin.FunctionPlot.Expr;
using MathPhys.Ink.Plugins;

namespace MathPhys.Ink.Plugin.FunctionPlot;

/// <summary>
/// 函数输入弹窗（MVP 形态）：自定义键盘拼写表达式 + 参数滑块 + 范围预设 + 实时预览。
/// </summary>
/// <remarks>
/// 为什么是弹窗而不是宿主参数面板：Step 4 选了<b>最小侵入</b>方案 —— 宿主零改动、
/// 契约零改动，输入交互完全在插件内部完成。一体机上全部用大按钮点按，不触发系统软键盘。
/// </remarks>
public sealed class FunctionInputWindow : Window
{
    private const string Back = "⌫";
    private const string Clear = "清空";

    private readonly Action<FunctionSpec> _onConfirm;

    private readonly TextBox _expr;
    private readonly StackPanel _paramPanel;
    private readonly Canvas _preview;
    private readonly TextBlock _rangeLabel;
    private readonly TextBlock _status;

    /// <summary>底部数学键盘（M26 起抽到契约工程的共用组件 MathKeyboard，默认表达式布局；与上方自定义数学键盘并存）。</summary>
    private readonly MathKeyboard _vk;

    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly Dictionary<string, TextBlock> _valueLabels = new();
    private readonly Dictionary<string, double> _paramValues = new();

    /// <summary>标注开关（零点 / 极值 / 交点）。</summary>
    private readonly CheckBox _showZeros = new() { Content = "标零点", FontSize = 13, Margin = new Thickness(0, 2, 10, 2) };
    private readonly CheckBox _showExtrema = new() { Content = "标极值", FontSize = 13, Margin = new Thickness(0, 2, 10, 2) };
    private readonly CheckBox _showIntersections = new() { Content = "标交点", FontSize = 13, Margin = new Thickness(0, 2, 10, 2) };

    private double _xMin = -10, _xMax = 10, _yMin = -10, _yMax = 10;

    public FunctionInputWindow(FunctionSpec? initial, Action<FunctionSpec> onConfirm)
    {
        _onConfirm = onConfirm;
        FontFamily = new FontFamily("Microsoft YaHei, Microsoft YaHei UI, Segoe UI");
        Title = "函数图像";
        // 放大以容纳底部虚拟键盘（键盘整体约 830 世界/像素宽，故窗宽给到 940）
        Width = 940; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        if (initial != null)
        {
            _xMin = initial.XMin; _xMax = initial.XMax; _yMin = initial.YMin; _yMax = initial.YMax;
            foreach (var kv in initial.Parameters) _paramValues[kv.Key] = kv.Value;
            _showZeros.IsChecked = initial.ShowZeros;
            _showExtrema.IsChecked = initial.ShowExtrema;
            _showIntersections.IsChecked = initial.ShowIntersections;
        }
        foreach (var cb in new[] { _showZeros, _showExtrema, _showIntersections })
            cb.Click += (_, _) => RefreshPreview();

        // ---------------- 左列：表达式 + 键盘 + 参数 ----------------
        _expr = new TextBox
        {
            IsReadOnly = true,
            FontSize = 22,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
            Margin = new Thickness(6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = initial?.Expr ?? "x^2",
        };

        _status = new TextBlock
        {
            FontSize = 12, Foreground = Brushes.Red, Margin = new Thickness(6, 0, 6, 0),
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        };

        var keypad = BuildKeypad();

        _paramPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        var paramHeader = new TextBlock
        {
            Text = "参数（拖动滑块实时改变曲线）",
            FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2),
        };

        // 标注开关：三个复选框，落在"参数"下方（都属于"这条曲线的附加信息"）
        var markerRow = new WrapPanel { Margin = new Thickness(6, 4, 6, 0) };
        markerRow.Children.Add(new TextBlock
        {
            Text = "标注：", FontSize = 13, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 4, 2),
        });
        markerRow.Children.Add(_showZeros);
        markerRow.Children.Add(_showExtrema);
        markerRow.Children.Add(_showIntersections);

        var left = new StackPanel { Width = 360 };
        left.Children.Add(new TextBlock { Text = "表达式（点按钮拼写）", FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(6, 6, 6, 0) });
        left.Children.Add(_expr);
        left.Children.Add(_status);
        left.Children.Add(keypad);
        left.Children.Add(markerRow);
        left.Children.Add(paramHeader);
        left.Children.Add(_paramPanel);

        // ---------------- 右列：预览 + 范围 + 按钮 ----------------
        _preview = new Canvas
        {
            Width = 220, Height = 200, Margin = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)),
            ClipToBounds = true,
        };

        var rangePanel = new WrapPanel { Margin = new Thickness(8, 0, 8, 0) };
        foreach (var (label, a, b, c, d) in new[]
        {
            ("[-10,10]", -10.0, 10.0, -10.0, 10.0),
            ("[-5,5]", -5.0, 5.0, -5.0, 5.0),
            ("[0,2π]", 0.0, 2 * Math.PI, -2.0, 2.0),
        })
        {
            var btn = new Button
            {
                Content = label, FontSize = 13, Margin = new Thickness(3),
                Padding = new Thickness(6, 3, 6, 3),
            };
            double aa = a, bb = b, cc = c, dd = d;
            btn.Click += (_, _) => { _xMin = aa; _xMax = bb; _yMin = cc; _yMax = dd; UpdateRangeLabel(); RefreshPreview(); };
            rangePanel.Children.Add(btn);
        }
        _rangeLabel = new TextBlock { FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(8, 2, 8, 4) };

        var confirm = new Button
        {
            Content = "确定", FontSize = 16, Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)), Foreground = Brushes.White,
        };
        confirm.Click += (_, _) => Confirm();
        var cancel = new Button
        {
            Content = "取消", FontSize = 16, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8),
        };
        cancel.Click += (_, _) => Close();
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(confirm);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "预览", FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(8, 6, 8, 0) });
        right.Children.Add(_preview);
        right.Children.Add(rangePanel);
        right.Children.Add(_rangeLabel);
        right.Children.Add(btnRow);

        // ---------------- 底部：虚拟全键盘（与自定义数学键盘并存）----------------
        // 一体机没有物理键盘：这块键盘让老师"像真实键盘一样打字"，
        // 上方自定义数学键盘则保留"一键插 sin( / √( 等"的便利，两者共用同一套插入逻辑。
        _vk = new MathKeyboard();
        _vk.KeyPressed += k => InsertText(k.Text, k.CaretOffset);
        var keyboard = _vk.Build();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);  Grid.SetRow(left, 0);
        Grid.SetColumn(right, 1); Grid.SetRow(right, 0);
        Grid.SetRow(keyboard, 1);
        Grid.SetColumnSpan(keyboard, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        grid.Children.Add(keyboard);
        Content = grid;

        UpdateRangeLabel();
        RebuildParams();
        RefreshPreview();
    }

    // ---------------------------------------------------------- 键盘

    private static readonly (string Label, string Insert)[] Keys =
    {
        ("sin", "sin("), ("cos", "cos("), ("tan", "tan("), ("ln", "ln("), ("log", "log("), ("√", "sqrt("),
        ("(", "("), (")", ")"), ("x", "x"), ("π", "π"), ("e", "e"), ("^", "^"),
        ("×", "*"), ("÷", "/"), ("+", "+"), ("−", "-"), (Back, Back), (Clear, Clear),
        // 分段 / 定义域专用：{ 条件: 取值, else: 取值 } 、 where x>=0 、比较符
        ("{ }", "{x<0: -x, else: x}"), ("where", " where "), ("<", "<"), (">", ">"),
        ("<=", "<="), (">=", ">="), (",", ","), (":", ":"),
    };

    private UniformGrid BuildKeypad()
    {
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, insert) in Keys)
        {
            var btn = new Button
            {
                Content = label, FontSize = 18, Margin = new Thickness(3),
                Padding = new Thickness(4), FontWeight = FontWeights.SemiBold,
            };
            string s = insert;
            btn.Click += (_, _) => Append(s);
            grid.Children.Add(btn);
        }
        return grid;
    }

    private void Append(string s)
    {
        string token = s switch
        {
            Back => MathKeyboard.Back,
            Clear => MathKeyboard.Clear,
            _ => s,
        };
        InsertText(token);
    }

    /// <summary>
    /// 把按键插入到表达式。M26 起插入逻辑统一走 MathKeyboard.HandleKey（与 LaTeX 公式面板同一份实现，
    /// 光标落点可确定性断言）；caretOffset 支持骨架键把光标送进花括号内部（表达式布局当前全用默认 -1 = 落末尾）。
    /// 文本框保持只读，避免触发系统软键盘遮住画布。
    /// </summary>
    private void InsertText(string token, int caretOffset = -1)
    {
        MathKeyboard.HandleKey(_expr, new MathKeyboard.KeyInsert(token, caretOffset));
        RebuildParams();
        RefreshPreview();
    }

    // ---------------------------------------------------------- 参数滑块

    private void RebuildParams()
    {
        if (!TryParse(out var ast))
        {
            _paramPanel.Children.Clear();
            _sliders.Clear();
            _valueLabels.Clear();
            return;
        }
        var names = ExpressionEvaluator.CollectParameters(ast);
        var keep = new HashSet<string>(names);

        // 移除已消失的参数滑块
        for (int i = _paramPanel.Children.Count - 1; i >= 0; i--)
        {
            if (_paramPanel.Children[i] is FrameworkElement fe && fe.Tag is string tag && !keep.Contains(tag))
                _paramPanel.Children.RemoveAt(i);
        }
        foreach (var k in _sliders.Keys.ToList())
            if (!keep.Contains(k)) _sliders.Remove(k);
        foreach (var k in _valueLabels.Keys.ToList())
            if (!keep.Contains(k)) _valueLabels.Remove(k);

        // 新增出现的参数滑块
        foreach (var name in names)
        {
            if (_sliders.ContainsKey(name)) continue;
            var slider = new Slider
            {
                Minimum = -5, Maximum = 5, TickFrequency = 0.01, IsSnapToTickEnabled = true,
                Width = 180, Margin = new Thickness(0, 2, 0, 2),
                Value = _paramValues.TryGetValue(name, out var v) ? v : 1.0,
            };
            var valLabel = new TextBlock { FontSize = 12, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = name, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = name + " =", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Width = 28 });
            row.Children.Add(slider);
            row.Children.Add(valLabel);

            double last = slider.Value;
            slider.ValueChanged += (_, _) =>
            {
                _paramValues[name] = slider.Value;
                valLabel.Text = slider.Value.ToString("F2", CultureInfo.InvariantCulture);
                if (Math.Abs(slider.Value - last) > 0)
                {
                    last = slider.Value;
                    RefreshPreview();
                }
            };
            valLabel.Text = slider.Value.ToString("F2", CultureInfo.InvariantCulture);

            _sliders[name] = slider;
            _valueLabels[name] = valLabel;
            _paramPanel.Children.Add(row);
        }
    }

    // ---------------------------------------------------------- 预览

    private bool TryParse(out ExprNode ast)
    {
        // 交互输入过程中表达式随时可能不完整（如刚按了 "(" 还没闭合），
        // 解析失败必须就地消化成红字提示，绝不能让它冒泡成 DispatcherUnhandledException。
        try
        {
            ast = ExpressionParser.Parse(_expr.Text);
            _status.Visibility = Visibility.Collapsed;
            _status.Text = string.Empty;
            return true;
        }
        catch (ParseException ex)
        {
            ast = null!;   // 仅在返回 false 的分支使用，调用方会立即 return，不会解引用
            _status.Text = "表达式有误：" + ex.Message;
            _status.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void UpdateRangeLabel()
    {
        _rangeLabel.Text = $"范围  x∈[{_xMin:F1},{_xMax:F1}]   y∈[{_yMin:F1},{_yMax:F1}]";
    }

    private void RefreshPreview()
    {
        _preview.Children.Clear();
        if (!TryParse(out var ast)) return;

        var parameters = new Dictionary<string, double>();
        foreach (var kv in _paramValues) parameters[kv.Key] = kv.Value;

        var segments = FunctionSampler.Sample(ast, _xMin, _xMax, _yMin, _yMax, parameters);
        double w = _preview.Width, h = _preview.Height;
        double dx = (_xMax - _xMin) <= 0 ? 1 : w / (_xMax - _xMin);
        double dy = (_yMax - _yMin) <= 0 ? 1 : h / (_yMax - _yMin);

        foreach (var seg in segments)
        {
            if (seg.Count < 2) continue;
            var pts = new PointCollection();
            foreach (var p in seg)
            {
                double px = (p.X - _xMin) * dx;
                double py = h - (p.Y - _yMin) * dy;     // 翻转 y（屏幕向下）
                pts.Add(new Point(px, py));
            }
            _preview.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = Brushes.DarkSlateBlue,
                StrokeThickness = 2,
            });
        }

        DrawPreviewMarkers(ast, parameters, dx, dy, h);
    }

    /// <summary>在预览画布上画特征点（与正式渲染同一套 <see cref="FeatureFinder"/>，不另写一份）。</summary>
    private void DrawPreviewMarkers(ExprNode ast, Dictionary<string, double> parameters, double dx, double dy, double h)
    {
        var found = new List<FeaturePoint>();

        if (_showZeros.IsChecked == true)
            found.AddRange(FeatureFinder.FindZeros(ast, parameters, _xMin, _xMax, 24));

        if (_showExtrema.IsChecked == true)
        {
            foreach (var p in FeatureFinder.FindExtrema(ast, parameters, _xMin, _xMax, 24))
                if (p.Kind != FeatureKind.Inflection) found.Add(p);
        }

        foreach (var p in found)
        {
            if (p.Y < _yMin || p.Y > _yMax) continue;
            double px = (p.X - _xMin) * dx;
            double py = h - (p.Y - _yMin) * dy;
            var dot = new Ellipse
            {
                Width = 6, Height = 6, Fill = Brushes.White,
                Stroke = Brushes.Crimson, StrokeThickness = 1.6, IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, px - 3);
            Canvas.SetTop(dot, py - 3);
            _preview.Children.Add(dot);
        }
    }

    // ---------------------------------------------------------- 确定

    private void Confirm()
    {
        if (!TryParse(out _))
        {
            MessageBox.Show("表达式无法解析，请检查。", "函数图像", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var spec = new FunctionSpec
        {
            Expr = _expr.Text,
            XMin = _xMin, XMax = _xMax, YMin = _yMin, YMax = _yMax,
            ShowZeros = _showZeros.IsChecked == true,
            ShowExtrema = _showExtrema.IsChecked == true,
            ShowIntersections = _showIntersections.IsChecked == true,
        };
        foreach (var kv in _paramValues) spec.Parameters[kv.Key] = kv.Value;
        _onConfirm(spec);
        Close();
    }
}
