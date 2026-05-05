using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class SettingsWindow : Window
{
    private static readonly SolidColorBrush IconBrush = new(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush SectionIconBrush = new(Color.FromRgb(0x24, 0x24, 0x24));

    private SymbolIcon HeroSettingsIconEl => (SymbolIcon)HeroSettingsIcon!;
    private Slider PenWidthSliderEl => (Slider)PenWidthSlider!;
    private TextBlock PenWidthValueTextEl => (TextBlock)PenWidthValueText!;
    private TextBlock AboutVersionTextEl => (TextBlock)AboutVersionText!;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplySymbolIconBrushes();
        WirePenSlider();
        ApplyVersionText();
    }

    private void ApplySymbolBrushes(SymbolIcon icon, SolidColorBrush brush)
    {
        icon.Foreground = brush;
    }

    private void ApplySymbolIconBrushes()
    {
        ApplySymbolBrushes(HeroSettingsIconEl, IconBrush);
        foreach (var fe in new FrameworkElement?[]
                 {
                     SectionThemeIcon,
                     HintThemeIcon,
                     SectionInkIcon,
                     PenHintIcon,
                     StrokePreviewIcon,
                     SectionCanvasIcon,
                     EraseHintIcon,
                     AboutInfoIcon,
                     LinkExplorerIcon,
                 })
        {
            if (fe is SymbolIcon si)
                ApplySymbolBrushes(si, SectionIconBrush);
        }
    }

    private void WirePenSlider()
    {
        var s = PenWidthSliderEl;
        s.ValueChanged += (_, _) => UpdatePenWidthLabel();
        UpdatePenWidthLabel();
    }

    private void UpdatePenWidthLabel()
    {
        var v = (int)Math.Round(PenWidthSliderEl.Value);
        PenWidthValueTextEl.Text = $"{v} px";
    }

    private void ApplyVersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null)
            AboutVersionTextEl.Text = $"版本 {v.Major}.{v.Minor}.{v.Build}";
    }
}
