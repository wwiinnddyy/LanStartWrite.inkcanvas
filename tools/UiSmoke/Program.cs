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
            Check(!navigation.IsCompact && PaneWidth(navigation) == 220, "Expanded navigation width");
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
            CheckPenMenu();
            CheckTipMenu();
            CheckTipOptions(settings);
            CheckTipReload();
            CheckTipEditor(settings);
            CheckEraserMenu();
            CheckFlyoutPlacement();
            CheckPreferences(path);
            CheckInkSurface();
        });
        // 排在导航动画那组之前：那组里有一条本机常红的时序检查，而 Check() 一红就中断整个队列。
        steps.Enqueue(CheckInkPreferenceLands);
        QueueNavigationAnimationChecks(settings, steps);

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
        // 六个工具键都要真的落在 FluentJalium 的 AppBar 模板上：三个 RadioToolToggleButton 是显式点的键，
        // 另外三个 AppBarButton 靠隐式样式命中 —— 后者一旦没命中就会安静地退回框架默认外观，
        // 构建与渲染都不会报错，所以这里按模板部件名逐个验。
        Check(new[] { "MouseToolToggle", "PenToolToggle", "EraseToolToggle", "UndoToolbarButton", "RedoToolbarButton", "SettingsToolbarButton" }
            .Select(name => toolbar.FindName(name)!)
            .All(control => Descendants((UIElement)control).OfType<Border>().Any(part => part.Name == "AppBarButtonInnerBorder")),
            "All six toolbar tools render the FluentJalium AppBar template");
        Check(new[] { "MouseToolToggle", "PenToolToggle", "EraseToolToggle", "UndoToolbarButton", "RedoToolbarButton", "SettingsToolbarButton" }
            .All(name => ((Control)toolbar.FindName(name)!).GetValue(Control.FocusVisualStyleProperty) is not null),
            "Toolbar tools carry keyboard focus visuals (FluentJalium's AppBar styles set none)");
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
    private static void CheckTipReload()
    {
        var all = StrokeTipParameters.All;
        var entryIndex = all.ToList().FindIndex(parameter => parameter.Id == "entryTaperLength");
        var entry = all[entryIndex];

        // 一项：总开关
        var off = InkTipOptions.Enabled;
        InkTipOptions.Load(new PreferenceSnapshot
        {
            TipPresetId = InkTipOptions.PresetId,
            TipEnabled = !off,
            TipValues = new TipValueVector { Values = InkTipOptions.CurrentValues },
            TipCustomPresets = new TipPresetCollection { Items = [.. InkTipOptions.CustomPresetRecords] },
        });
        Check(InkTipOptions.Enabled == !off, "读档能改笔锋总开关");
        InkTipOptions.SetEnabled(off);

        // 二项：取值。档位标识认不得时必须自己重新认一次，而不是硬指一个不存在的名字。
        var values = InkTipOptions.CurrentValues;
        values[entryIndex] = 77;
        var extraId = "custom.9";
        var records = new List<TipPresetRecord>(InkTipOptions.CustomPresetRecords)
        {
            new() { Id = extraId, Name = "旧档里的笔", Values = [.. StrokeTipParameters.PresetScoped.Select(p => p.DefaultValue)] },
        };
        var snapshot = new PreferenceSnapshot
        {
            TipPresetId = "已经删掉的一档",
            TipEnabled = off,
            TipValues = new TipValueVector { Values = values },
            TipCustomPresets = new TipPresetCollection { Items = records },
        };

        InkTipOptions.Load(snapshot);
        Check(Math.Abs(entry.Get(InkTipOptions.Settings) - 77) < 1e-9, "读档把取值写进当前设置");
        Check(InkTipOptions.FindPreset(extraId) is not null, "读档把「我的笔锋」装回预设库");
        Check(InkTipOptions.PresetId.Length == 0, "存档里的档位标识已经不存在时，落到自定义而不是硬指一个名字");

        InkTipOptions.Load(snapshot);
        Check(InkTipOptions.Presets.Count(preset => preset.Id == extraId) == 1,
            "同一份存档读两遍不会把同一支笔装两遍");
        Check(!InkTipOptions.DeleteCustomPreset("standard") && InkTipOptions.DeleteCustomPreset(extraId),
            "读进来的自定义预设可以删掉，内置的仍然删不掉");

        // 三项：坏引用。JSON 里的 "values": null / "items": null 会让反序列化把它置空，
        // 读取那一侧必须把 null 当「没有这一项」，而不是让一个手改坏的存档把启动打崩。
        // 先把自定义预设与取值存一份，测完装回去 —— 后面的存档往返还要靠它们。
        var keepRecords = InkTipOptions.CustomPresetRecords;
        var keepValues = InkTipOptions.CurrentValues;
        var survived = true;
        try
        {
            InkTipOptions.Load(new PreferenceSnapshot
            {
                TipPresetId = "standard",
                TipEnabled = InkTipOptions.Enabled,
                TipValues = new TipValueVector { Values = null },
                TipCustomPresets = new TipPresetCollection { Items = null },
            });
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException)
        {
            survived = false;
        }
        Check(survived, "存了空引用的快照被当成「没有这一项」，不打崩应用");
        Check(Math.Abs(entry.Get(InkTipOptions.Settings) - 77) < 1e-9, "空取值不动已有的参数");

        // 四项：上限。存档会截断超限的自定义预设，应用侧必须用同一个数拦住 ——
        // 否则用户会看到「存进去了、下次启动少了一支」。
        Check(InkTipOptions.CanSaveCustomPreset, "没到上限时还能继续存「我的笔锋」");

        // 五项：装回去，并顺带验证「恢复为所选档位」真的撤掉了微调。
        InkTipOptions.Load(new PreferenceSnapshot
        {
            TipPresetId = "brush",
            TipEnabled = InkTipOptions.Enabled,
            TipValues = new TipValueVector { Values = keepValues },
            TipCustomPresets = new TipPresetCollection { Items = [.. keepRecords] },
        });
        Check(InkTipOptions.CustomPresets.Count == keepRecords.Count, "自定义预设可以整批装回来");

        InkTipOptions.ResetToPreset();
        Check(InkTipOptions.PresetId == "brush" && MatchesPreset(InkTipOptions.FindPreset("brush")!),
            "「恢复为所选档位」把微调撤掉、回到该档的取值");
    }

    private static void CheckPreferences(string path)
    {
        var updates = 0;
        void CountUpdate(PreferenceSnapshot _) => updates++;
        AppPreferences.Changed += CountUpdate;
        var pressure = !AppPreferences.Current.Pressure;
        AppPreferences.Update(AppPreferences.Current with { Pressure = pressure, PenWidth = 11 });
        AppPreferences.Changed -= CountUpdate;
        Check(updates == 1 && InkRuntimeOptions.Current.EnablePressure == pressure &&
            AppPreferences.Current.PenWidth == 11,
            "Preference batches synchronize ink runtime and notify UI exactly once");
        AppPreferences.Update(AppPreferences.Current with { PenWidth = double.NaN });
        Check(AppPreferences.Current.PenWidth == 4, "Preferences reject nonfinite values");
        Check(AppPreferences.IsSavePending, "Pending saves are visible to the settings footer");
        AppPreferences.Flush();
        Check(AppPreferences.SaveError is null, "Isolated preference save succeeds");
        Check(!AppPreferences.IsSavePending, "Successful flush clears pending-save state");
        Check(JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(path)) == AppPreferences.Current, "Preferences round-trip through JSON");

        // 笔锋是"数组进存档"的第一处：它的取值与自定义预设必须一起过存档往返。
        // 这一条同时守着 PreferenceSnapshot 的相等语义 —— 数组若按引用比，往返之后这里就是红的。
        Check(AppPreferences.Current.TipValues is { Values.Length: 18 },
            "笔锋的全量取值（含三个速度参数）随偏好落盘");
        var storedPresets = AppPreferences.Current.TipCustomPresets.Items ?? [];
        Check(storedPresets.Count == InkTipOptions.CustomPresetRecords.Count && storedPresets.Count > 0,
            "「我的笔锋」随偏好落盘");
        Check(AppPreferences.Current.TipPresetId == InkTipOptions.PresetId
            && AppPreferences.Current.TipEnabled == InkTipOptions.Enabled,
            "当前档位与笔锋总开关随偏好落盘");
    }

    private static void CheckFlyoutPlacement()
    {
        var work = new Rect(-1920, 0, 1920, 1040);
        var point = FlyoutPlacement.Calculate(new Rect(-200, 950, 180, 68), new Size(280, 260), work, 1);
        Check(point.X == -280 && point.Y < 950, "Flyout flips above and clamps on a negative-origin monitor");
        point = FlyoutPlacement.Calculate(new Rect(100, 100, 300, 68), new Size(280, 260), new Rect(0, 0, 1920, 1040), 1);
        Check(point.X == 100 && point.Y == 160, "Flyout aligns below with a 4 DIP visible surface gap");
    }

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
