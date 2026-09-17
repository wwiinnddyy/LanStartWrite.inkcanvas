using System.Text.Json;
using Jalium.UI;
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

        var steps = new Queue<Action>();
        steps.Enqueue(() =>
        {
            Check(((Grid)settings.FindName("SettingsContentHost")!).Children.Count == 1, "Only the selected page is attached");
            Check(((Border)settings.FindName("NavigationPaneRoot")!).Width == 220, "Expanded navigation width");
            settings.Width = 560;
        });
        steps.Enqueue(() =>
        {
            settings.ForceRenderFrame();
            Check(((Border)settings.FindName("NavigationPaneRoot")!).Width == 48, "Native/layout resize switches navigation to compact");
            Check(((TextBlock)settings.FindName("InkNavLabel")!).Visibility == Visibility.Collapsed, "Compact navigation labels hidden");
            settings.Width = 960;
        });
        steps.Enqueue(() =>
        {
            settings.ForceRenderFrame();
            Check(((Border)settings.FindName("NavigationPaneRoot")!).Width == 220, "Expanded navigation restored after widening");
            CheckThemePalette();
            CheckSwitch(settings);
            CheckResponsiveRows(settings);
            CheckNavigation(settings);
            CheckToolbarTouch();
            CheckPenMenu();
            CheckFlyoutPlacement();
            CheckPreferences(path);
        });
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
        var original = FluentTheme.Brush("TextFillColorPrimaryBrush");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Dark });
        Check(FluentTheme.IsDark, "Dark theme state");
        Check(ReferenceEquals(original, FluentTheme.Brush("TextFillColorPrimaryBrush")), "Existing brush identity preserved when changing theme");
        Check(((SolidColorBrush)original).Color.R == 255, "Existing text brush changes to dark-theme foreground");
        AppPreferences.Update(AppPreferences.Current with { Theme = AppTheme.Light });
        Check(!FluentTheme.IsDark && ((SolidColorBrush)original).Color.R == 0, "Light theme restores existing brush");
        Check(((SolidColorBrush)FluentTheme.Brush("SystemColorWindowColorBrush")).Color == SystemColors.WindowColor,
            "Native system brushes replace upstream magenta placeholders");
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
        var action = (FrameworkElement)row.Children[1];
        row.Measure(new Size(300, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 300, row.DesiredSize.Height));
        Check(row.IsStacked && Grid.GetRow(action) == 1 && Grid.GetColumn(action) == 0,
            "Narrow settings row places action below description");
        Check(ReferenceEquals(action, row.Children[1]) && Grid.GetColumnSpan(row.Children[0]) == 2,
            "Responsive reflow preserves control identity and full-width description");
        row.Measure(new Size(600, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height));
        Check(!row.IsStacked && Grid.GetRow(action) == 0 && Grid.GetColumn(action) == 1,
            "Wide settings row restores left-text/right-control layout");
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
        Check(Descendants(ink).OfType<Border>().Any(border => border.Name == "NavigationFocus"),
            "Navigation template provides a noninteractive keyboard focus visual");
        appearance.RaiseEvent(new RoutedEventArgs(Jalium.UI.Controls.Primitives.ButtonBase.ClickEvent, appearance));
    }

    private static void QueueNavigationAnimationChecks(SettingsWindow window, Queue<Action> steps)
    {
        var indicator = (Border)window.FindName("NavigationSelectionIndicator")!;
        var layer = (Canvas)window.FindName("NavigationIndicatorLayer")!;
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
            motionEnabled = FluentTheme.AnimationsEnabled;
            Select("InteractionNavButton");
            if (motionEnabled)
                Check(Math.Abs(CurrentTop() - startTop) < 0.1 && indicator.HasAnimation(FrameworkElement.HeightProperty),
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
                var top = CurrentTop();
                var height = indicator.Height;
                Select("InkNavButton");
                Check(Math.Abs(CurrentTop() - top) < 0.1 && Math.Abs(indicator.Height - height) < 0.1,
                    "Rapid reversal resumes from the currently displayed geometry");
            }
            else Select("InkNavButton");
        });
        steps.Enqueue(() => { }); // Let the replacement 600 ms animation finish on real render ticks.
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            Check(IsAt("InkNavButton") && !indicator.HasAnimation(FrameworkElement.HeightProperty),
                "Retargeted animation settles on the last selection and removes its clocks");
            Select("AboutNavButton");
            window.Height += 80;
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            Check(IsAt("AboutNavButton"), "Footer indicator follows layout changes during window resizing");
            window.Width = 560;
        });
        steps.Enqueue(() =>
        {
            window.ForceRenderFrame();
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = true });
            Select("AppearanceNavButton");
            Check(((Border)window.FindName("NavigationPaneRoot")!).Width == 48 && IsAt("AppearanceNavButton"),
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
        Check(Descendants((UIElement)toolbar.FindName("MouseToolToggle")!).OfType<Border>().Any(border => border.Name == "ToolFocus"),
            "Toolbar tools have keyboard focus visuals without replacing drag grip");
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
        ((RadioButton)menu.FindName("PenColorRing7")!).IsChecked = true;
        Check(menu.SelectedColor.B == 0xD4 && notifications == 1, "Palette selection invokes one color change");
        ((Slider)menu.FindName("PenThicknessSlider")!).Value = 6;
        Check(menu.SelectedThickness == 6 && notifications == 2, "Menu slider updates selected thickness");
    }

    private static void CheckPreferences(string path)
    {
        var updates = 0;
        void CountUpdate(PreferenceSnapshot _) => updates++;
        AppPreferences.Changed += CountUpdate;
        var pressure = !AppPreferences.Current.Pressure;
        AppPreferences.Update(AppPreferences.Current with { Pressure = pressure, Smoothing = InkSmoothingLevel.High });
        AppPreferences.Changed -= CountUpdate;
        Check(updates == 1 && InkRuntimeOptions.Current.EnablePressure == pressure &&
            InkRuntimeOptions.Current.SmoothingLevel == InkSmoothingLevel.High,
            "Preference batches synchronize ink runtime and notify UI exactly once");
        AppPreferences.Update(AppPreferences.Current with { PenWidth = double.NaN, MinPointDistance = 999 });
        Check(AppPreferences.Current.PenWidth == 4 && AppPreferences.Current.MinPointDistance == 2.5, "Preferences reject nonfinite and out-of-range values");
        Check(AppPreferences.IsSavePending, "Pending saves are visible to the settings footer");
        AppPreferences.Flush();
        Check(AppPreferences.SaveError is null, "Isolated preference save succeeds");
        Check(!AppPreferences.IsSavePending, "Successful flush clears pending-save state");
        Check(JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(path)) == AppPreferences.Current, "Preferences round-trip through JSON");
    }

    private static void CheckFlyoutPlacement()
    {
        var work = new Rect(-1920, 0, 1920, 1040);
        var point = FlyoutPlacement.Calculate(new Rect(-200, 950, 180, 68), new Size(280, 260), work, 1);
        Check(point.X == -280 && point.Y < 950, "Flyout flips above and clamps on a negative-origin monitor");
        point = FlyoutPlacement.Calculate(new Rect(100, 100, 300, 68), new Size(280, 260), new Rect(0, 0, 1920, 1040), 1);
        Check(point.X == 100 && point.Y == 160, "Flyout aligns below with a 4 DIP visible surface gap");
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
