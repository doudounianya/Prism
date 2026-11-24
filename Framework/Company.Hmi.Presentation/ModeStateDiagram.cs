using Company.Hmi.Contracts;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Company.Hmi.Presentation;

/// <summary>仅用于选择要展示的图形，不代表设备实际模式。</summary>
public enum ModeDiagramKind { Manual, Automatic, Maintenance, Emergency }

/// <summary>
/// 用户确认的三张模式图及共用急停说明的原生 WPF 呈现。模式图保留参考图的坐标比例、线路与箭头；
/// Viewbox 只执行等比例缩放，窗口尺寸变化不会重建图形，更不会发起通讯。
/// 界面操作使用原生 Button，仅向页面报告明确操作意图；通讯和权限校验均由既有业务服务执行。
/// 反馈变化只更新现有状态框和按钮属性，不重建图形，不把演示状态带入现场程序。
/// </summary>
public sealed class ModeStateDiagram : Viewbox
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ModeDiagramKind), typeof(ModeStateDiagram),
        new PropertyMetadata(ModeDiagramKind.Manual, (owner, _) => ((ModeStateDiagram)owner).Build()));

    public ModeDiagramKind Kind { get => (ModeDiagramKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.Register(nameof(Feedback), typeof(IModeDiagramFeedback),
        typeof(ModeStateDiagram), new PropertyMetadata(EmptyFeedback.Instance, (owner, _) => ((ModeStateDiagram)owner).ApplyFeedback()));
    public IModeDiagramFeedback Feedback { get => (IModeDiagramFeedback)GetValue(FeedbackProperty); set => SetValue(FeedbackProperty, value); }
    public event EventHandler<ModeActionEventArgs>? ActionRequested;

    private Canvas? _canvas;
    private readonly Dictionary<int, Border> _nodes = new();
    private readonly List<Button> _buttons = [];
    private readonly Dictionary<Button, (TextBlock Caption, Path Hand)> _actionContent = new();
    private readonly List<(string Text, string Type, Border Border)> _labels = [];
    private double _arrowSize;
    private static readonly FontFamily DiagramFont = new("Microsoft YaHei, Segoe UI");
    // 缓存空心手掌的矢量轮廓，不依赖字体版本或系统表情，避免现场显示成缺字方框。
    private static readonly Geometry HandIconGeometry = (Geometry)Geometry.Parse(
        "M9 22 C7 21 5 18 3 15 C2 13 3 11 5 12 L7 14 V6 C7 3.5 10 3.5 10 6 V4 C10 1.5 13 1.5 13 4 V6 C13 3.5 16 3.5 16 6 V9 C16 6.5 19 6.5 19 9 V15 C19 19 16 22 13 22 Z M10 6 V12 M13 6 V12 M16 9 V13").GetAsFrozen();
    // 全图只有一种强调色：当前状态与 True 都为蓝底白字，其他节点/信号使用白底灰框。
    private const string ActiveFill = "#1765D2", ActiveEdge = "#104E9C";
    private const string NeutralFill = "#FFFFFF", NeutralEdge = "#A8B5C4", NeutralText = "#526171", ZoneFill = "#F4F6F8";

    public ModeStateDiagram()
    {
        Stretch = Stretch.Uniform;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (_canvas is null) Build();
    }

    /// <summary>仅初始化或显式更换图形种类时构建；页签切换不调用本方法。</summary>
    private void Build()
    {
        _nodes.Clear(); _buttons.Clear(); _actionContent.Clear(); _labels.Clear();
        _canvas = new Canvas { Background = GridBackground(), ClipToBounds = false };
        Child = _canvas;
        _arrowSize = Kind == ModeDiagramKind.Maintenance ? 7 : 12;
        switch (Kind)
        {
            case ModeDiagramKind.Manual: Manual(); break;
            case ModeDiagramKind.Automatic: Automatic(); break;
            case ModeDiagramKind.Maintenance: Maintenance(); break;
            case ModeDiagramKind.Emergency: Emergency(); break;
        }
        ApplyFeedback();
    }

    private void Manual()
    {
        _canvas!.Width = 1098; _canvas.Height = 602;
        Zone(183, 150, 540, 210, 34, 5);
        // 每条路径均为独立的有向状态转移；坐标取自用户最终确认的手动模式图。
        Wire("M475 86H62V551H475"); Wire("M684 551H1014V86H761");
        Wire("M475 99H251V280"); Wire("M355 280V112H475");
        Wire("M500 125V280"); Wire("M650 280V125");
        // 按最新图示删除停止→故障、调试→故障、调试→暂停三条说明路径；其余路径保留。
        Wire("M910 454H944V99H761");
        Wire("M684 306H806V427");
        Wire("M580 333V512"); Wire("M702 454H631V512");
        Title("手动模式", 32, 42);
        // 初始化请求与完成/停止条件分列；文字边界之间留出间隔，连线跟随各自列中心。
        Action("参数调试", 251, 206, 92, 32, 20, "DebugEnter"); Action("退出调试", 355, 206, 92, 32, 20, "DebugExit"); Action("初始化按钮", 500, 207, 110, 32, 20, "Initialize");
        Label("初始化完成", 650, 180, "flag"); Label("或", 650, 208, "text"); Action("停止按钮", 650, 237, 100, 32, 20, "Stop");
        // 条件标签使用对应水平/竖直线的精确坐标；下方标签移至直线段中部，避开折角。
        Label("暂停触发", 62, 292); Label("暂停解除", 1014, 309); Label("告警解除", 944, 278);
        Label("告警", 754, 306, "alarm");
        Label("暂停触发", 580, 425); Label("暂停触发", 631, 475);
        Node("0：停止", 475, 73, 286, 52, 28); Node("30：调试", 199, 280, 208, 53, 28);
        Node("40：初始化", 475, 280, 209, 53, 28); Node("10：故障", 702, 427, 208, 52, 28); Node("20：暂停", 475, 512, 209, 52, 28);
    }

    private void Automatic()
    {
        _canvas!.Width = 1127; _canvas.Height = 734;
        Zone(155, 203, 425, 327, 51, 6);
        Wire("M216 115H72V678H216"); Wire("M521 689H988V115H521");
        Wire("M840 578H882V130H521"); Wire("M521 146H778V546");
        Wire("M270 162V390"); Wire("M470 390V162"); Wire("M521 421H715V546");
        Wire("M270 452V642"); Wire("M715 609V658H521");
        Title("自动模式", 51, 54);
        // 左列为三个同时满足的运行条件；右列为单次结束、停止按钮或旋钮手动，任一成立即退出运行。
        // 不缩小字号：扩大列距、逐行保留间隔，并下移运行节点给条件组留足高度。
        Label("初始化完成", 270, 186, "flag", 26); Label("与", 270, 227, "text", 26);
        Label("运行继续校验", 270, 268, "flag", 26); Label("与", 270, 309, "text", 26); Label("运行按钮", 270, 350, "physical", 26);
        Label("单次任务结束", 470, 202, "flag", 26); Label("或", 470, 240, "text", 26);
        Action("停止按钮", 470, 278, 130, 34, 26, "Stop"); Label("或", 470, 316, "text", 26); Label("旋钮手动", 470, 354, "physical", 26);
        Label("告警", 644, 146, "alarm", 26); Label("告警", 638, 421, "alarm", 26);
        Label("告警解除", 882, 352, "physical", 26); Label("暂停解除", 988, 402, "physical", 26);
        // 故障返回暂停的标签置于水平线段中部，留出左侧箭头及状态框的间隔。
        Label("暂停触发", 72, 362, "physical", 26); Label("暂停触发", 270, 548, "physical", 26); Label("暂停触发", 630, 658, "physical", 26);
        Node("0：停止", 216, 99, 305, 63, 32); Node("50：运行", 216, 390, 305, 62, 32);
        Node("10：故障", 590, 546, 250, 63, 32); Node("20：暂停", 216, 642, 305, 63, 32);
    }

    private void Maintenance()
    {
        _canvas!.Width = 665; _canvas.Height = 338;
        Zone(87, 122, 402, 156, 28, 3);
        // 保留原图三组路径。末人退出先回停止，再重判故障/暂停；旁边两条退出路径是重判结果的简图，详见页面说明。
        // 维护/故障节点中心仍在 y=198，仅增加节点高度，为两条反向条件线保留独立的文字空间。
        Wire("M257 93V163"); Wire("M340 163V93"); Wire("M257 294V233"); Wire("M340 233V294");
        Wire("M382 174H496"); Wire("M496 221H382");
        Title("维护模式", 39, 26);
        const string enter = "本人验证上锁\n（逐人加入）", leave = "末人验证解锁\n（回到停止）";
        Action(enter, 257, 141, 77, 30, 11.5, "MaintenanceEnter0"); Action(leave, 340, 141, 77, 30, 11.5, "MaintenanceExit");
        Action("末人验证解锁\n（故障重判）", 448, 174, 77, 30, 11.5, "MaintenanceExit"); Action(enter, 448, 221, 77, 30, 11.5, "MaintenanceEnter10");
        Action(enter, 257, 261, 77, 30, 11.5, "MaintenanceEnter20"); Action("末人验证解锁\n（暂停重判）", 340, 261, 77, 30, 11.5, "MaintenanceExit");
        Node("0：停止", 216, 63, 166, 30, 17); Node("60：维护任务", 216, 163, 166, 70, 17);
        Node("10：故障", 496, 163, 121, 70, 17); Node("20：暂停", 216, 294, 166, 30, 17);
    }

    /// <summary>急停是任意模式共用的状态说明；此图不产生请求，也不把状态 70 当作执行器停稳证明。</summary>
    private void Emergency()
    {
        _canvas!.Width = 900; _canvas.Height = 620;
        Title("急停状态判定", 45, 36);
        Put(new Border { Width = 360, Height = 58, Background = Brush(NeutralFill), BorderBrush = Brush(NeutralEdge), BorderThickness = new Thickness(1.8), CornerRadius = new CornerRadius(6), Child = Text("任意模式／任意状态", 28, Brush(NeutralText)) }, 270, 110);
        Wire("M450 168V265"); Wire("M450 329V480");
        Label("急停信号触发", 450, 214, "flag", 26);
        Label("急停信号解除", 450, 404, "flag", 26);
        Node("70：急停", 270, 265, 360, 64, 32); Node("0：停止", 270, 480, 360, 64, 32);
        var note = Text("状态70不代表机构已停稳；实际停稳与安全复位另行确认。", 20, Brush(NeutralText));
        note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Put(note, (900 - note.DesiredSize.Width) / 2, 575);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(color)!;
        brush.Freeze(); return brush;
    }

    /// <summary>网格只是一张可复用的绘图刷，不创建大量网格线控件。</summary>
    private static DrawingBrush GridBackground()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 50, 50));
            for (var offset = 0; offset <= 50; offset += 10)
            {
                var pen = new Pen(Brush(offset is 0 or 50 ? "#EEF0F3" : "#F7F8FA"), offset is 0 or 50 ? 1 : .6);
                dc.DrawLine(pen, new Point(offset, 0), new Point(offset, 50));
                dc.DrawLine(pen, new Point(0, offset), new Point(50, offset));
            }
        }
        var result = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 50, 50) };
        result.Freeze(); return result;
    }

    private void Put(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y); _canvas!.Children.Add(element);
    }

    private void Zone(double x, double y, double width, double height, double radius, double stroke)
        => Put(new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius,
            Fill = Brush(ZoneFill), Stroke = Brush("#B8C2CC"), StrokeThickness = stroke,
            StrokeDashArray = new DoubleCollection([3.4, 2.8]), IsHitTestVisible = false }, x, y);

    /// <summary>箭头从每条线路的末端切向量计算，避免水平和竖直线路的箭头方向不一致。</summary>
    private void Wire(string data)
    {
        var geometry = PathGeometry.CreateFromGeometry(Geometry.Parse(data));
        geometry.GetPointAtFractionLength(1, out var tip, out var tangentPoint);
        geometry.Freeze();
        _canvas!.Children.Add(new Path { Data = geometry, Stroke = Brushes.Black, StrokeThickness = 1.8, IsHitTestVisible = false });
        var tangent = new Vector(tangentPoint.X, tangentPoint.Y);
        tangent.Normalize();
        var side = new Vector(-tangent.Y, tangent.X);
        var arrow = new StreamGeometry();
        using (var context = arrow.Open())
        {
            context.BeginFigure(tip, true, true);
            context.LineTo(tip - tangent * _arrowSize + side * (_arrowSize / 2), true, false);
            context.LineTo(tip - tangent * (_arrowSize * .7), true, false);
            context.LineTo(tip - tangent * _arrowSize - side * (_arrowSize / 2), true, false);
        }
        arrow.Freeze(); _canvas.Children.Add(new Path { Data = arrow, Fill = Brushes.Black, IsHitTestVisible = false });
    }

    private static TextBlock Text(string text, double size, Brush foreground) => new()
    {
        Text = text, FontFamily = DiagramFont, FontSize = size, Foreground = foreground,
        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
        Padding = new Thickness(0), IsHitTestVisible = false
    };

    private void Title(string text, double y, double size)
    {
        var label = Text(text, size, Brush(NeutralText)); label.FontWeight = FontWeights.Bold;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 按整张绘图画布居中，不再跟随原图中某个状态框的横坐标。
        Put(label, (_canvas!.Width - label.DesiredSize.Width) / 2, y - label.DesiredSize.Height / 2);
    }

    private void Label(string text, double x, double y, string type = "physical", double size = 20)
    {
        var label = Text(text, size, Brush(NeutralText));
        // 高亮加粗边框仍给单行文字保留完整行高，避免 True 时上下笔画被挤掉。
        label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        label.LineHeight = size * 1.05;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = label.DesiredSize.Width + 4; var height = size * 1.25;
        var border = new Border { Width = width, Height = height, Child = label, IsHitTestVisible = false };
        // “与／或”与其所在分组采用相同的不透明底色，覆盖先绘制的连线，避免线穿过字形。
        border.Background = Brush(type == "text" ? ZoneFill : NeutralFill);
        border.BorderBrush = type == "text" ? Brushes.Transparent : Brush(NeutralEdge);
        border.BorderThickness = type == "text" ? new Thickness(0) : new Thickness(1);
        _labels.Add((text, type, border));
        Put(border, x - width / 2, y - height / 2);
    }

    private void Node(string text, double x, double y, double width, double height, double size)
    {
        var border = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(Kind == ModeDiagramKind.Maintenance ? 4 : 8),
            Background = Brush(NeutralFill), BorderBrush = Brush(NeutralEdge),
            BorderThickness = new Thickness(1.8), Child = Text(text, size, Brushes.Black), IsHitTestVisible = false };
        _nodes.Add(int.Parse(text.Split('：')[0]), border); Put(border, x, y);
    }

    /// <summary>
    /// 禁用模板不改变透明度，避免切页时变灰；按钮只报告业务动作，不能自行访问硬件。
    /// </summary>
    private void Action(string text, double x, double y, double width, double height, double size, string action)
    {
        var caption = Text(text, size, Brushes.Black);
        caption.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        caption.LineHeight = size * 1.1;
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hand = new Path { Width = 24, Height = 24, Data = HandIconGeometry, Stroke = Brushes.Black,
            StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.Uniform, IsHitTestVisible = false, Tag = "ActionHand" };
        // 手掌在文字左侧独立缩放，维护图两行文字仍按原字号、原行高完整显示。
        // 图标与文字均不单独命中，点击仍由原 Button 接收，初始化的按住/松开事件保持原路径。
        var iconWidth = size * .85;
        var gap = size * .1;
        var content = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        content.Children.Add(new Viewbox { Width = iconWidth, Height = size, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, gap, 0), Child = hand, IsHitTestVisible = false });
        content.Children.Add(caption);
        // 保持按钮中心在线路上；仅补足容纳图标所需的最小宽度，避免新增手掌挤压或遮挡原文字。
        width = Math.Max(width, Math.Ceiling(caption.DesiredSize.Width + iconWidth + gap + 2));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Button.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var button = new Button { Width = width, Height = height, MinHeight = 0, MinWidth = 0,
            Content = content, Tag = action, Padding = new Thickness(0), IsEnabled = false,
            Background = Brush(NeutralFill), BorderBrush = Brush(NeutralEdge), BorderThickness = new Thickness(1),
            ToolTip = "等待当前有效反馈或操作权限。", Template = new ControlTemplate(typeof(Button)) { VisualTree = border } };
        System.Windows.Automation.AutomationProperties.SetName(button, text.Replace('\n', ' '));
        if (action is "Initialize" or "Stop") ModePressButton.SetUseHold(button, true);
        else button.Click += (_, _) => ActionRequested?.Invoke(this, new ModeActionEventArgs(action));
        _buttons.Add(button);
        _actionContent.Add(button, (caption, hand));
        ToolTipService.SetShowOnDisabled(button, true);
        Put(button, x - width / 2, y - height / 2);
    }

    /// <summary>将缓存/新读取投影到原来的控件实例；当前模式和状态绝不由页签选择推断。</summary>
    private void ApplyFeedback()
    {
        var feedback = Feedback ?? EmptyFeedback.Instance;
        var mode = Kind switch { ModeDiagramKind.Manual => 0, ModeDiagramKind.Maintenance => 10, _ => 20 };
        foreach (var (code, border) in _nodes)
        {
            bool current = (Kind == ModeDiagramKind.Emergency || feedback.Mode == mode) && feedback.Status == code;
            border.BorderBrush = Brush(current ? ActiveEdge : NeutralEdge);
            border.BorderThickness = new Thickness(current ? 3 : 1.8);
            border.Background = Brush(current ? ActiveFill : NeutralFill);
            if (border.Child is TextBlock nodeText)
            {
                nodeText.Foreground = current ? Brushes.White : Brush(NeutralText);
                nodeText.FontWeight = current ? FontWeights.Bold : FontWeights.Normal;
            }
        }
        foreach (var button in _buttons)
        {
            var action = (string)button.Tag;
            // 维护图上的入口只打开人员锁管理，不直接写 PLC。持锁或断线时也必须可进入验证/核对，
            // 真正上锁、解锁仍由人员管理服务校验身份、连接及当前反馈。
            bool maintenanceEntry = Kind == ModeDiagramKind.Maintenance;
            button.IsEnabled = (maintenanceEntry && feedback.SupportsPersonnelLocks) || ((action is "Initialize" or "Stop") && ModePressButton.IsHeld(button)) || feedback.CanRequest(action);
            var index = action == "Stop" ? 3 : action.StartsWith("Debug", StringComparison.Ordinal) ? 6 : action == "Initialize" ? 7 : 8;
            // 维护图的六处文字均为人员管理入口说明，不把同一个维护请求位映射成六个“动作已激活”。
            // 固定中性色；当前模式/状态节点仍由上方节点循环按真实 PLC 反馈高亮。
            bool on = !maintenanceEntry && feedback.Action(index) == true;
            button.Background = Brush(on ? ActiveFill : NeutralFill);
            button.BorderBrush = Brush(on ? ActiveEdge : NeutralEdge);
            button.BorderThickness = new Thickness(on ? 2 : 1);
            if (_actionContent.TryGetValue(button, out var content))
            {
                // 手掌与原文字使用相同前景色；仅文字沿用原有加粗规则，图标不额外改变状态颜色。
                var foreground = on ? Brushes.White : Brush(NeutralText);
                content.Caption.Foreground = foreground;
                content.Caption.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
                content.Hand.Stroke = foreground;
            }
            button.ToolTip = maintenanceEntry ? "打开人员锁管理，验证身份后上锁或解锁；此入口不直接写入 PLC。" :
                action == "Stop" ? "按住发出停止请求，松开释放。手动初始化或自动运行时可操作。" :
                button.IsEnabled ? "当前请求位：" + feedback.Action(index) : "等待当前有效反馈、对应状态或操作权限。";
        }
        foreach (var (text, type, border) in _labels)
        {
            // 同名条件在多条线路上重复显示时，全部使用同一个已确认的 PLC 反馈位。
            // 运行/暂停/复位仍只读；停止 b_Action[3] 按用户确认开放图内按住操作。
            // 解除条件按相应反馈为 False 判断；未知不能当成 False，也不开放任何物理/反馈位写入。
            if (type == "text") continue;
            bool? value = text switch
            {
                "运行按钮" => feedback.Action(1), "暂停按钮" => feedback.Action(2),
                "停止按钮" => feedback.Action(3), "复位按钮" => feedback.Action(4),
                "初始化完成" => feedback.Signal(0), "告警" => feedback.Signal(2),
                "单次任务结束" => feedback.Signal(3),
                "运行继续校验" => feedback.Signal(4), "暂停触发" => feedback.Signal(1),
                "暂停解除" => feedback.Signal(1) is { } pause ? !pause : null,
                "告警解除" => feedback.Signal(2) is { } fault && feedback.Signal(1) is { } held ? !fault && !held : null,
                "旋钮手动" => feedback.Action(0) is { } automatic ? !automatic : null,
                "急停信号触发" => feedback.Signal(5),
                "急停信号解除" => feedback.Signal(5) is { } emergency ? !emergency : null, _ => null
            };
            bool on = value == true;
            border.Background = Brush(on ? ActiveFill : NeutralFill);
            border.BorderBrush = Brush(on ? ActiveEdge : NeutralEdge);
            border.BorderThickness = new Thickness(on ? 2 : 1);
            if (border.Child is TextBlock label)
            {
                label.Foreground = on ? Brushes.White : Brush(NeutralText);
                label.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            }
        }
    }
}

public sealed class ModeActionEventArgs(string action) : EventArgs
{
    public string Action { get; } = action;
}

internal sealed class EmptyFeedback : IModeDiagramFeedback
{
    public static readonly EmptyFeedback Instance = new();
    public int? Mode => null;
    public int? Status => null;
    public bool SupportsPersonnelLocks => false;
    public bool? Action(int index) => null;
    public bool? Signal(int index) => null;
    public bool CanRequest(string action) => false;
}
