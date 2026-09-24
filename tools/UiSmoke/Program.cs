using System.Runtime.InteropServices;
using System.Text.Json;
using FluentJalium.Controls;
using FluentJalium.Themes;
using Dusk.Adapter.Jalium;
using Dusk.Ink.Controls;
using Dusk.Ink.Input;
using Dusk.Ink.Model;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using Jalium.UI.Threading;
using LanStartWrite.Inkcanvas;

// Local integration checks, not hardware touch certification or screenshot-diff tests.
// Windows stay offscreen. Preferences are stored beside this test executable, never in the user's profile.
internal static class Program
{
    private static int _checks;
    private static int _exitCode;
    private static readonly List<Window> Windows = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var preview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, preview ? "preview-state" : "test-state", "preferences.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new PreferenceSnapshot()));
        RenderContext.GetOrCreateCurrent(RenderBackend.Auto).DefaultRenderingEngine = RenderingEngine.Impeller;
        Jalium.UI.Markup.ThemeLoader.Initialize();
        var app = new Application();
        AppPreferences.Initialize(path);
        FluentTheme.Initialize(app);

        // 第一拍就要量导航面板的宽度。面板打开带 0.2 秒过渡，而第一拍在 320ms ——
        // 看着够，实测仍会抖（6 个导航项那一次就是它红的）。于是从一开头就关掉动画：
        // 这里量的是"布局事实"，不是"动画对不对"。导航动画那组需要动的时候自己会再打开。
        AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });

        if (preview)
        {
            var toolbar = new AnnotationToolbarWindow
            {
                Title = "揽星书写 · Fluent 预览", Left = 120, Top = 360,
            };
            app.MainWindow = toolbar;
            toolbar.Show();
            try { return app.Run(); }
            finally { AppPreferences.Flush(); }
        }
        var settings = new SettingsWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -12000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
        };
        app.MainWindow = settings;
        Windows.Add(settings);

        var navigation = (FluentNavigationView)settings.FindName("NavigationRoot")!;
        var steps = new Queue<Action>();
        steps.Enqueue(() =>
        {
            Check(((Grid)settings.FindName("SettingsContentHost")!).Children.Count == 1, "Only the selected page is attached");
            Check(!navigation.IsCompact && PaneWidth(navigation) == 220,
                $"Expanded navigation width（compact={navigation.IsCompact}，量到的宽={PaneWidth(navigation)}）");
            // 面板宽度带 0.2 秒过渡（库里读的是 SplitViewPaneAnimationOpenDuration），
            // 所以"下一拍就该读到 48"其实是道时序题 —— 实测会抖。把过渡关掉再量宽度，
            // 用的正是应用自己那一项"减少动画"；两态是否真的换了由 IsCompact 与标签折叠那两条管。
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            settings.Width = 560;
        });
        steps.Enqueue(() =>
        {
            settings.ForceRenderFrame();
            Check(navigation.IsCompact && PaneWidth(navigation) == 48, "Native/layout resize switches navigation to compact");
            var inkItem = (FluentNavigationItem)settings.FindName("InkNavButton")!;
            Check(inkItem.IsCompact && Part<ContentPresenter>(Descendants(inkItem), "PART_Label").Visibility == Visibility.Collapsed,
                "Compact state reaches the items and hides their labels");
            settings.Width = 960;
        });
        steps.Enqueue(() =>
        {
            settings.ForceRenderFrame();
            Check(!navigation.IsCompact && PaneWidth(navigation) == 220, "Expanded navigation restored after widening");
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = false });
            CheckThemePalette();
            CheckSwitch(settings);
            CheckResponsiveRows(settings);
            CheckNavigation(settings);
            CheckToolbarTouch();
            CheckToolbarTools(settings);
            CheckCanvasModes(settings);
            CheckPenMenu();
            CheckTipMenu();
            CheckTipOptions(settings);
            CheckTipReload();
            CheckTipEditor(settings);
            CheckEraserMenu();
            CheckFlyoutPlacement();
            CheckToolbarPlacement();
            CheckPreferences(path);
            CheckInkSurface();
            CheckWindowLayers();
        });
        // 排在导航动画那组之前：那组里有一条本机常红的时序检查，而 Check() 一红就中断整个队列。
        steps.Enqueue(CheckInkPreferenceLands);
        QueueNavigationAnimationChecks(settings, steps);
        // 自愈那道网得真的在跑。它由 DispatcherTimer 驱动（1.5 秒一拍），
        // 而队列每拍 320ms、前面还压着一组导航动画检查 —— 排到最后，到这里早就过了一拍。
        steps.Enqueue(() => Check(WindowLayerManager.AutoRepairChecks > 0,
            $"周期自检确实在跑（跑了 {WindowLayerManager.AutoRepairChecks} 拍，不是只写了没接上）"));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (steps.Count > 0) steps.Dequeue()();
                if (steps.Count > 0) return;
                Console.WriteLine($"PASS: {_checks} checks. Synthetic routed touch only; physical touch not tested.");
            }
            catch (Exception ex)
            {
                _exitCode = 1;
                Console.Error.WriteLine(ex);
            }
            timer.Stop();
            for (var i = Windows.Count - 1; i >= 0; i--) Windows[i].Close();
        };
        settings.Loaded += (_, _) => timer.Start();
        settings.Show();
        app.Run();
        return _exitCode;
    }

    private static void CheckThemePalette()
    {
        Check(Equals(ResourceDictionary.CurrentThemeKey, "Light"), "Initial Light theme also initializes framework resource lookup");
        var original = FluentThemeManager.GetBrush("TextFillColorPrimaryBrush");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Dark });
        Check(FluentThemeManager.IsDark, "Dark theme state");
        Check(ReferenceEquals(original, FluentThemeManager.GetBrush("TextFillColorPrimaryBrush")), "Existing brush identity preserved when changing theme");
        Check(((SolidColorBrush)original).Color.R == 255, "Existing text brush changes to dark-theme foreground");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Light });
        Check(!FluentThemeManager.IsDark && ((SolidColorBrush)original).Color.R == 0, "Light theme restores existing brush");
        // 应用自有 token：库没有"实体浮层表面"这一层，深浅得由应用自己跟着翻 —— 这两条守的就是那条接线。
        var toolbarSurface = (SolidColorBrush)FluentTheme.AppTokenBrush("ToolbarSurfaceBrush");
        Check(toolbarSurface.Color.R == 0xFF, "App-owned toolbar surface is the light solid in Light");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Dark });
        Check(ReferenceEquals(toolbarSurface, (SolidColorBrush)FluentTheme.AppTokenBrush("ToolbarSurfaceBrush"))
            && toolbarSurface.Color.R == 0x2C, "App-owned toolbar surface flips dark in place on the same brush");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Light });
        // FluentJalium 把 SystemColor* 做成直读 SystemColors 的别名，不再抄一份颜色值，
        // 所以"抄色对不对"这条已经无从比较，只能验它解析得到的是个可用画笔。
        Check(FluentThemeManager.GetBrush("SystemColorWindowColorBrush") is SolidColorBrush,
            "Native system colour slots resolve through the theme layer");
    }

    private static void CheckSwitch(SettingsWindow window)
    {
        var toggle = (FluentToggleSwitch)window.FindName("ReduceMotionSwitch")!;
        var clicks = 0;
        var command = new CountingCommand();
        toggle.Click += (_, _) => clicks++;
        toggle.Command = command;
        Check(toggle.Width == 48 && toggle.Height == 32, "Switch hit target is 48 x 32 DIP");
        var track = Descendants(toggle).OfType<Border>().First(x => x.Name == "SwitchTrack");
        Check(Math.Abs(track.ActualWidth - 40) < 0.1 && Math.Abs(track.ActualHeight - 20) < 0.1, "Switch rendered track is 40 x 20 DIP");
        var root = (UIElement)window.Content!;
        var start = toggle.TransformToVisual(root)!.Transform(new Point(12, 16));
        var first = new SyntheticTouch(91);
        var second = new SyntheticTouch(92);
        SendTouch(toggle, first, UIElement.PreviewTouchDownEvent, start);
        Check(toggle.IsPointerPressed && ReferenceEquals(first.Captured, toggle), "Touch-down captures the originating contact");
        SendTouch(toggle, second, UIElement.PreviewTouchDownEvent, start);
        Check(second.Captured is null, "Second contact cannot take over switch drag");
        SendTouch(toggle, first, UIElement.PreviewTouchMoveEvent, new Point(start.X + 20, start.Y));
        Check(toggle.ThumbOffset.Left == 20, "Captured touch moves knob to on position");
        SendTouch(toggle, first, UIElement.PreviewTouchUpEvent, new Point(start.X + 20, start.Y));
        Check(toggle.IsChecked == true && AppPreferences.Current.ReduceMotion, "Touch release commits exactly one state change");
        Check(first.Captured is null && !toggle.IsPointerPressed, "Touch release clears contact capture");
        Check(clicks == 1 && command.Count == 1, "Touch drag commits Click and Command once, without double toggling");
        toggle.IsChecked = false;
        SendTouch(toggle, first, UIElement.PreviewTouchDownEvent, start);
        toggle.IsEnabled = false;
        Check(first.Captured is null && !toggle.IsPointerPressed, "Disabling during drag releases capture");
        SendTouch(toggle, first, UIElement.PreviewTouchDownEvent, start);
        Check(!toggle.IsPointerPressed && toggle.IsChecked == false, "Disabled switch ignores touch-down");
        toggle.IsEnabled = true;
        SendTouch(toggle, first, UIElement.PreviewTouchDownEvent, start);
        SendTouch(toggle, first, UIElement.PreviewTouchUpEvent, new Point(start.X, start.Y + 80));
        Check(!toggle.IsPointerPressed && toggle.IsChecked == false && clicks == 1,
            "Tap released outside switch cancels without a click");
        SendTouch(toggle, first, UIElement.PreviewTouchDownEvent, start);
        toggle.ReleaseTouchCapture(first);
        Check(!toggle.IsPointerPressed && toggle.IsChecked == false && clicks == 1,
            "Lost touch capture cancels pending switch interaction");
        first.Finish();
        second.Finish();
        toggle.Command = null;
    }

    private static void CheckResponsiveRows(SettingsWindow window)
    {
        var row = (FluentSettingsRow)window.FindName("ThemeSettingsRow")!;
        var description = (FrameworkElement)row.Children[0];
        var action = (FrameworkElement)row.Children[1];
        Point At(FrameworkElement child) => child.TransformToVisual(row)!.Transform(new Point(0, 0));
        row.Measure(new Size(300, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 300, row.DesiredSize.Height));
        // 库的 FluentSettingsRow 是 Panel，没有行列可问 —— 只能量它真的排出来的位置。
        Check(row.IsStacked && At(action).Y >= description.ActualHeight - 1
            && action.ActualWidth > 0 && description.ActualWidth > 280,
            "Narrow settings row stacks the action below a full-width description");
        Check(ReferenceEquals(action, row.Children[1]),
            "Responsive reflow repositions the same control instead of replacing it");
        row.Measure(new Size(600, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height));
        // 宽态下动作控件在自己的槽里还会再垂直居中一次（实测 y 比说明低 4.57 DIP），
        // 所以这里要的是"两条带子重叠"，不是"顶边对齐"。
        var actionBottom = At(action).Y + action.ActualHeight;
        Check(!row.IsStacked && At(action).X > At(description).X
            && At(action).Y < description.ActualHeight && actionBottom > 0
            && Math.Abs(At(action).X + action.ActualWidth - 600) < 0.1,
            "Wide settings row puts the action right-aligned beside the description");
        window.InvalidateMeasure();
        window.ForceRenderFrame();
    }

    private static void CheckNavigation(SettingsWindow window)
    {
        var appearance = (FluentNavigationItem)window.FindName("AppearanceNavButton")!;
        var ink = (FluentNavigationItem)window.FindName("InkNavButton")!;
        Check(appearance.IsSelected && !ink.IsSelected, "Navigation exposes selection independently of focus");
        ink.RaiseEvent(new RoutedEventArgs(Jalium.UI.Controls.Primitives.ButtonBase.ClickEvent, ink));
        window.ForceRenderFrame();
        var host = (Grid)window.FindName("SettingsContentHost")!;
        Check(ink.IsSelected && !appearance.IsSelected && host.Children.Count == 1 &&
            ReferenceEquals(host.Children[0], window.FindName("InkSectionPanel")),
            "Navigation activates exactly one live page");
        Check(ink.GetValue(Control.FocusVisualStyleProperty) is not null,
            "Navigation item carries the theme's keyboard focus visual");
        appearance.RaiseEvent(new RoutedEventArgs(Jalium.UI.Controls.Primitives.ButtonBase.ClickEvent, appearance));
    }

    private static void QueueNavigationAnimationChecks(SettingsWindow window, Queue<Action> steps)
    {
        var navigation = (FluentNavigationView)window.FindName("NavigationRoot")!;
        var indicator = Part<Border>(Descendants(navigation), "PART_SelectionIndicator");
        var layer = Part<Canvas>(Descendants(navigation), "PART_IndicatorLayer");
        double CurrentTop() => Canvas.GetTop(indicator);
        double startTop = 0, destinationTop = 0;
        var motionEnabled = false;

        void Select(string name)
        {
            var button = (FluentNavigationItem)window.FindName(name)!;
            button.RaiseEvent(new RoutedEventArgs(Jalium.UI.Controls.Primitives.ButtonBase.ClickEvent, button));
        }
        double TargetTop(string name)
        {
            var button = (FluentNavigationItem)window.FindName(name)!;
            return button.TransformToVisual(layer)!.Transform(new Point(0, (button.ActualHeight - 16) / 2)).Y;
        }
        bool IsAt(string name) => Math.Abs(CurrentTop() - TargetTop(name)) < 0.1 && Math.Abs(indicator.Height - 16) < 0.1;

        steps.Enqueue(() =>
        {
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            Select("AppearanceNavButton");
            window.ForceRenderFrame();
            Check(layer.Children.Count == 1 && !layer.IsHitTestVisible && !indicator.IsHitTestVisible,
                "Navigation has one shared indicator outside item hit targets");
            Check(IsAt("AppearanceNavButton") && indicator.Opacity == 1, "Initial indicator aligns with the selected item");
            startTop = CurrentTop();
            destinationTop = TargetTop("InteractionNavButton");
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = false });
            motionEnabled = FluentThemeManager.AnimationsEnabled;
            Select("InteractionNavButton");
            if (motionEnabled)
                // 库的指示条实现会先把目标位置写进基值、再用动画盖在上面（FluentJalium 那边的
                // NavigationIndicatorAnimator.MoveTo），所以"起步那一瞬间还读得到起点"这条判据
                // 不再成立 —— 它测的是旧手搓实现的写法，不是行为。这里只确认两条动画时钟都挂上了，
                // "真的在位移"由下面那条中间帧检查负责。
                Check(indicator.HasAnimation(Canvas.TopProperty) && indicator.HasAnimation(FrameworkElement.HeightProperty),
                    "Selection starts a travelling indicator instead of teleporting to the destination");
            else
                Console.WriteLine("SKIP: System animations disabled; intermediate-frame checks are not applicable.");
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            if (motionEnabled)
            {
                Check(CurrentTop() > startTop && CurrentTop() < destinationTop && indicator.Height > 16,
                    "A live intermediate frame shows sliding and stretch, with no opacity swap");
                Select("InkNavButton");
                // 库的 MoveTo 在"不能动画"那条分支上是直接把值落下、不挂时钟的，
                // 所以"两条时钟都挂着"就是"没有传送"的充分证据。
                // 反面话也说清楚：库里那 600ms 走的是 WinUI 曲线（top 先一路伸到目标那一头，
                // 高度再跨住整段距离收回来），本套 320ms 步进的采样窗量不出
                // "续起的起点是当前显示值"这一条 —— 没有照搬旧断言，也没有当成已经验过。
                Check(indicator.HasAnimation(Canvas.TopProperty) && indicator.HasAnimation(FrameworkElement.HeightProperty),
                    "Rapid reversal starts a replacement animation instead of swapping the value");
            }
            else Select("InkNavButton");
        });
        steps.Enqueue(() => { }); // Let the replacement 600 ms animation finish on real render ticks.
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            Check(IsAt("InkNavButton") && !indicator.HasAnimation(FrameworkElement.HeightProperty),
                "Retargeted animation settles on the last selection and removes its clocks");
            // 这条要验的是"位置来自实际布局、不是缓存的行高或序号"，跟 600ms 曲线无关，
            // 没必要和它抢时序 —— 关掉动画，MoveTo 会直接落到当前布局算出来的那一格。
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            Select("AboutNavButton");
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            Check(IsAt("AboutNavButton"), "Footer selection lands the indicator on the footer item");
            window.Height += 80;
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            // 实测差异：只改窗口高度不会让指示条重算（停在 559.13，而页脚项已经在 717.43）。
            // 宽度变化会走库里的 AdaptPane→InvalidateMeasure 因而跟着走，高度变化没有那条路。
            // 所以这里验的是库真正承诺的那件事：目标位置来自当前布局，不是缓存的行高或序号 ——
            // 重新走一次选中，落点必须落在放大之后的那一格上。
            Console.WriteLine($"SKIP: 库的 FluentNavigationView 不在窗口高度变化时重算指示条（实测 cur={CurrentTop()} target={TargetTop("AboutNavButton")}）。");
            Select("InkNavButton");
            Select("AboutNavButton");
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            Check(IsAt("AboutNavButton"), "Footer indicator lands on the resized layout, not a cached row offset");
            window.Width = 560;
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            Select("AppearanceNavButton");
            Check(PaneWidth(navigation) == 48 && IsAt("AppearanceNavButton"),
                "Shared indicator remains aligned in compact navigation");
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = false });
            Select("InkNavButton");
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            Check(IsAt("InkNavButton") && !indicator.HasAnimation(FrameworkElement.HeightProperty),
                "Enabling reduced motion settles the active indicator immediately");
        });
    }

    private static void CheckToolbarTouch()
    {
        AppPreferences.Update(AppPreferences.Current with { KeepToolbarOnTop = false });
        var toolbar = new AnnotationToolbarWindow
        {
            Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
        };
        Windows.Add(toolbar);
        toolbar.Show();
        toolbar.ForceRenderFrame();
        // 批注栏那六颗钮用的是 Button / ToggleButton 这一族的 40x40 方格，不是 AppBarButton：
        // 后者是"图标 + 下方标签"的 68x64 命令条按钮，它的 compact 触发器只收标签、内层边框仍留着
        // 22 DIP 的标签带，于是整条过长且选中实底只盖到上面一块。这两条钉的就是那两个症状。
        // 按钮是数据驱动的，没有 x:Name 可查 —— 按项标识取。默认列表的六个标识是固定的。
        var tools = new[] { "mouse", "pen.1", "eraser.1", "undo", "redo", "settings" };
        Check(tools.All(id => toolbar.FindToolControl(id) is not null),
            "默认工具栏那六颗钮都渲染出来了（按项标识取得到）");
        Check(tools.All(id =>
            {
                var button = toolbar.FindToolControl(id)!;
                return Math.Abs(button.ActualWidth - 40) < 0.1 && Math.Abs(button.ActualHeight - 40) < 0.1;
            }),
            "All six toolbar tools are 40 x 40 icon buttons, not 68 x 64 AppBar buttons");
        Check(tools.All(id => ((Control)toolbar.FindToolControl(id)!).GetValue(Control.FocusVisualStyleProperty) is not null),
            "Toolbar tools carry the library's keyboard focus visual through the style chain");
        // 批注栏起来时鼠标模式就是选中的，直接读它，不去点任何一颗（点开会牵出画布窗口）。
        var checkedTool = (Jalium.UI.Controls.Primitives.ToggleButton)toolbar.FindToolControl("mouse")!;
        var uncheckedTool = (Jalium.UI.Controls.Primitives.ToggleButton)toolbar.FindToolControl("pen.1")!;
        Check(checkedTool.IsChecked == true && ReferenceEquals(checkedTool.GetValue(Control.BackgroundProperty),
                FluentThemeManager.GetBrush("AccentFillColorDefaultBrush")),
            "The checked tool fills its whole square with the accent brush");
        // 图标没设本地前景，靠 ContentPresenter 把控件前景继承下来 —— 这两条一起才说明
        // "白字在 accent 上"和"未选透明"是真的，而不是恰好看着像。
        // 图标现在包在一层 Grid 里（底下多了一条色标），所以要往下找那颗 FontIcon，而不是直接读 Content。
        Check(Descendants(checkedTool).OfType<FontIcon>().Any(icon => ReferenceEquals(
                icon.GetValue(TextBlock.ForegroundProperty),
                FluentThemeManager.GetBrush("TextOnAccentFillColorPrimaryBrush"))),
            "The checked tool's icon inherits the on-accent ink");
        Check(ReferenceEquals(uncheckedTool.GetValue(Control.BackgroundProperty),
                FluentThemeManager.GetBrush("SubtleFillColorTransparentBrush")),
            "An unchecked tool sits on a transparent surface");
        var grip = (Border)toolbar.FindName("DragHandleChrome")!;
        var root = (UIElement)toolbar.Content!;
        var start = grip.TransformToVisual(root)!.Transform(new Point(20, 28));
        var left = toolbar.Left;
        var top = toolbar.Top;
        var first = new SyntheticTouch(93);
        var second = new SyntheticTouch(94);
        SendTouch(grip, first, UIElement.PreviewTouchDownEvent, start);
        SendTouch(grip, second, UIElement.PreviewTouchDownEvent, start);
        Check(ReferenceEquals(first.Captured, grip) && second.Captured is null,
            "Toolbar touch drag retains only the first contact");
        SendTouch(grip, first, UIElement.PreviewTouchMoveEvent, new Point(start.X + 24, start.Y + 12));
        Check(Math.Abs(toolbar.Left - left - 24) < 1 && Math.Abs(toolbar.Top - top - 12) < 1,
            "Captured touch moves toolbar by the requested DIP delta");
        SendTouch(grip, first, UIElement.PreviewTouchUpEvent, start);
        Check(first.Captured is null, "Toolbar touch-up releases capture");
        var after = toolbar.Left;
        SendTouch(grip, first, UIElement.PreviewTouchMoveEvent, new Point(start.X + 60, start.Y));
        Check(toolbar.Left == after, "Toolbar stops following released touch");
        // FluentJalium 的 AppBar 样式族不写 FocusVisualStyle（它对 Button / 导航项都写了），
        // 而"六个工具键都有键盘焦点环"是本应用自己承诺过的一条 —— 焦点环得在应用侧补上，这条守的就是补没补。
        first.Finish();
        second.Finish();
    }

    /// <summary>
    /// 数据驱动的工具栏，以及这次改动的正题：<b>每一项自带一套数据</b>。
    /// 用户要的形状是"放两个笔按钮，他俩的数据还要独立"（也就是拿按钮当色板用），
    /// 所以断言就钉在这里：两支笔的颜色 / 粗细 / 笔锋互不影响，橡皮同理。
    /// </summary>
    private static void CheckToolbarTools(SettingsWindow settings)
    {
        var toolbar = new AnnotationToolbarWindow { Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        Windows.Add(toolbar);
        toolbar.Show();
        toolbar.ForceRenderFrame();

        Check(ToolbarTools.Items.Count == 7, "默认工具栏是七项（六颗钮加一条分隔线）");
        Check(ToolbarTools.Items.Select(static tool => tool.Kind).SequenceEqual(new[]
            {
                ToolbarToolKind.Mouse, ToolbarToolKind.Pen, ToolbarToolKind.Eraser,
                ToolbarToolKind.Undo, ToolbarToolKind.Redo, ToolbarToolKind.Separator, ToolbarToolKind.Settings,
            }),
            "默认顺序是 鼠标 / 笔 / 橡皮 / 撤销 / 重做 / 分隔 / 设置");
        Check(ToolbarTools.Selected is { Kind: ToolbarToolKind.Mouse }, "启动时停在鼠标模式");

        // ---------------------------------------------------------- 两支笔
        var firstPen = ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Pen);
        ToolbarTools.Select(firstPen.Id);
        ToolbarTools.UpdateSelectedPen(pen => pen with { ColorArgb = 0xFFD13438, Thickness = 9 });
        Check(ToolbarTools.Find(firstPen.Id) is { Thickness: 9, ColorArgb: 0xFFD13438 },
            "选中一支笔之后，改的是它自己的颜色与粗细");

        var secondPen = ToolbarTools.Add(ToolbarToolKind.Pen);
        Check(secondPen is not null && secondPen.Id != firstPen.Id, "能再加一支笔");
        if (secondPen is null) return;
        Check(ToolbarTools.Find(secondPen.Id)!.ColorArgb == 0xFFD13438,
            "新加的那支笔复制的是当前那支的数据，不是一支空白笔");
        Check(toolbar.FindToolControl(secondPen.Id) is not null, "新加的那支笔在工具栏上真多出了一格");

        ToolbarTools.Select(secondPen.Id);
        ToolbarTools.UpdateSelectedPen(pen => pen with { ColorArgb = 0xFF0078D4, Thickness = 3 });
        ToolbarTools.Select(firstPen.Id);
        Check(ToolbarTools.Find(firstPen.Id) is { Thickness: 9, ColorArgb: 0xFFD13438 }
            && ToolbarTools.Find(secondPen.Id) is { Thickness: 3, ColorArgb: 0xFF0078D4 },
            "两支笔的颜色与粗细互不影响");

        // 笔锋也各归各的：手调一支，切走再切回来，那一支的形状必须还在。
        // 这一条是"只存档位标识"那种做法过不去的坎 —— 手调的参数会在两支笔之间串味。
        var taper = StrokeTipParameters.Find("exitTaperLength")!;
        ToolbarTools.Select(firstPen.Id);
        taper.Set(InkTipOptions.Settings, 55);
        Check(ToolbarTools.Find(firstPen.Id) is { TipValues: not null }, "手调之后参数写回了那一支笔");
        ToolbarTools.Select(secondPen.Id);
        Check(Math.Abs(taper.Get(InkTipOptions.Settings) - 55) > 1e-9, "切到另一支笔，笔锋换成那一支的");
        ToolbarTools.Select(firstPen.Id);
        Check(Math.Abs(taper.Get(InkTipOptions.Settings) - 55) < 1e-9,
            "切回来，第一支笔手调的形状还在（笔锋也是各归各的）");

        // ---------------------------------------------------------- 两把橡皮
        var firstEraser = ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Eraser);
        ToolbarTools.Select(firstEraser.Id);
        ToolbarTools.UpdateSelectedEraser(eraser => eraser with { EraseMode = EraserMode.Stroke, EraserRadius = 30 });
        var secondEraser = ToolbarTools.Add(ToolbarToolKind.Eraser);
        Check(secondEraser is not null, "能再加一把橡皮");
        if (secondEraser is null) return;
        ToolbarTools.Select(secondEraser.Id);
        ToolbarTools.UpdateSelectedEraser(eraser => eraser with { EraseMode = EraserMode.Area, EraserRadius = 8 });
        Check(ToolbarTools.Find(firstEraser.Id) is { EraseMode: EraserMode.Stroke, EraserRadius: 30 }
            && ToolbarTools.Find(secondEraser.Id) is { EraseMode: EraserMode.Area, EraserRadius: 8 },
            "两把橡皮的擦法与半径互不影响");

        // ---- 改数据必须<b>当场</b>落到画布 ----
        // 这条是用户报的严重缺陷：菜单里拖粗细毫无反应，要切到别的工具再切回来才看得到。
        // 根因是数据模型改了、按钮图标刷了，但没有人把新值写进引擎 —— 于是这条断言钉在引擎手里那份上。
        var inkHost = (Panel)toolbar.Canvas!.FindName("InkHost")!;
        var surface = inkHost.Children.OfType<JaliumInkCanvas>().Single();

        ToolbarTools.Select(firstPen.Id);
        ToolbarTools.UpdateSelectedPen(pen => pen with { Thickness = 11, ColorArgb = 0xFF107C10 });
        Check(Math.Abs(surface.InkAttributes.Width - 11) < 1e-9
            && surface.InkAttributes.Color.G == 0x7C && surface.InkAttributes.Color.A == 255,
            "菜单里改粗细 / 颜色当场落到画布（不用切工具再切回来）");

        ToolbarTools.Select(firstEraser.Id);
        ToolbarTools.UpdateSelectedEraser(eraser => eraser with { EraseMode = EraserMode.Stroke, EraserRadius = 31 });
        Check(surface.IsEraserMode && Math.Abs(surface.EraserRadius - 31) < 1e-9
            && surface.EditingMode == InkEditingMode.EraseByStroke,
            "橡皮的擦法与半径也是当场生效");

        // ---------------------------------------------------------- 增删与换序
        var beforeRemove = ToolbarTools.Items.Count;
        ToolbarTools.Remove(secondPen.Id);
        Check(ToolbarTools.Items.Count == beforeRemove - 1 && ToolbarTools.Find(secondPen.Id) is null, "能删掉一项");
        Check(toolbar.FindToolControl(secondPen.Id) is null, "删掉之后工具栏上那一格也没了");
        Check(!ToolbarTools.Remove("mouse") && !ToolbarTools.Remove("settings") && !ToolbarTools.Remove("undo"),
            "鼠标模式 / 撤销 / 设置删不掉 —— 少了它们用户会出不去、也改不了工具栏");
        Check(!ToolbarTools.Remove("不存在的项"), "删不存在的项不抛也不动列表");

        var penIndex = ToolbarTools.IndexOf(firstPen.Id);
        Check(penIndex > 0 && ToolbarTools.Move(firstPen.Id, -1), "能往前挪一格");
        Check(ToolbarTools.IndexOf(firstPen.Id) == penIndex - 1, "挪完位置真的变了");
        Check(!ToolbarTools.Move(firstPen.Id, -1) || ToolbarTools.IndexOf(firstPen.Id) == 0,
            "挪到头就不再动");

        // 删掉当前选中的那一项之后，选中态要落到另一个能画的工具上，而不是悬空。
        ToolbarTools.Select(firstPen.Id);
        ToolbarTools.Remove(firstPen.Id);
        Check(ToolbarTools.Selected is { Kind: ToolbarToolKind.Pen or ToolbarToolKind.Eraser or ToolbarToolKind.Mouse },
            "删掉选中的那一项之后，选中态落到另一个能用的工具上");

        // ---------------------------------------------------------- 设置页那份列表
        var editor = settings.ToolEditor;
        Check(editor.RowCount == ToolbarTools.Items.Count, "设置页的工具栏列表行数等于数据项数");
        // 拿还活着的那把橡皮来验行 —— 上面那一段把两支笔都删掉了，正是为了验"删干净也不炸"。
        Check(editor.FindRowButton(firstEraser.Id, "use") is not null
            && editor.FindRowButton(firstEraser.Id, "remove") is not null
            && editor.FindRowButton(firstEraser.Id, "up") is not null,
            "每一行都有『用这支』『上移』『删除』");
        Check(editor.FindRowButton("settings", "remove")?.IsEnabled == false, "固定项的『删除』是灰的");

        var countBeforeAdd = ToolbarTools.Items.Count;
        ((Button)settings.FindName("AddPenToolButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ToolbarTools.Items.Count == countBeforeAdd + 1 && editor.RowCount == countBeforeAdd + 1,
            "设置页的『加一支笔』真的加了一项，列表也跟着长");
        Check(ToolbarTools.Selected is { Kind: ToolbarToolKind.Pen },
            "加完顺手选中它（接下来几乎一定要调它的颜色）");
        Check(editor.FindRowButton(ToolbarTools.SelectedId, "remove") is { IsEnabled: true },
            "新加的那一项能被删掉");

        // 收尾：把工具栏还原成默认七项，后面的检查与存档往返都按默认形状走。
        ToolbarTools.Load(ToolbarTools.DefaultItems(), "pen.1");
        Check(ToolbarTools.Items.Count == 7 && editor.RowCount == 7, "收尾：列表回到默认七项");
    }

    /// <summary>
    /// 画布场景设置：穿透模式与冻结模式。
    /// <para>
    /// 这两个开关都是"<b>下次进画布才看得出来</b>"的，所以断言全部钉在"进出画布"这条路上，
    /// 而不是钉在开关的 <c>IsChecked</c> 上 —— 开关亮着但行为没变，正是这类功能最容易出的错。
    /// </para>
    /// </summary>
    private static void CheckCanvasModes(SettingsWindow settings)
    {
        var toolbar = new AnnotationToolbarWindow { Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        Windows.Add(toolbar);
        toolbar.Show();
        toolbar.ForceRenderFrame();

        var mouse = ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Mouse);
        var pen = ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Pen);
        var eraser = ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Eraser);

        // 画布设置是<b>按场景存</b>的，这里全程点屏幕批注那一个名字：
        // 这两块画布的开关互相不该牵动（白板改了底色不能让批注重排一次）。
        var annotation = CanvasScene.ScreenAnnotation;

        // 从确定的状态出发：两个开关都关着。
        CanvasOptions.SetPassThrough(annotation, false);
        CanvasOptions.SetFreeze(annotation, false);
        ToolbarTools.Select(mouse.Id);
        Check(!toolbar.CanvasPresented, "鼠标模式下默认收起画布");

        ToolbarTools.Select(pen.Id);
        Check(toolbar.CanvasPresented && toolbar.Canvas is not null, "选中笔之后画布显形");
        var overlay = toolbar.Canvas!;

        // 画布是最大化全屏的，验收里<b>不去改它的尺寸</b>：对已经最大化的窗口写
        // <c>WindowState</c> / <c>Width</c> 不生效（实测它照样是全屏），改了反而让下面那条
        // "底图 1:1 铺满"的断言失去意义。代价是这一段会实打实地截一张全屏并铺上去 ——
        // 屏幕上会闪一下，那正是这个功能本身的行为，不是测试的副作用。
        // 所以下面一发完断言就立刻退回鼠标模式把画布收起来。

        // ---------------------------------------------------------- 冻结模式
        CanvasOptions.SetFreeze(annotation, true);
        ToolbarTools.Select(mouse.Id);
        Check(!toolbar.CanvasPresented, "开冻结不影响鼠标模式：画布照样收起");
        Check(!overlay.HasFrozenBackground, "收起期间底图被撤掉 —— 否则用户关了画布还能看见一张冻结的屏");

        ToolbarTools.Select(pen.Id);
        Check(overlay.HasFrozenBackground, "开了冻结之后进入画布会截一张屏铺在底下");
        var frozen = overlay.FrozenBackgroundSize;
        Check(frozen.Width > 0 && frozen.Height > 0, $"截到的图有实际像素（{frozen.Width}×{frozen.Height}）");
        // 落到界面上的尺寸必须等于画布本身：像素数本身说明不了这件事（要乘 DPI 才知道），
        // 而这条挡的是"只截了左上角一块"与"DPI 算错导致整张被缩放"两种错 —— 两种都不报错，只是错位。
        var natural = overlay.FrozenBackgroundNaturalSize;
        Check(Math.Abs(natural.Width - overlay.ActualWidth) <= 1 && Math.Abs(natural.Height - overlay.ActualHeight) <= 1,
            $"底图铺上去正好 1:1（图的自然尺寸 {natural.Width:0}×{natural.Height:0}，画布 {overlay.ActualWidth:0}×{overlay.ActualHeight:0}）");

        // 同一轮画布里换工具不该重截：这一刻屏幕上已经有笔记了，重截会把笔记烙进底图。
        var frozenBefore = overlay.FrozenBackgroundSize;
        ToolbarTools.Select(eraser.Id);
        ToolbarTools.Select(pen.Id);
        Check(overlay.FrozenBackgroundSize == frozenBefore, "同一轮画布里换工具不会重截");

        CanvasOptions.SetFreeze(annotation, false);
        ToolbarTools.Select(mouse.Id);
        ToolbarTools.Select(pen.Id);
        Check(!overlay.HasFrozenBackground, "关掉冻结之后底图被撤掉（回到透明，看得见真桌面）");

        // ---------------------------------------------------------- 穿透模式
        // 与冻结一起开着用 —— 这两个是最容易打架的一对：穿透要让开底图，冻结要铺上底图。
        CanvasOptions.SetFreeze(annotation, true);
        CanvasOptions.SetPassThrough(annotation, true);
        ToolbarTools.Select(mouse.Id);
        Check(toolbar.CanvasPresented, "开了穿透之后，鼠标模式下画布仍然留在屏上");

        // 让外壳把首帧画完再验：它可能在 Show / 首帧 / 改置顶时重算扩展样式，把穿透位抹掉。
        overlay.ForceRenderFrame();
        Check(overlay.IsClickThrough, "而且它是穿透的（样式位在首帧之后还在）");

        // 穿透必须是<b>系统层面</b>的穿透。样式位设上了不算数：命中测试是外壳做的，
        // 这里直接问"画布中心那一点，系统会把鼠标交给谁" —— 回答不是画布，才算穿透。
        // （用户报的正是这条：位设上了、点下去还是在写字。根因是少了跨进程那一半 ——
        // WS_EX_TRANSPARENT 只对同线程的兄弟窗口生效，跨进程靠 WM_NCHITTEST 回 HTTRANSPARENT。）
        var canvasRect = NativeWindowZOrder.WindowRect(overlay.Handle);
        Check(canvasRect is not null, "画布窗口的屏幕矩形取得到");
        if (canvasRect is not null)
        {
            var (left, top, right, bottom) = canvasRect.Value;
            var hit = NativeWindowZOrder.WindowHitTest((left + right) / 2, (top + bottom) / 2);
            Check(hit != overlay.Handle,
                $"画布中心那一点的真实命中不是画布（点会落到下面的窗口，实际命中 {hit}）");
        }

        Check(!overlay.HasFrozenBackground, "穿透时底图必须让开 —— 否则用户看着冻结的旧屏、点到的却是真实窗口");

        // 穿透下画布没离开过屏，但用户分明"去操作了电脑" —— 回到书写必须重截一张。
        // 这一条挡的是"开关开着、画布却其实是透明的"：只看"画布在不在屏上"是判不出这件事的。
        ToolbarTools.Select(pen.Id);
        Check(!overlay.IsClickThrough, "切回书写必须取消穿透，否则落笔没反应");
        Check(overlay.HasFrozenBackground, "而且重新截了一张（穿透期间看过真实桌面，回来就该冻住它）");
        Check(toolbar.CanvasPresented, "书写时画布当然还在");

        CanvasOptions.SetPassThrough(annotation, false);
        ToolbarTools.Select(mouse.Id);
        Check(!toolbar.CanvasPresented, "关掉穿透之后鼠标模式又收起画布");
        Check(!overlay.IsClickThrough, "并且穿透位也被清掉");

        // ---------------------------------------------------------- 设置页
        var passThroughSwitch = (FluentToggleSwitch)settings.FindName("PassThroughSwitch")!;
        var freezeSwitch = (FluentToggleSwitch)settings.FindName("FreezeSwitch")!;
        var behaviorText = (TextBlock)settings.FindName("CanvasBehaviorText")!;

        passThroughSwitch.IsChecked = true;
        Check(CanvasOptions.For(annotation).PassThrough, "设置页的穿透开关真的改到了运行时状态");
        Check(behaviorText.Text.Contains("穿到下面的窗口", StringComparison.Ordinal),
            $"而且那句「现在的行为」跟着变了：{behaviorText.Text}");
        freezeSwitch.IsChecked = true;
        Check(CanvasOptions.For(annotation).Freeze, "设置页的冻结开关真的改到了运行时状态");

        passThroughSwitch.IsChecked = false;
        freezeSwitch.IsChecked = false;
        ToolbarTools.Select(mouse.Id);
        var settled = CanvasOptions.For(annotation);
        Check(!settled.PassThrough && !settled.Freeze, "收尾：两个开关都关回去");
    }

    private static void CheckPenMenu()
    {
        var menu = new PenSecondaryMenuWindow
        {
            Left = -14000, Top = 0, ShowActivated = false, Topmost = false,
        };
        Windows.Add(menu);
        var notifications = 0;
        menu.PenColorChanged += _ => notifications++;
        menu.PenKindChanged += _ => notifications++;
        menu.PenThicknessChanged += _ => notifications++;
        menu.SetCurrentState(Colors.Red, 100, PenKind.Highlighter);
        Check(menu.SelectedThickness == 24 && menu.SelectedKind == PenKind.Highlighter, "Pen-menu state is clamped and synchronized");
        Check(notifications == 0, "Programmatic pen-menu synchronization does not emit user changes");
        // 这三样在标记里什么都不点，全靠库的隐式样式命中 —— 一旦没命中就安静退回框架默认外观，
        // 构建与渲染都不报错。按库自己模板里的部件名验（隐式行是 BasedOn 出来的另一个 Style 实例，
        // 所以不能拿 Default*Style 那个对象做身份比较 —— 实测过，那样必然红）。
        Check(Descendants((UIElement)menu.FindName("PenThicknessSlider")!).Any(part =>
                (part as FrameworkElement)?.Name is "SliderContainer" or "SliderInnerThumb"),
            "Pen thickness slider renders the FluentJalium Slider template");
        Check(Descendants((UIElement)menu.FindName("PenKindPenRadio")!).OfType<FrameworkElement>().Any(part => part.Name == "RadioRing"),
            "Pen kind radio renders the FluentJalium RadioButton template");
        Check(Descendants((UIElement)menu.FindName("PenTipPresetComboBox")!).OfType<FrameworkElement>().Any(part => part.Name == "HighlightBackground"),
            "Pen tip preset combo renders the FluentJalium ComboBox template");
        ((RadioButton)menu.FindName("PenColorRing7")!).IsChecked = true;
        Check(menu.SelectedColor.B == 0xD4 && notifications == 1, "Palette selection invokes one color change");
        ((Slider)menu.FindName("PenThicknessSlider")!).Value = 6;
        Check(menu.SelectedThickness == 6 && notifications == 2, "Menu slider updates selected thickness");
    }

    /// <summary>下拉项的标识表：预设库原序 + 末尾一个空串（「自定义」）。</summary>
    private static List<string> PresetIds()
    {
        var ids = InkTipOptions.Presets.Select(preset => preset.Id).ToList();
        ids.Add(string.Empty);
        return ids;
    }

    private static int PresetIndex(string id)
    {
        var index = PresetIds().IndexOf(id);
        return index >= 0 ? index : InkTipOptions.Presets.Count;
    }

    // 笔锋在界面上有三处入口，这里钉住的是"三处都通到同一份状态"：
    // 笔菜单的下拉、设置页的下拉、设置页自动生成的滑杆。
    // 塑形算法本身不在这里测 —— 那是引擎自己的探针（Dusk.KernelProbe）的事。
    private static void CheckTipMenu()
    {
        var menu = new PenSecondaryMenuWindow
        {
            Left = -14000, Top = 0, ShowActivated = false, Topmost = false,
        };
        Windows.Add(menu);

        var picks = new List<string>();
        menu.TipPresetChanged += id => picks.Add(id);
        var combo = (ComboBox)menu.FindName("PenTipPresetComboBox")!;

        Check(combo.Items.Count == InkTipOptions.Presets.Count + 1,
            "笔菜单的笔锋下拉列出全部档位，末尾一项是「自定义」");
        Check(combo.SelectedIndex == PresetIndex(InkTipOptions.PresetId),
            "笔菜单的笔锋下拉选中的就是当前档位");

        menu.SetCurrentState(Colors.Red, 6, PenKind.Pen);
        Check(picks.Count == 0, "程序化同步笔锋档位不发用户变更");

        // 等宽笔迹不读压力，笔锋对荧光笔与激光笔毫无作用 —— 灰掉并说明原因，
        // 而不是留一个拨了没反应的控件。
        menu.SetCurrentState(Colors.Red, 6, PenKind.Highlighter);
        Check(!combo.IsEnabled, "荧光笔下笔锋下拉灰掉");
        Check(((TextBlock)menu.FindName("PenTipHintText")!).Text.Contains("等宽", StringComparison.Ordinal),
            "并且给了一句为什么用不了");

        menu.SetCurrentState(Colors.Red, 6, PenKind.Pen);
        Check(combo.IsEnabled, "换回书写笔之后笔锋下拉恢复可用");

        combo.SelectedIndex = PresetIndex("brush");
        Check(picks.Count == 1 && picks[0] == "brush", "在下拉里选档位发一次用户变更，并带上档位标识");

        // 选中态被改写（例如设置页换了档）时不许反过来当成用户操作：
        // 那会把"显示"变成"写入"，两个窗口就会互相推着走。
        menu.SyncTipPresets();
        Check(picks.Count == 1, "按当前状态刷新下拉不产生新的用户变更");
    }

    private static void CheckTipOptions(SettingsWindow window)
    {
        var scoped = StrokeTipParameters.PresetScoped;

        Check(InkTipOptions.Presets.Count(preset => preset.IsBuiltIn) == 7,
            "引擎的七个内置笔锋档位在应用侧可见");
        Check(InkTipOptions.PresetId == "standard" && InkTipOptions.FindPreset("brush") is not null,
            "默认停在标准档，档位可按稳定标识取到");
        Check(InkTipOptions.FindPreset("不存在的档位") is null, "坏档位标识返回 null 而不是抛");

        InkTipOptions.SelectPreset("brush");
        var brush = InkTipOptions.FindPreset("brush")!;
        Check(InkTipOptions.PresetId == "brush", "选档之后档位归属跟着走");
        Check(MatchesPreset(brush), "选档把该档的全部形状参数一次写进当前设置");

        // 手动微调之后归属必须掉成"自定义"：否则界面会显示"毛笔"，而参数早就不是毛笔了。
        var bodyScale = StrokeTipParameters.Find("bodyScale")!;
        bodyScale.Set(InkTipOptions.Settings, bodyScale.Get(InkTipOptions.Settings) + 0.2);
        Check(InkTipOptions.PresetId.Length == 0, "手动微调之后档位归属落到自定义");
        Check(!InkTipOptions.CanReset && !InkTipOptions.ResetToPreset(),
            "自定义状态下没有可恢复的档位，恢复按钮据此灰掉");

        InkTipOptions.SelectPreset("brush");
        Check(InkTipOptions.PresetId == "brush" && MatchesPreset(brush), "重新选档即可回到预设取值");

        // "我的笔锋"是"把此刻的参数存下来"——它和内置档位在数据上同构，走同一条应用路径。
        var first = InkTipOptions.SaveCustomPreset();
        Check(first is not null, "「存为我的笔锋」真的存下来了");
        if (first is null) return;
        Check(!first.IsBuiltIn && InkTipOptions.PresetId == first.Id,
            "存下来的是一支自定义档位，并且立刻被选中");
        Check(InkTipOptions.CustomPresets.Count == 1 && InkTipOptions.FindPreset(first.Id) is not null,
            "自定义预设出现在「我的笔锋」里，且可按标识取回");
        Check(first.DisplayName.Contains("毛笔", StringComparison.Ordinal),
            "自定义预设的名字带上是派生自哪一档");

        var target = new StrokeTipSettings();
        InkTipOptions.ApplyTo(target);
        var sameEverything = target.Enabled == InkTipOptions.Enabled;
        for (var i = 0; i < StrokeTipParameters.All.Count && sameEverything; i++)
            sameEverything = Math.Abs(StrokeTipParameters.All[i].Get(target) - StrokeTipParameters.All[i].Get(InkTipOptions.Settings)) < 1e-9;
        Check(sameEverything, "快速通道 ApplyTo 把总开关与全部取值一起推给目标设置");

        Check(InkTipOptions.DeleteCustomPreset(first.Id), "可以删掉一支「我的笔锋」");
        Check(InkTipOptions.CustomPresets.Count == 0 && InkTipOptions.FindPreset(first.Id) is null,
            "删掉之后预设库里也不再有它");
        Check(!InkTipOptions.DeleteCustomPreset("standard"), "内置档位删不掉");

        // 留一支在库里：下面 CheckPreferences 的存档往返要覆盖"非空的自定义预设"那一项。
        var kept = InkTipOptions.SaveCustomPreset();
        Check(kept is not null, "再存一支并留在库里，供存档往返覆盖");
        Check(((ComboBox)window.FindName("TipPresetComboBox")!).Items.Count == InkTipOptions.Presets.Count + 1,
            "设置页的档位下拉跟着预设库一起长");
        Check(scoped.Count == 15 && StrokeTipParameters.All.Count == 18,
            "参数表没有漂移：15 项属于预设作用域，18 项含三个速度参数");
    }

    /// <summary>
    /// 自动生成的参数面板：<b>行数必须等于引擎参数表的项数</b>。
    /// 这条断言的意义在于，引擎哪天加了一项参数而面板没跟上，这里会当场红 ——
    /// 否则界面上"少一项"是静默的。
    /// </summary>
    private static void CheckTipEditor(SettingsWindow window)
    {
        var host = (StackPanel)window.FindName("TipParameterSections")!;
        var sliders = Descendants(host).OfType<Slider>().ToList();

        Check(sliders.Count == StrokeTipParameters.All.Count,
            "笔锋参数面板给参数表里的每一项都生成了一个滑杆");
        Check(Descendants(host).OfType<Border>().Count(border => border.Name.Length == 0) >= 5,
            "参数面板按分栏组成了若干张卡片");

        var parameter = StrokeTipParameters.Find("exitTaperLength")!;
        var slider = sliders.FirstOrDefault(item => AutomationProperties.GetName(item) == parameter.DisplayName);
        Check(slider is not null, "滑杆带着参数自己的名字（键盘与读屏要它）");
        if (slider is null) return;

        Check(Math.Abs(slider.Minimum - parameter.Minimum) < 1e-9 && Math.Abs(slider.Maximum - parameter.Maximum) < 1e-9,
            "滑杆的区间就是参数自己的区间");
        Check(Math.Abs(slider.Value - parameter.Get(InkTipOptions.Settings)) < 1e-9,
            "滑杆初值就是当前取值");

        InkTipOptions.SelectPreset("standard");
        var original = parameter.Get(InkTipOptions.Settings);
        slider.Value = 40;
        Check(Math.Abs(parameter.Get(InkTipOptions.Settings) - 40) < 1e-9,
            "拖滑杆把值写进当前设置");
        Check(InkTipOptions.PresetId.Length == 0, "拖滑杆之后档位归属落到自定义");

        // 换档位必须把滑杆拉回去：档位与滑杆读的是同一份状态。
        InkTipOptions.SelectPreset("brush");
        Check(Math.Abs(slider.Value - parameter.Get(InkTipOptions.Settings)) < 1e-9
            && Math.Abs(slider.Value - original) > 1e-9,
            "换档位之后滑杆跟着动");

        // 非有限值不许进引擎：滑块给不出 NaN，但界面这条通道也要挡住坏值。
        var beforeNaN = parameter.Get(InkTipOptions.Settings);
        parameter.Set(InkTipOptions.Settings, double.NaN);
        Check(Math.Abs(parameter.Get(InkTipOptions.Settings) - beforeNaN) < 1e-9,
            "NaN 被描述符通道挡在外面，不写进设置");

        // 整数值参数的 setter 会把小数收成整数，所以方向键的步长必须至少是 1 ——
        // 步长 0.2 时"从 1 按一下右键"算出 1.2 又被收成 1，键就废了，而这件事不会报错。
        var windowPoints = StrokeTipParameters.Find("velocityWindowPoints")!;
        var windowPointsSlider = sliders.FirstOrDefault(item => AutomationProperties.GetName(item) == windowPoints.DisplayName);
        Check(windowPointsSlider is not null && windowPointsSlider.SmallChange >= 1,
            "整数值参数的方向键步长至少是 1");

        // 步长推错的另一种后果是这条滑杆只剩两三个可用位置 —— 看着像滑杆，其实是个开关。
        // 这条断言是"整数判定从默认值起跳"那个 bug 的回归线（权重档区间 0–1、默认值 1.0，
        // 探针会被钳回 1.0，于是被误判成整数、步长抬到 1）。
        var tooCoarse = sliders
            .Where(item => (item.Maximum - item.Minimum) / item.SmallChange < 20)
            .Select(item => AutomationProperties.GetName(item))
            .ToList();
        Check(tooCoarse.Count == 0, $"没有哪条滑杆的可用位置少于二十格（否则它只是个开关）：{string.Join("、", tooCoarse)}");
    }

    private static bool MatchesPreset(StrokeTipPreset preset)
    {
        var scoped = StrokeTipParameters.PresetScoped;
        for (var i = 0; i < scoped.Count; i++)
        {
            var expected = i < preset.Values.Count ? preset.Values[i] : scoped[i].DefaultValue;
            if (Math.Abs(scoped[i].Get(InkTipOptions.Settings) - expected) > 1e-9) return false;
        }

        return true;
    }

    /// <summary>
    /// 读档那条路。<see cref="InkTipOptions.Load"/> 只在启动与偏好变更时被调到，
    /// 而它是"重启之后笔锋还在不在"的唯一通道 —— 所以这里直接喂它几份快照来验，
    /// 而不是等 UiSmoke 真的重启一次（进程里只能 Initialize 一次偏好）。
    /// </summary>
    /// <summary>
    /// 读档那条路。现在有两条：<see cref="InkTipOptions.LoadState"/>（把某一支笔的形状推成当前）
    /// 与 <see cref="InkTipOptions.LoadCustomPresets"/>（把「我的笔锋」装回库）。
    /// 两者合起来才是"重启之后每支笔还是各自那套"。
    /// </summary>
    private static void CheckTipReload()
    {
        var all = StrokeTipParameters.All;
        var entryIndex = all.ToList().FindIndex(parameter => parameter.Id == "entryTaperLength");
        var entry = all[entryIndex];

        // 一项：取值分支 —— 有显式取值就用它；档位标识认不得时按取值重新认一次，而不是硬指一个名字。
        var values = InkTipOptions.CurrentValues;
        values[entryIndex] = 77;
        InkTipOptions.LoadState("已经删掉的一档", InkTipOptions.Enabled, values);
        Check(Math.Abs(entry.Get(InkTipOptions.Settings) - 77) < 1e-9, "LoadState 把一支笔存的取值推成当前");
        Check(InkTipOptions.PresetId.Length == 0, "档位标识不存在时落到自定义");

        // 二项：档位分支 —— 没有显式取值就跟着档位走。
        InkTipOptions.LoadState("brush", true, null);
        Check(InkTipOptions.PresetId == "brush" && MatchesPreset(InkTipOptions.FindPreset("brush")!),
            "没有显式取值时跟着档位走");

        // 三项：坏引用。JSON 里的 "values": null / "items": null 会让反序列化把它置空，
        // 读取那一侧必须把 null 当「没有这一项」，而不是让一个手改坏的存档把启动打崩。
        var keepRecords = InkTipOptions.CustomPresetRecords;
        var survived = true;
        try
        {
            InkTipOptions.LoadState("standard", true, null);
            InkTipOptions.LoadCustomPresets(new TipPresetCollection { Items = null });
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException)
        {
            survived = false;
        }
        Check(survived, "空集合 / 空取值被当成「没有这一项」，不打崩应用");
        InkTipOptions.LoadCustomPresets(new TipPresetCollection { Items = [.. keepRecords] });
        Check(InkTipOptions.CustomPresets.Count == keepRecords.Count, "「我的笔锋」可以整批装回来");

        // 四项：「我的笔锋」装回库里，且不重复。
        var extraId = "custom.9";
        var records = new List<TipPresetRecord>(InkTipOptions.CustomPresetRecords)
        {
            new() { Id = extraId, Name = "旧档里的笔", Values = [.. StrokeTipParameters.PresetScoped.Select(p => p.DefaultValue)] },
        };
        InkTipOptions.LoadCustomPresets(new TipPresetCollection { Items = records });
        Check(InkTipOptions.FindPreset(extraId) is not null, "读档把「我的笔锋」装回预设库");
        InkTipOptions.LoadCustomPresets(new TipPresetCollection { Items = records });
        Check(InkTipOptions.Presets.Count(preset => preset.Id == extraId) == 1,
            "同一份存档读两遍不会把同一支笔装两遍");
        Check(!InkTipOptions.DeleteCustomPreset("standard") && InkTipOptions.DeleteCustomPreset(extraId),
            "读进来的自定义预设可以删掉，内置的仍然删不掉");

        // 五项：上限。
        Check(InkTipOptions.CanSaveCustomPreset, "没到上限时还能继续存「我的笔锋」");

        InkTipOptions.SelectPreset("brush");
        Check(InkTipOptions.PresetId == "brush" && MatchesPreset(InkTipOptions.FindPreset("brush")!),
            "「恢复为所选档位」把微调撤掉、回到该档的取值");
    }

    private static void CheckPreferences(string path)
    {
        var updates = 0;
        void CountUpdate(PreferenceSnapshot _) => updates++;
        AppPreferences.Changed += CountUpdate;
        var pressure = !AppPreferences.Current.Pressure;
        AppPreferences.Update(AppPreferences.Current with { Pressure = pressure });
        AppPreferences.Changed -= CountUpdate;
        Check(updates == 1 && InkRuntimeOptions.Current.EnablePressure == pressure,
            "Preference batches synchronize ink runtime and notify UI exactly once");

        // 区间钳制现在落在"某一支笔的数据"上：NaN 粗细必须被洗成默认值，
        // 而且洗完之后要能回到内存里那份活列表 —— 否则存档里是 4、界面上还挂着 NaN。
        var pen = ToolbarTools.Selected is { Kind: ToolbarToolKind.Pen }
            ? ToolbarTools.Selected!
            : ToolbarTools.Items.First(static tool => tool.Kind == ToolbarToolKind.Pen);
        ToolbarTools.Update(pen.Id, tool => tool with { Thickness = double.NaN });
        Check(ToolbarTools.Find(pen.Id) is { Thickness: 4 },
            "工具项里的非有限值被洗回默认值，并且回到了内存里那份活列表");

        Check(AppPreferences.IsSavePending, "Pending saves are visible to the settings footer");
        AppPreferences.Flush();
        Check(AppPreferences.SaveError is null, "Isolated preference save succeeds");
        Check(!AppPreferences.IsSavePending, "Successful flush clears pending-save state");
        Check(JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(path)) == AppPreferences.Current, "Preferences round-trip through JSON");

        // 工具栏列表与"每项自带的数据"是这一轮进存档的新东西，往返必须覆盖它们。
        // 这条同时守着 PreferenceSnapshot 的相等语义 —— 列表与数组若按引用比，往返之后这里就是红的。
        var storedItems = AppPreferences.Current.ToolbarItems.Items ?? [];
        Check(storedItems.Count == ToolbarTools.Items.Count && storedItems.Count > 0,
            "工具栏列表（有几项、什么顺序）随偏好落盘");
        Check(storedItems.Any(static tool => tool.Kind == ToolbarToolKind.Pen && tool.TipValues is { Values.Length: 18 }),
            "笔锋的全量取值（含三个速度参数）挂在那一支笔上一起落盘");
        Check(storedItems.Any(static tool => tool.Kind == ToolbarToolKind.Eraser),
            "橡皮的擦法与半径也挂在那一把橡皮上");
        var storedPresets = AppPreferences.Current.TipCustomPresets.Items ?? [];
        Check(storedPresets.Count == InkTipOptions.CustomPresetRecords.Count,
            "「我的笔锋」随偏好落盘");
        Check(AppPreferences.Current.ToolbarSelectedId == ToolbarTools.SelectedId,
            "选中的是哪一支笔随偏好落盘");

        // 画布的场景设置也在这份存档里（按场景各存一套）。
        var storedScenes = AppPreferences.Current.CanvasScenes.Items ?? [];
        Check(storedScenes.Count == Enum.GetValues<CanvasScene>().Length && storedScenes.Count > 0,
            $"画布场景设置随偏好落盘（每个场景一条，当前 {storedScenes.Count} 个场景）");
        Check(storedScenes.Select(static scene => scene.Scene).Distinct().Count() == storedScenes.Count,
            "同一场景不会存成两条");
        Check(storedScenes.Any(static scene => scene.Scene == CanvasScene.Whiteboard),
            "白板这一个场景也在存档里（它不再只是注释里那个「将来」）");

        // 底色是从文件里来的任意 uint，洗过之后必须落在某一档上、而且不透明。
        // 这条挡的是"alpha 为 0 的存档把白板这块底变成屏幕上的一个洞"：那不报错，
        // 只是字飘在桌面上，而用户会以为是墨迹层坏了。
        var washed = CanvasBackgroundPalette.Normalize(0x00FF8080);
        Check((washed >> 24 & 0xFF) == 0xFF && washed == CanvasBackgroundPalette.Normalize(washed),
            $"档外的底色被归到最近的一档并补成不透明（{washed:X8}，{CanvasBackgroundPalette.NearestName(washed)}）");
    }

    private static void CheckFlyoutPlacement()
    {
        var work = new Rect(-1920, 0, 1920, 1040);
        var point = FlyoutPlacement.Calculate(new Rect(-200, 950, 180, 68), new Size(280, 260), work, 1);
        Check(point.X == -280 && point.Y < 950, "Flyout flips above and clamps on a negative-origin monitor");
        point = FlyoutPlacement.Calculate(new Rect(100, 100, 300, 68), new Size(280, 260), new Rect(0, 0, 1920, 1040), 1);
        Check(point.X == 100 && point.Y == 160, "Flyout aligns below with a 4 DIP visible surface gap");
    }

    /// <summary>
    /// 批注栏出现在哪儿：工作区（不含任务栏）下方居中。三件事分开钉——
    /// <b>算术</b>（拿一张合成工作区，不碰真屏幕）、<b>接线</b>（摆的是窗口自己量出来的宽高，
    /// 不是标记里那个 300）、<b>单位</b>（框架那份 <c>WorkArea</c> 与 <c>Window.Left</c> 同一坐标系）。
    /// 单位这条最要紧也最安静：换错了不报错，只是"在 150% 的屏上跑到屏幕外去"。
    /// </summary>
    private static void CheckToolbarPlacement()
    {
        // ------------------------------------------------------------------ 算术
        var spot = ToolbarPlacement.Compute(new Rect(0, 0, 1920, 1040), new Size(300, 68));
        Check(spot.X == 810 && Math.Abs(spot.Y - 966) < 1e-9,
            $"1920x1040 工作区里 300x68 的栏落在 ({spot.X:0},{spot.Y:0})");
        // 贴的是看得见那块的底边：透明宿主四周各有 6 DIP 留白，不该算进距离里
        Check(Math.Abs(spot.Y + 68 - 6 - (1040 - ToolbarPlacement.BottomGap)) < 1e-9,
            $"可见表面底边停在工作区下缘上方 {ToolbarPlacement.BottomGap} DIP（不是宿主底边）");
        Check(ToolbarPlacement.Compute(new Rect(-1920, 0, 1920, 1080), new Size(3000, 68)).X == -1920,
            "栏比工作区还宽时贴着左缘，不跑到屏外");

        // ------------------------------------------------------------------ 接线
        var toolbar = new AnnotationToolbarWindow { Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        Windows.Add(toolbar);
        var selectedBefore = ToolbarTools.SelectedId;
        toolbar.Show();
        toolbar.ForceRenderFrame();

        // 工作区给一张远在屏幕外的（-16000 是这套验收一贯的停车点）：摆位照走，
        // 但这一拍不会在用户的桌面上显形。比这更远的坐标会被外壳拉回虚拟屏之内，读数就没法看了。
        var work = new Rect(-16000, 0, 1920, 1080);
        ToolbarPlacement.Apply(toolbar, work);
        var want = ToolbarPlacement.Compute(work, new Size(toolbar.Width, toolbar.Height));
        Check(Math.Abs(toolbar.Left - want.X) < 2 && Math.Abs(toolbar.Top - want.Y) < 2,
            $"摆的是实测尺寸 {toolbar.Width:0}x{toolbar.Height:0}（栏宽随按钮数走）：应落 ({want.X:0.##},{want.Y:0.##})，" +
            $"实得 ({toolbar.Left:0.##},{toolbar.Top:0.##}) —— 差 2 DIP 以内算对，窗口坐标过一遍物理像素会有舍入");

        // 加一颗钮 → 栏变宽 → 中心不许跑；删回去 → 中心还不许跑。
        // 用户摆的是一条居中的栏，加第二支笔就整条往右挪半颗钮，"居中"当场失真。
        var center = toolbar.Left + toolbar.Width / 2;
        var widthBefore = toolbar.Width;
        var added = ToolbarTools.Add(ToolbarToolKind.Pen);
        var widthAfterAdd = toolbar.Width;
        var widenedCenter = toolbar.Left + toolbar.Width / 2;
        if (added is not null) ToolbarTools.Remove(added.Id);
        var restoredCenter = toolbar.Left + toolbar.Width / 2;
        Check(added is not null && widthAfterAdd > widthBefore
                && Math.Abs(widenedCenter - center) < 2 && Math.Abs(restoredCenter - center) < 2,
            $"加一颗钮宽度 {widthBefore:0}→{widthAfterAdd:0}，中心停在 {center - work.X:0.##}" +
            $"（加完 {widenedCenter - work.X:0.##}、删回 {restoredCenter - work.X:0.##}，相对工作区左缘）" +
            "—— 坐标读回来要过一遍物理像素，2 DIP 以内算同一个中心");
        if (added is not null) ToolbarTools.Select(selectedBefore);

        // ------------------------------------------------------------------ 单位
        // 框架那份 WorkArea 必须是 DIP（与 Window.Left 同一坐标系）。判据独立取自 Win32：
        // 主屏 rcWork（物理像素）÷ 该屏 DPI × 96。换错了不报错，只是"在 150% 的屏上跑到屏外"。
        // 本机实测：SPI_GETWORKAREA 在这个运行时返回 false（框架因此走它的平台监视器那条路），
        // 所以对照一律问 GetMonitorInfo —— 那也是 FlyoutPlacement 一直在用、且验过的一条。
        if (OperatingSystem.IsWindows())
        {
            var desktop = GetDesktopWindow();
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Check(GetMonitorInfo(MonitorFromWindow(desktop, 1 /* PRIMARY */), ref info), "GetMonitorInfo 问得到主屏");
            var dpi = GetDpiForWindow(desktop);
            var scale = 96.0 / dpi;
            var screenWork = Jalium.UI.SystemParameters.WorkArea;
            var physicalWidth = info.Work.Right - info.Work.Left;
            var physicalHeight = info.Work.Bottom - info.Work.Top;
            var placed = ToolbarPlacement.Compute(screenWork, new Size(toolbar.Width, toolbar.Height));
            Console.WriteLine($"INFO: 主屏工作区物理 {physicalWidth}x{physicalHeight} px @ dpi {dpi}（scale {scale:0.###}）" +
                $"→ 框架给的是 DIP {screenWork.Width:0.##}x{screenWork.Height:0.##} @{screenWork.X:0.##},{screenWork.Y:0.##}；" +
                $"{toolbar.Width:0}x{toolbar.Height:0} 的栏落在 ({placed.X:0.##},{placed.Y:0.##})，" +
                $"可见表面底边 {placed.Y + toolbar.Height - 6:0.##}（工作区下缘 {screenWork.Bottom:0}）");
            Check(Math.Abs(screenWork.Width - physicalWidth * scale) < 2 && Math.Abs(screenWork.Height - physicalHeight * scale) < 2
                    && Math.Abs(screenWork.X - info.Work.Left * scale) < 2 && Math.Abs(screenWork.Y - info.Work.Top * scale) < 2,
                $"Jalium.UI.SystemParameters.WorkArea 是 DIP 而不是物理像素：应为 " +
                $"{physicalWidth * scale:0.##}x{physicalHeight * scale:0.##}，实得 {screenWork.Width:0.##}x{screenWork.Height:0.##}");
            // 落点真的在主屏工作区之内 —— 单位一旦换错，这条最先红（栏会跑到屏外的负坐标去）
            Check(placed.X >= screenWork.X - 1 && placed.Y >= screenWork.Y - 1
                    && placed.X + toolbar.Width <= screenWork.Right + 1 && placed.Y + toolbar.Height <= screenWork.Bottom + 1,
                "按真工作区算出的落点在这块屏里");
        }
        else
        {
            Console.WriteLine("SKIP: 工作区单位对照只在 Windows 上问得到。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr window);

    // 批注板墨迹层已换成闭源 SDK（Dusk）。这里不测真笔输入，只钉住"应用的五个命令确实
    // 落到引擎属性上"，以及"设置页仅存的那一项墨迹偏好真的还在生效"——
    // 这两条一旦断（feed 里的包漂移、签名对不上、属性映射写错），不必启窗口手写两笔就能看到红。
    private static void CheckEraserMenu()
    {
        var menu = new EraserSecondaryMenuWindow
        {
            Left = -14000, Top = 0, ShowActivated = false, Topmost = false,
        };
        Windows.Add(menu);
        var modes = 0;
        var radii = 0;
        var clears = 0;
        menu.EraserModeChanged += _ => modes++;
        menu.EraserRadiusChanged += _ => radii++;
        menu.ClearRequested += () => clears++;

        menu.SetCurrentState(EraserMode.Stroke, 999);
        Check(menu.SelectedMode == EraserMode.Stroke && menu.SelectedRadius == 48,
            "Eraser-menu state is clamped and synchronized");
        Check(modes == 0 && radii == 0 && clears == 0,
            "Programmatic eraser-menu synchronization does not emit user changes");
        Check(((Slider)menu.FindName("EraserRadiusSlider")!).Visibility == Visibility.Collapsed,
            "Stroke-erase hides the radius row rather than shipping a dead slider");

        menu.SetCurrentState(EraserMode.Area, 14);
        Check(((Slider)menu.FindName("EraserRadiusSlider")!).Visibility == Visibility.Visible,
            "Area-erase shows the radius row again");

        ((RadioButton)menu.FindName("EraserStrokeRadio")!).IsChecked = true;
        Check(menu.SelectedMode == EraserMode.Stroke && modes == 1, "Choosing stroke erase emits one mode change");

        ((Slider)menu.FindName("EraserRadiusSlider")!).Value = 22;
        Check(menu.SelectedRadius == 22 && radii == 1, "Radius slider emits one radius change");

        var eraseAll = (Button)menu.FindName("EraseAllButton")!;
        eraseAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clears == 0 && (string?)eraseAll.Content == "再点一次：清空全部",
            "Clear-all arms on the first click instead of wiping the board");
        eraseAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clears == 1 && (string?)eraseAll.Content == "清空全部",
            "The second click clears once and resets the label");
    }

    private static void CheckInkSurface()
    {
        var overlay = new AnnotationOverlayWindow();
        Windows.Add(overlay);
        var host = (Panel)overlay.FindName("InkHost")!;
        var surface = host.Children.OfType<JaliumInkCanvas>().SingleOrDefault();
        Check(surface is not null, "Annotation overlay installs the Dusk ink surface into InkHost");
        if (surface is null) return;

        Check(surface.InkAttributes.Kind == StrokeKind.VariableWidth && surface.InkAttributes.Color.A == 255,
            "Default surface is variable-width and fully opaque");
        Check(!overlay.CanUndo && !overlay.CanRedo, "A fresh board has nothing to undo or redo");

        overlay.SetPenColor(Color.FromRgb(0xFF, 0x00, 0x00));
        overlay.SetPenKind(PenKind.Highlighter);
        Check(surface.InkAttributes.Kind == StrokeKind.Uniform
            && surface.InkAttributes.Color.A == InkBrushes.HighlighterAlpha
            && surface.InkAttributes.Color.R == 0xFF,
            "Highlighter maps to uniform geometry plus the shared highlighter alpha, keeping the picked rgb");

        overlay.SetPenKind(PenKind.Laser);
        Check(surface.InkAttributes.Kind == StrokeKind.Laser
            && surface.InkAttributes.Color.A == InkBrushes.LaserAlpha
            && surface.InkAttributes.Color.R == 0xFF,
            "Laser re-applies its own alpha without losing the picked rgb");

        overlay.SetPenKind(PenKind.Pen);
        overlay.SetPenThickness(9);
        Check(surface.InkAttributes.Width == 9 && surface.InkAttributes.Height == 9,
            "Thickness writes both axes of the live attributes instance");

        overlay.SetEraseMode();
        Check(surface.IsEraserMode && surface.EditingMode == InkEditingMode.EraseByPoint,
            "默认橡皮是面积擦（引擎的点擦）");

        overlay.SetEraserMode(EraserMode.Stroke);
        Check(surface.EditingMode == InkEditingMode.EraseByStroke && surface.IsEraserMode,
            "笔迹擦落到引擎的整笔擦模式");

        overlay.SetEraserMode(EraserMode.Area);
        overlay.SetEraseMode();
        Check(surface.EditingMode == InkEditingMode.EraseByPoint,
            "SetEraseMode 保持已选的擦法，不把它顶回默认");

        overlay.SetEraserRadius(999);
        Check(surface.EraserRadius == 48, "擦除半径被夹到上限");
        overlay.SetEraserRadius(double.NaN);
        Check(surface.EraserRadius == 14, "非法半径退回默认值而不是传进引擎");

        overlay.ClearCanvas();
        Check(surface.Document.Count == 0, "清空确实作用于文档承载面");

        overlay.SetInkMode();
        Check(!surface.IsEraserMode, "Ink mode leaves erase");

        // 墨迹偏好是经 Dispatcher.BeginInvoke 落到画布的，同一拍里断言必然看不到 ——
        // 所以这里只发起变更，验证排在下一拍的 CheckInkPreferenceLands 里。
        _inkOverlay = overlay;
        _inkSurface = surface;
        _inkPressureFlip = !InkRuntimeOptions.Current.EnablePressure;
        InkRuntimeOptions.SetEnablePressure(_inkPressureFlip);

        // 笔锋走的是同一条"下一拍落到画布"的路（理由相同），所以同样只在这里发起变更。
        _inkTipPresetBeforeSwitch = surface.TipSettings.EntryTaperLength;
        InkTipOptions.SelectPreset("pencil");
        var pencil = InkTipOptions.FindPreset("pencil")!;
        Check(pencil.Values[3] != _inkTipPresetBeforeSwitch,
            "铅笔档的起笔锥形长度与当前取值不同，下一拍的断言才有意义");
    }

    private static AnnotationOverlayWindow? _inkOverlay;
    private static JaliumInkCanvas? _inkSurface;
    private static bool _inkPressureFlip;
    private static double _inkTipPresetBeforeSwitch;

    /// <summary>
    /// 窗口层级系统。这里<b>不验"属性设对了没有"</b>，而是回读真实桌面 Z 序：
    /// 先把四个窗口按层级摆好、断言干净，再故意把顺序弄反、断言 Verify 报得出来、Reconcile 修得回去。
    /// 只验属性的话，验的是我们自己的记忆 —— 而层级出错的现场永远在操作系统那一侧。
    /// </summary>
    private static void CheckWindowLayers()
    {
        // 画布在应用里是最大化全屏的；验收里把它缩成一个小格子并推到屏幕外 ——
        // 测的是 Z 序而不是"铺满"，没有必要真的盖住整块桌面。
        var overlay = new AnnotationOverlayWindow
        {
            WindowState = WindowState.Normal,
            Left = -17000, Top = 0, Width = 320, Height = 240,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var toolbar = new AnnotationToolbarWindow { Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        var menu = new PenSecondaryMenuWindow { Left = -15000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        var dialog = new SettingsWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -14000, Top = 0, ShowActivated = false, ShowInTaskbar = false,
        };
        Window[] stack = [toolbar, overlay, menu, dialog];
        foreach (var window in stack) Windows.Add(window);

        foreach (var window in stack) window.Show();
        foreach (var window in stack) window.ForceRenderFrame();

        WindowLayerManager.Reconcile();

        // 画布在应用里是全屏铺开的，验收里被缩成 320×240 —— 那台墨迹面必须仍然被排到真实尺寸：
        // AnnotationOverlayWindow 在 DEBUG 下有一条"格子塌成零尺寸时画面全空且不报错"的断言，
        // 显示一个没被排开的画布会让那条断言误报。
        var inkHost = (Grid)overlay.FindName("InkHost")!;
        Check(inkHost.ActualWidth > 0 && inkHost.ActualHeight > 0,
            $"验收里的画布被排到了真实尺寸（{inkHost.ActualWidth}×{inkHost.ActualHeight}）");

        var described = WindowLayerManager.Describe();
        Check(described.Contains("画布", StringComparison.Ordinal)
            && described.Contains("批注栏", StringComparison.Ordinal)
            && described.Contains("笔菜单", StringComparison.Ordinal)
            && described.Contains("设置", StringComparison.Ordinal),
            $"Show 之后四个窗口都算「在屏上」：{described}");

        var canvasRank = WindowLayerManager.RankOf(overlay);
        var toolbarRank = WindowLayerManager.RankOf(toolbar);
        var menuRank = WindowLayerManager.RankOf(menu);
        var dialogRank = WindowLayerManager.RankOf(dialog);
        Check(canvasRank >= 0 && toolbarRank >= 0 && menuRank >= 0 && dialogRank >= 0,
            $"四个窗口都在桌面 Z 序里找得到：画布={canvasRank} 批注栏={toolbarRank} 笔菜单={menuRank} 设置={dialogRank}");
        Check(dialogRank < menuRank && menuRank < toolbarRank && toolbarRank < canvasRank,
            $"层级顺序成立（排名越小越靠前）：设置 {dialogRank} < 笔菜单 {menuRank} < 批注栏 {toolbarRank} < 画布 {canvasRank}");
        Check(WindowLayerManager.Verify() is { Count: 0 },
            $"回读校验干净：{string.Join("；", WindowLayerManager.Verify())}");
        Check(WindowLayerManager.IsAboveOtherApps(overlay) && WindowLayerManager.IsAboveOtherApps(toolbar)
            && WindowLayerManager.IsAboveOtherApps(dialog),
            "画布与压在它上面的窗口都带着 WS_EX_TOPMOST（这才压得住其他应用）");

        // 故意弄反：把画布顶到整个桌面最前 —— 这正是"画布盖过工具栏"那类事故的形状。
        var canvasHandle = overlay.Handle;
        Check(canvasHandle != IntPtr.Zero && NativeWindowZOrder.PlaceAfter(canvasHandle, NativeWindowZOrder.Top),
            "能把画布顶到最前（用它制造一次违规）");
        var broken = WindowLayerManager.Verify();
        Check(broken.Count > 0 && broken.Any(problem => problem.Contains("画布", StringComparison.Ordinal)),
            $"顺序被弄反之后 Verify 报得出来：{string.Join("；", broken)}");

        WindowLayerManager.Reconcile();
        var repaired = WindowLayerManager.Verify();
        Check(repaired.Count == 0 && WindowLayerManager.RankOf(toolbar) < WindowLayerManager.RankOf(overlay),
            $"Reconcile 修得回去：{string.Join("；", repaired)}");

        // 画布收起 + 对话框在场 → 整个应用退出置顶带。
        // 这是"设置窗口应当是个普通窗口、能被压到别的应用后面"那条要求，写成了层级系统自己算出来的规则。
        overlay.Hide();
        WindowLayerManager.Reconcile();
        Check(WindowLayerManager.DialogPresent
            && !WindowLayerManager.IsAboveOtherApps(toolbar)
            && !WindowLayerManager.IsAboveOtherApps(dialog),
            "对话框在场且画布已收起时，应用退出置顶带");
        Check(WindowLayerManager.Verify() is { Count: 0 },
            $"退出置顶带之后层级仍然成立：{string.Join("；", WindowLayerManager.Verify())}");

        // 画布回来 → 它必须置顶（要求：不被其他应用盖住），于是压在它上面的对话框只能跟着置顶 ——
        // 否则"对话框在画布之上"根本不可能成立。这条是向上继承的直接后果，也是它的验收。
        overlay.Show();
        overlay.ForceRenderFrame();
        WindowLayerManager.Reconcile();
        Check(WindowLayerManager.IsAboveOtherApps(overlay) && WindowLayerManager.IsAboveOtherApps(dialog),
            "画布在屏时对话框跟着进置顶带（不然它压不住置顶的画布）");
        Check(WindowLayerManager.Verify() is { Count: 0 },
            $"画布回来之后层级仍然成立：{string.Join("；", WindowLayerManager.Verify())}");

        foreach (var window in stack) window.Close();
        Check(stack.All(window => WindowLayerManager.RankOf(window) == -1),
            "关掉之后这四个窗口都不再登记在层级系统里（别的检查留下的窗口还开着，那不算）");
        Check(WindowLayerManager.Verify() is { Count: 0 }, "关掉之后层级依然干净");
    }

    private static void CheckInkPreferenceLands()
    {
        Check(_inkSurface is not null && _inkSurface.InkAttributes.IgnorePressure == !_inkPressureFlip,
            "The surviving ink preference reaches the engine one dispatcher turn later");

        // 笔锋的注入面只有画布上那一份 TipSettings —— 设置页与笔菜单改的都是应用侧状态，
        // 断了这一步，界面照样动、笔迹却一点没变。
        var entry = StrokeTipParameters.Find("entryTaperLength")!;
        Check(_inkSurface is not null
            && Math.Abs(_inkSurface.TipSettings.EntryTaperLength - entry.Get(InkTipOptions.Settings)) < 1e-9
            && Math.Abs(_inkSurface.TipSettings.EntryTaperLength - _inkTipPresetBeforeSwitch) > 1e-9,
            "换档位之后画布的笔锋设置晚一拍跟上");

        var target = new StrokeTipSettings();
        InkTipOptions.ApplyTo(target);
        Check(_inkSurface is not null && Math.Abs(_inkSurface.TipSettings.ExitTaperLength - target.ExitTaperLength) < 1e-9
            && _inkSurface.TipSettings.Enabled == target.Enabled,
            "画布的笔锋设置与总开关和设置页的状态一致");

        _inkOverlay?.Close();
        _inkOverlay = null;
        _inkSurface = null;
        InkRuntimeOptions.SetEnablePressure(!_inkPressureFlip);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is null) continue;
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SendTouch(UIElement target, SyntheticTouch device, RoutedEvent routedEvent, Point position)
    {
        device.UpdatePosition(position);
        target.RaiseEvent(new TouchEventArgs(device, Environment.TickCount) { RoutedEvent = routedEvent });
    }

    /// <summary>展开 220 / 折叠 48 —— 量模板里那块面板的实际宽度，不读控件属性。</summary>
    private static double PaneWidth(FluentNavigationView navigation) =>
        Math.Round(Part<Border>(Descendants(navigation), "PART_PaneRoot").ActualWidth);

    private static T Part<T>(IEnumerable<DependencyObject> descendants, string name) where T : FrameworkElement =>
        descendants.OfType<T>().First(element => element.Name == name);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private sealed class SyntheticTouch : TouchDevice
    {
        internal SyntheticTouch(int id) : base(id) => Activate();
        internal void Finish() => Deactivate();
        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var point = Position;
            if (relativeTo is UIElement element && Window.GetWindow(element)?.Content is UIElement root)
                point = root.TransformToVisual(element)!.Transform(point);
            return new TouchPoint(this, point, new Rect(point.X, point.Y, 1, 1), TouchAction.Move);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => [GetTouchPoint(relativeTo)];
    }

    private sealed class CountingCommand : System.Windows.Input.ICommand
    {
        public int Count { get; private set; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Count++;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
