# Project Memory - LanStartWrite.Inkcanvas

## Critical: This is a Jalium.UI project

**THIS IS NOT WPF. THIS IS NOT WinUI. THIS IS NOT Avalonia.**

This project uses **Jalium.UI** framework. All UI code, markup, and patterns must follow Jalium.UI conventions.

---

## Critical: 墨迹引擎是 Dusk，以闭源 SDK 引入

自 2026-09-23 起，本项目的墨迹引擎是兄弟仓库 `C:\git\ink\Dusk`，**以闭源 SDK 形式**进入：

| 事实 | 位置 |
|---|---|
| 唯一墨迹承载面 | `AnnotationOverlayWindow` 的 code-behind 里 `new JaliumInkCanvas()`，塞进 `AnnotationOverlayWindow.jalxaml` 的 `InkHost` |
| 包从哪来 | `C:\git\ink\Dusk\sdk\build-sdk.ps1` → 单程序集 `Dusk.dll` → `C:\git\ink\dusk-feed\` |
| 源从哪读 | `NuGet.config` 里的 `dusk-sdk` 源；**本仓库里没有 Dusk 源码，也不许加 ProjectReference** |
| 引擎文档 | `C:\git\ink\Dusk\docs\`（仅本地保留，未入库）；公开 GPL 源码仓见 Dusk 的 `PackageProjectUrl` |

三条不要碰的规矩，都是这次迁移用实测换来的：

1. **不要在 `.jalxaml` 里写 Dusk 的类型名**。引擎类型只出现在 code-behind。这样万一日后把混淆保留面从"全 public"收窄成"契约闭包"，应用侧零 diff。
2. **不要再往墨迹层加反射补丁**。本项目原先有 ~600 行围着 `Jalium.UI.Controls.InkCanvas` 打的补丁（反射取 protected `DynamicRenderer`、反射改私有 `MinPointDistance`、等距加密补点、探测喂点入口、DispatcherTimer 手搓激光淡出）——全部已删，Dusk 内核对这些都有原生对应物（`InkInputProfile` 按设备档位、`StrokeKind.Laser` + 覆盖层淡出、`GetIntermediatePoints` 直连湿墨、`Metrics` 埋点）。加回来就是重新制造上游版本依赖。
3. **`JaliumInkCanvas.Dispose()` 必须显式调**。Jalium 不代调、类型无终结器；漏掉且 Document 被外部持有会留住整棵墨迹视觉树。现有做法：`AnnotationOverlayWindow` 的 `Closed` 里调一次。

行为以 Dusk 语义为准，不还原旧手感（用户明确要求"应用侧是墨迹系统重构，不追求完全还原功能"）。当前与旧版的已知差异与当前事实：

- 橡皮有两种，由橡皮二级菜单选：**面积擦**＝引擎点擦（一笔可能被切成几段），**笔迹擦**＝整笔摘除（`InkEditingMode.EraseByStroke`）。选择落盘在 `AppPreferences.EraseMode`，半径只在选择面积擦时给出（笔迹擦用不到它，那一行直接隐藏而不是留个假滑块）。
- **清空**在二级菜单里，需二次点击确认（3 秒超时回弹）。
- **撤销/重做已接**：`AnnotationOverlayWindow` 自建 `InkHistory` 挂到文档上，工具栏两个按钮的可用态由 `Document.Changed` → `HistoryStateChanged` 驱动。一次擦除拖拽＝一步撤销（历史批开在 Dusk 的 `InkCanvasCore` 手势边界上，不在应用侧）。
- 激光笔**不进文档**、总 600ms 淡出（旧版是 600ms 停留 + 600ms 淡出），并带"亮芯 + 外扩晕"：光晕的宽度倍数与不透明度比例定在 Dusk 的 `InkGlow.For(kind)`，宿主只负责画，不自己决定观感。
- 设置页的墨迹区原有 6 项，其中 4 项（平滑等级、最小采样点距、实时采样通道、倾斜数据采集）在 Dusk 上没有注入口（内核每次落笔按设备类型自己覆盖采样档位，见 `InkCanvasCore.BeginGesture`），已下架而不是留成空壳；保留的是画笔粗细与压力感应。

### 笔锋（2026-09-24 落）

引擎侧的笔锋本来就有（`StrokeTipParameters` 描述符表 / `StrokeTipPreset` 预设向量 / `StrokeTipSettings` / `StrokeTipProfile` 兼容枚举）。这一步做的是**把它变成用户能用的东西**：预设可选、参数可调、存得下来、看得见效果。

| 事实 | 位置 |
|---|---|
| 全应用唯一一份笔锋状态 | `InkTipOptions`（app）：一个 `StrokeTipSettings` + 当前档位标识 + 「我的笔锋」 |
| 参数面板（18 行滑杆） | `StrokeTipEditor`：**整块从描述符表生成**，一行参数都没手写 |
| 试写区 | `SettingsWindow` 里一台真的 `JaliumInkCanvas`，读同一份设置 |
| 快捷选档 | `PenSecondaryMenuWindow` 的「笔锋」下拉（内置七档 + 自定义） |
| 推到画布 | `AnnotationOverlayWindow.ApplyTipOptions` → `InkTipOptions.ApplyTo(_surface.TipSettings)` |

几条换了设计也换不回来的判断：

1. **画布那份 `TipSettings` 是"被推过去的"，不是真相来源。** 应用的真相在 `InkTipOptions`；推的方式是引擎的快照往返（`CaptureSnapshot` / `ApplySnapshot`，按参数名对齐、批量写）。这样"改成什么样"只有一处可读，而参数表将来增删也不会让旧档对不上号。
2. **自定义档位的标识一律带 `custom.` 前缀**（`AppPreferences.CustomPresetIdPrefix`）。这保证它不可能与内置七档撞名 —— 否则存档就能绕过引擎那条"内置不可覆盖"的规则，把所有使用者的手感一起改掉。
3. **参数面板的分组是应用侧唯一的手写信息**（`StrokeTipEditor.Sections`，按稳定标识归栏）。引擎的参数表是扁平的（顺序即预设向量顺序，不能动），所以分组只能在这儿；漏归类的新参数会落到末尾的「其它」栏，**不会从界面上消失**。UiSmoke 有一条 `滑杆数 == StrokeTipParameters.All.Count` 的断言守着"引擎加了参数而界面没跟上"这件事。
4. **`.jalxaml` 里一个 Dusk 类型名都不出现**（规矩 1）：参数面板在 code-behind 里生成，标记里只有一个空容器 `TipParameterSections`。
5. **试写区必须显式 `Dispose()`**，与 `AnnotationOverlayWindow` 同一条理由（Jalium 不代调、类型无终结器）。
6. **荧光笔与激光笔不受笔锋影响**（等宽几何不读压力）。笔菜单里那一行**灰掉并写一句原因**，而不是像橡皮菜单那样藏起来 —— 浮窗的位置是按旧尺寸算好的，藏一行会让它跳一次；灰掉则零布局变化。
7. **速度那三项（`velocityInfluence` / `velocityReference` / `velocityWindowPoints`）刻意不属于预设作用域**（引擎的规定：需要标定过的毫秒时间戳，不该因为切档位被关掉），但**会随当前取值一起落盘**。本应用的时间戳在 Jalium 适配层由 `StylusBatchTiming.Stamp` 按毫秒摊开，所以这一档是可用的。
8. **数组进 `PreferenceSnapshot` 会破坏"存盘再读回来相等"那条断言**（record 合成的 `Equals` 对数组按引用比）。所以 `TipValueVector` / `TipPresetRecord` / `TipPresetCollection` 是**显式按值比较**的类型，共用 `TipValue` 那对比较/哈希助手。加新的数组字段时照抄这三者的写法，别直接放 `double[]`。
9. **档位归属靠"取值反查"而不是记忆**：任何参数一变就重认一遍（`FindMatchingPresetId`），认不出任何一档就落到「自定义」。所以界面不会出现"档位显示毛笔、参数早就不是毛笔了"。程序化套档时会压住这次反查（`_suppress`），因为两个档位取值相同时反查会认到先登记的那个。
10. **笔菜单的档位下拉不缓存一份状态**：它读 `InkTipOptions` 显示、把用户的选择发出去（`TipPresetChanged` → 宿主转交）。档位不像颜色 / 粗细那样在本窗口留副本 —— 设置页也在改它，留副本就是两个真相。

笔锋的塑形算法一行都没在应用侧重写，也没在这里测 —— 那是引擎自己的探针（`Dusk.KernelProbe` 的七档压力剖面表）的事。UiSmoke 只钉"应用侧这一层接线没断"：选档写到设置、微调掉成自定义、存/删自定义档、描述符面板行数、读档把自定义档装回库里、以及换档之后**画布的 `TipSettings` 晚一拍跟上**。

引擎版本与 SDK 线：`sdk/build-sdk.ps1` 每次改动后升 `1.0.N`（同版本禁覆盖），三门（元数据面比对 / 消费面编译 / 包内容审计）全绿才投放 feed；应用侧再改 `PackageReference` 版本号。Dusk 侧的 `tools/check.ps1` 十二项性能门槛里八项是墙钟时间，本机同一指标四轮实测在 205–771 毫秒之间摆动（771 那轮当场红），三条分配门槛则四轮字节不变 —— 把它当"这台机器当下忙不忙"的读数看，红了一定要复跑再结论。

混淆是**轻档**（public 面全保名，只改不可见名字 + 加密字符串），实测数字与升档通路记在 `Dusk/sdk/obfuscar.xml` 顶部；这一档的实际保护来自单程序集合并与不投 pdb/源链接/xml。

---

## Critical: 控件层是 FluentJalium（Astra），以源码直引

自 2026-09-24 起，本仓库**不再有自己的一份 Fluent 主题层**。原先的 `Themes/Fluent/`（10 个 `.jalxaml`，1,067 行 —— 其中 `FluentTheme.jalxaml` 那份合并清单运行时根本不装载）与四个自建控件类型（`FluentToggleSwitch` / `FluentNavigationItem` / `FluentSettingsRow` / `NavigationIndicatorAnimator`，310 行）整体下架，改由兄弟仓库 `C:\git\Jalium\FluentJalium` 提供。

| 事实 | 位置 |
|---|---|
| 怎么引入 | `LanStartWrite.Inkcanvas.csproj` 里 `<ProjectReference Include="..\..\..\..\Jalium\FluentJalium\src\FluentJalium\FluentJalium.csproj" />`。**源码直引**，不走 feed —— Dusk 那套版本仪式是为闭源+混淆建的，这里没有那个理由 |
| 怎么装 | `Program.cs`：`RenderContext` → **`ThemeLoader.Initialize()`** → `new Application()` → `FluentThemeManager.Apply(app, variant)` → **然后**才准构造任何控件。`Apply` 晚于控件就会读到旧排版表（库里 spike/TypoProbe 量过） |
| 应用侧还剩什么 | `FluentTheme.cs`（把偏好翻译成库的主题变体 + 两项应用自有 token）、`Themes/AppTokens.jalxaml`、`Themes/AppControls.jalxaml` |
| 标记里的命名空间 | `xmlns:fluent="clr-namespace:FluentJalium.Controls;assembly=FluentJalium"` |
| 深浅与高对比 | 全部由 `FluentThemeManager` 决定；它改的是**已发布的画笔实例的颜色**，所以 `{StaticResource}` 消费者能跟着翻 —— 这条和本仓库旧实现是同一个设计，不是巧合，是前提 |

`ThemeLoader.Initialize()` 这一行以前没有也照样跑（应用的 `.jalxaml` 是编译期生成的），但库的字典是**运行时用 XamlReader 解析**的，缺了它就是在未初始化的加载器上解析。

### 换层实测换来的判断，别改回去

1. **派生控件不保证命中隐式样式。** `RadioToolToggleButton : AppBarToggleButton` 拿不到 `TargetType="AppBarToggleButton"` 的隐式行，必须显式点键（`Style="{StaticResource DefaultAppBarToggleButtonStyle}"`）。库自己的派生控件也一律这么做，附带注释说明原因。框架类型（`Slider` / `RadioButton` / `ComboBox` / `AppBarButton`）的隐式命中已实测有效。
2. **别拿 `GetStyle("Default*Style")` 判"命中没有"。** 隐式行是 `BasedOn` 出来的**另一个 Style 实例**，按对象身份比必然红。要验就验模板部件名：`SliderContainer` / `RadioRing` / `HighlightBackground` / `AppBarButtonInnerBorder`（UiSmoke 里那四条就是这么写的）。
3. **库的 AppBar 样式族不写 `FocusVisualStyle`。** 它对 `Button`、导航项都写了，AppBar 这一族没有 —— 于是六个工具键会安静地丢掉键盘焦点环，而"统一的键盘焦点"是本应用自己承诺过的。现在由 `AnnotationToolbarWindow.jalxaml` 在六个控件上各点一次 `FocusVisualRingStyle` 补上。**这条该往库里提**，补在这儿是权宜。
4. **库的指示条走的是 WinUI 曲线**：`MoveTo` 会先把新目标写进**基值**再用动画盖上去，且 `top` 一路先伸到目标那一头、高度再跨住整段距离收回到 16。于是旧那条"选中瞬间读到的还该是起点"的判据不成立了 —— 时钟走过第一格之前读到的就是基值。现在钉的是"两条动画时钟都挂着"（库在不动画那条分支上是直接落值、不挂时钟的，所以这就是"没传送"的充分证据）。**"续起的起点取的是当前显示值"这一条本套 320ms 步进量不出来，没有当成已经验过**（试过按高度采样，三轮里红过一次，已撤）。
5. **库的 `FluentNavigationView` 不在窗口高度变化时重算指示条**（实测：停在 637.43，页脚项已在 717.43）。宽度变化会走它自己的 `AdaptPane → InvalidateMeasure` 因而跟着走，高度变化没有那条路。UiSmoke 现在如实打一条 SKIP 并改验"落点来自当前布局、不是缓存行高"。**这条也该往库里提。**
6. **图标走 `FontIcon` + 显式 `FontFamily="Segoe Fluent Icons"`。** `SymbolIcon` 在这个运行时不暴露 FontFamily，被框架钉死在 `Segoe MDL2 Assets`（Win10 形状），且 764 格里 120 格画不出墨。码点一律取自库的实测表 `FluentJalium/spike/GlyphInkProbe/glyph-ink-symbol.csv`，不许凭记忆写。当前六个：`E7C9` 鼠标 / `E76D` 笔 / `E75C` 橡皮 / `E7A7` 撤销 / `E7A6` 重做 / `E713` 设置，`ink` 均 > 0。**不要给图标设本地 `Foreground`** —— 一设就压住库 `IconInk` 那条活绑定，选中态"白字在 accent 上"当场失效。
7. **量导航面板宽度要先关掉动画。** `PART_PaneRoot` 的宽度带 0.2 秒过渡（读 `SplitViewPaneAnimationOpenDuration`），"下一拍就该读到 48"是道时序题 —— 实测三轮里红过一次。现在那三步在 `ReduceMotion=true` 下量，两态真的换了与否由 `IsCompact` 与 `PART_Label` 折叠那两条管。
8. **库没有的就继续自实现**（用户定的范围）：九色画笔色板 `PenColorSwatchStyle`、两个实体浮层表面 token（`ToolbarSurfaceBrush` 白 / `#2C2C2C`，`FlyoutSurfaceBrush` `#F9F9F9` / `#2C2C2C`；WinUI 的对应物 `FlyoutPresenterBackground` 是亚克力，本应用刻意不用，所以也不能借那个键名）、`FlyoutPlacement` 的原生坐标定位、`RadioToolToggleButton.Reactivated`、四个窗口的分工（Design.MD §1）。应用侧的 `HelperTextStyle` / `SectionTextStyle` / `SettingsCardStyle` 是**基于库的键往上加**的三行扩展（库按 WinUI 原样发布尺度，不替宿主定辅助文字颜色与卡片行距），不是第二套尺度。

UiSmoke 现状：**139 条全绿，连续三轮数字一致**（`dotnet build tools/UiSmoke/UiSmoke.csproj -c Debug -p:OutputPath=bin/Verify/` 后直接跑 `tools/UiSmoke/bin/Verify/LanStartWrite.Inkcanvas.UiSmoke.exe`）。注意本应用的 `.exe` 若在运行中会锁住 `bin/Debug`，构建一律带 `-p:OutputPath` 绕开。



## Jalium.UI Framework Reference

### What is Jalium.UI?
Jalium.UI is a GPU-accelerated UI framework for .NET 10. It combines:
- **WPF-style object model**: DependencyObject, DependencyProperties, visual tree, logical tree, routed events, data binding
- **JALXAML markup language**: Declarative UI markup with namespace `https://schemas.jalium.dev/jalxaml/presentation`, file extension `.jalxaml`, supports Source Generator compile-time code generation
- **Razor syntax extensions in JALXAML**: `@Path`, `@(expr)`, `@{ ... }`, `@if`, mixed text templates
- **GPU-native rendering backends**: DirectX 12 (Windows), Vulkan (Linux/Android), Metal (macOS), Software fallback

### Current Version
v26.10.2 (latest release as of 2026-05-06)

### Platform Targets
- **Primary**: Windows 10/11 x64 (DirectX 12)
- **Cross-platform**: Android (arm64-v8a, x86_64), Linux (Vulkan), macOS (Metal)
- **Runtime**: .NET 10 (net10.0-windows, net10.0-android, net10.0)

### Key Differences from WPF
- **Rendering**: DirectX 12 (not DirectX 9 like WPF). Uses Vello GPU compute pipeline.
- **Markup**: JALXAML (`.jalxaml` files), NOT XAML. Namespace: `https://schemas.jalium.dev/jalxaml/presentation` or `https://jalium.dev/ui`
- **Razor syntax**: JALXAML supports Razor-style syntax (`@Path`, `@(expr)`, `@{ ... }`, `@if`) as additive sugar on top of `{Binding ...}`
- **Startup**: Must call `ThemeLoader.Initialize()` BEFORE any JALXAML parsing
- **NOT a drop-in WPF replacement**: API names are close to WPF but differences exist intentionally

### Package Structure
| Package | Responsibility |
|---------|---------------|
| Jalium.UI.Core | Dependency property system, visual tree, layout, routed events, binding, animation |
| Jalium.UI.Media | Brushes, geometry, drawing, text formatting, imaging, visual effects |
| Jalium.UI.Input | Mouse, keyboard, touch, stylus input abstractions |
| Jalium.UI.Interop | Managed/native bridge, P/Invoke, runtime native dependency packaging |
| Jalium.UI.Gpu | GPU resource management, render graph, materials, shaders, backend abstraction |
| Jalium.UI.Controls | Controls, panels, templates, windowing, themes, docking, charts |
| Jalium.UI.Xaml | JALXAML parse/load pipeline, Razor syntax support, markup services |
| Jalium.UI.Build | MSBuild tasks for JALXAML compilation workflow |
| Jalium.UI.Xaml.SourceGenerator | Roslyn source generator for XAML/code-behind integration |
| Jalium.UI | Metapackage that references the full framework stack |

### Platform Packages
- **Jalium.UI.Desktop**: net10.0-windows distribution with native DLLs
- **Jalium.UI.Android**: net10.0-android distribution with native .so libraries

### Startup Pattern (Critical Order!)
```
ThemeLoader.Initialize() -> new Application() -> XamlReader.Parse(...) -> new Window { Content = ... } -> app.Run(window)
```
- Only ONE Application instance per process
- `[STAThread]` must be applied to entry method
- ThemeLoader.Initialize() must run before ANY JALXAML parsing

### Available Controls (80+)
- **Input**: Button, TextBox, PasswordBox, NumberBox, AutoCompleteBox, ComboBox, Slider, CheckBox, RadioButton
- **Data**: TreeView, DataGrid, TreeDataGrid, ListBox, ListView
- **Navigation**: NavigationView, TabControl, Ribbon, CommandBar, MenuBar
- **Documents**: FlowDocumentViewer, FlowDocumentReader, FlowDocumentScrollViewer, Markdown
- **Charts**: Category, DateTime, Logarithmic axes with chart legend
- **Rich**: InkCanvas, WebView/WebBrowser, EditControl, QRCode, TitleBar
- **Layout**: Grid, StackPanel, Canvas, DockPanel, WrapPanel, UniformGrid, VirtualizingStackPanel

### Visual Effects
- Liquid glass with refraction, chromatic aberration
- Backdrop effects: blur, acrylic, mica, frosted glass
- Transition shaders and element effects (blur, drop shadow)
- Custom shader support via HLSL

### Text Rendering
- ClearType sub-pixel text rendering with dual-source blending
- CPU rasterization fallback path
- Cross-platform text shaping via FreeType + HarfBuzz (Linux/Android)

### Resources
- GitHub: https://github.com/VeryJokerJal/Jalium.UI
- Official Site: http://jaliumui.top
- Docs: http://docs.jaliumui.top
- QQ Group: 1079778999
- License: MIT
