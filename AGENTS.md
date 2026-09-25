# Project Memory - LanStartWrite.Inkcanvas

## Critical: This is a Jalium.UI project

**THIS IS NOT WPF. THIS IS NOT WinUI. THIS IS NOT Avalonia.**

This project uses **Jalium.UI** framework. All UI code, markup, and patterns must follow Jalium.UI conventions.

---

## Critical: 墨迹引擎是 Dusk，以闭源 SDK 引入

自 2026-09-23 起，本项目的墨迹引擎是兄弟仓库 `C:\git\ink\Dusk`，**以闭源 SDK 形式**进入：

| 事实 | 位置 |
|---|---|
| 唯一墨迹承载面 | `CanvasSurface`（应用侧一个类）**里面** `new JaliumInkCanvas()`；两块画布（屏幕批注与白板）各持一份 `CanvasSurface`，塞进各自 `.jalxaml` 的 `InkHost` |
| 包从哪来 | `C:\git\ink\Dusk\sdk\build-sdk.ps1` → 单程序集 `Dusk.dll` → `C:\git\ink\dusk-feed\` |
| 源从哪读 | `NuGet.config` 里的 `dusk-sdk` 源；**本仓库里没有 Dusk 源码，也不许加 ProjectReference** |
| 引擎文档 | `C:\git\ink\Dusk\docs\`（仅本地保留，未入库）；公开 GPL 源码仓见 Dusk 的 `PackageProjectUrl` |

三条不要碰的规矩，都是这次迁移用实测换来的：

1. **不要在 `.jalxaml` 里写 Dusk 的类型名**。引擎类型只出现在 code-behind。这样万一日后把混淆保留面从"全 public"收窄成"契约闭包"，应用侧零 diff。
2. **不要再往墨迹层加反射补丁**。本项目原先有 ~600 行围着 `Jalium.UI.Controls.InkCanvas` 打的补丁（反射取 protected `DynamicRenderer`、反射改私有 `MinPointDistance`、等距加密补点、探测喂点入口、DispatcherTimer 手搓激光淡出）——全部已删，Dusk 内核对这些都有原生对应物（`InkInputProfile` 按设备档位、`StrokeKind.Laser` + 覆盖层淡出、`GetIntermediatePoints` 直连湿墨、`Metrics` 埋点）。加回来就是重新制造上游版本依赖。
3. **`JaliumInkCanvas.Dispose()` 必须显式调**。Jalium 不代调、类型无终结器；漏掉且 Document 被外部持有会留住整棵墨迹视觉树。现有做法：两个窗口的 `Closed` 里各调一次 `CanvasSurface.Dispose()`（它内部再调引擎的 `Dispose`）。

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

1. **派生控件不保证命中隐式样式。** `RadioToolToggleButton : ToggleButton` 拿不到 `TargetType="ToggleButton"` 的隐式行，必须显式点键（现在点的是应用自己的 `ToolToggleButtonStyle`）。库自己的派生控件也一律这么做，附带注释说明原因。框架类型（`Slider` / `RadioButton` / `ComboBox` / `Button`）的隐式命中已实测有效。
2. **别拿 `GetStyle("Default*Style")` 判"命中没有"。** 隐式行是 `BasedOn` 出来的**另一个 Style 实例**，按对象身份比必然红（踩过）。要么验模板部件名 —— `SliderContainer` / `RadioRing` / `HighlightBackground` 这三条在 UiSmoke 里守着笔菜单那三个控件；要么验可观测的几何与色阶身份（工具栏那六颗走的是后者，见下条）。注意 `ComboBox` 的盒子模板里没有 `Pill`，那是"下拉项"的模板，未展开时根本不建 —— 第一次就踩在这儿。
3. **浮动批注栏那六颗钮不是 `AppBarButton`。** 第一次换层时按名字对号入座换了 `AppBarButton` / `AppBarToggleButton`，当场就是错的：那一族是命令条上"图标 + 下方标签"的 **68x64** 按钮，`IsCompact` 只是把标签收起来，内层边框仍然留着 22 DIP 的标签带（库里 `AppBarButtonInnerBorderCompactMargin = 2,6,2,22`）—— 于是整条明显过长、**选中实底只盖到上面一块**。批注栏要的 40x40 图标居中、未选透明、选中整块铺 accent，对应的是 `Button` / `ToggleButton`：库里 `ToggleButtonBackgroundChecked` 就是 `AccentFillColorDefaultBrush`、`ToggleButtonForegroundChecked` 就是 `TextOnAccentFillColorPrimaryBrush`，与旧手写模板同一套色阶。样式在 `Themes/AppControls.jalxaml` 的 `ToolActionButtonStyle` / `ToolToggleButtonStyle`（40x40 方格 + 清内边距；切换钮未选那一半换成 Subtle 阶梯，因为它默认是带边框的实体按钮底）。**换层是按用途挑控件，不是把类型名换掉。**
   附带一条：库的 AppBar 那一族不写 `FocusVisualStyle`（对 `Button` / 导航项都写了）。本应用现在不走它了，焦点环由 `ButtonLayoutStyle` 直接带进来；但谁要用 `AppBarButton` 做命令条，得自己补。
4. **库的指示条走的是 WinUI 曲线**：`MoveTo` 会先把新目标写进**基值**再用动画盖上去，且 `top` 一路先伸到目标那一头、高度再跨住整段距离收回到 16。于是旧那条"选中瞬间读到的还该是起点"的判据不成立了 —— 时钟走过第一格之前读到的就是基值。现在钉的是"两条动画时钟都挂着"（库在不动画那条分支上是直接落值、不挂时钟的，所以这就是"没传送"的充分证据）。**"续起的起点取的是当前显示值"这一条本套 320ms 步进量不出来，没有当成已经验过**（试过按高度采样，三轮里红过一次，已撤）。
5. **库的 `FluentNavigationView` 不在窗口高度变化时重算指示条**（实测：停在 637.43，页脚项已在 717.43）。宽度变化会走它自己的 `AdaptPane → InvalidateMeasure` 因而跟着走，高度变化没有那条路。UiSmoke 现在如实打一条 SKIP 并改验"落点来自当前布局、不是缓存行高"。**这条也该往库里提。**
6. **图标走 `FontIcon` + 显式 `FontFamily="Segoe Fluent Icons"`。** `SymbolIcon` 在这个运行时不暴露 FontFamily，被框架钉死在 `Segoe MDL2 Assets`（Win10 形状），且 764 格里 120 格画不出墨。码点一律取自库的实测表 `FluentJalium/spike/GlyphInkProbe/glyph-ink-symbol.csv`，不许凭记忆写。当前七个：`E7C9` 鼠标 / `E76D` 笔 / `E75C` 橡皮 / `E7A7` 撤销 / `E7A6` 重做 / `E713` 设置 / `E786` 白板，`ink` 均 > 0；
   另有 `E8B3` 选择（白板里那颗鼠标钮的替代形状，ink=401）。`EF20` Marquee 与 `EDA4` Touchscreen **ink=0**，看着对却画不出墨，禁用。**不要给图标设本地 `Foreground`** —— 一设就压住库 `IconInk` 那条活绑定，选中态"白字在 accent 上"当场失效。
7. **量导航面板宽度要先关掉动画。** `PART_PaneRoot` 的宽度带 0.2 秒过渡（读 `SplitViewPaneAnimationOpenDuration`），"下一拍就该读到 48"是道时序题 —— 实测三轮里红过一次。现在那三步在 `ReduceMotion=true` 下量，两态真的换了与否由 `IsCompact` 与 `PART_Label` 折叠那两条管。
8. **库没有的就继续自实现**（用户定的范围）：九色画笔色板 `PenColorSwatchStyle`、两个实体浮层表面 token（`ToolbarSurfaceBrush` 白 / `#2C2C2C`，`FlyoutSurfaceBrush` `#F9F9F9` / `#2C2C2C`；WinUI 的对应物 `FlyoutPresenterBackground` 是亚克力，本应用刻意不用，所以也不能借那个键名）、`FlyoutPlacement` 的原生坐标定位、`RadioToolToggleButton.Reactivated`、四个窗口的分工（Design.MD §1）。应用侧的 `HelperTextStyle` / `SectionTextStyle` / `SettingsCardStyle` 是**基于库的键往上加**的三行扩展（库按 WinUI 原样发布尺度，不替宿主定辅助文字颜色与卡片行距），不是第二套尺度。

UiSmoke 现状：**327 条全绿 + 1 条 SKIP**（`dotnet build tools/UiSmoke/UiSmoke.csproj -c Debug -p:OutputPath=bin/Verify/` 后直接跑 `tools/UiSmoke/bin/Verify/LanStartWrite.Inkcanvas.UiSmoke.exe`）。
导航动画那组里 `Retargeted animation settles...` / `A live intermediate frame...` 两条会**自己红**（2026-09-25 实测：改动前后都会 3 次里红 1 次，量的是库的动画时钟，不是白板的账），
而 `Check()` 一红就中断整条队列 —— 所以**基线要复跑两三次再取数**，单看一次的红绿不可信。注意本应用的 `.exe` 若在运行中会锁住 `bin/Debug`，构建一律带 `-p:OutputPath` 绕开。**跑之前先看 exe 的时间戳** —— 跑一份旧 exe 会安静地验一套旧检查，数字看着还挺像样（踩过一次：46 条全绿其实是几个月前的产物）。另外 Main 一进来就把 `ReduceMotion` 设成 true：第一拍就要量导航面板宽度，而面板打开带 0.2 秒过渡 —— 320ms 的第一拍实测仍会抖（6 个导航项那次就是它红的）；导航动画那组需要动的时候自己会再打开。

另有一处会骗人的陈旧产物：`src/LanStartWrite.Inkcanvas/obj/Debug/net10.0-windows/generated/Jalium.UI.Xaml.SourceGenerator/` 里那份 `*.g.cs` 停在 2026-09-17 没再更新过（**当前管线真正用的是 `obj/<cfg>/net10.0-windows/Jalxaml/Razor/*.jalxaml`** —— 想确认"标记改动进了构建没有"就 `grep` 那里，别 `grep` 那份 `.g.cs`）。



## Critical: 窗口层级由 WindowLayerManager 统一排

自 2026-09-24 起，**窗口之间的 Z 序不再由各窗口自己喊 `Topmost`**，而是登记给一套系统算。三个文件：

| 文件 | 职责 |
|---|---|
| `WindowLayer.cs` | 层级枚举。整数就是顺序 |
| `NativeWindowZOrder.cs` | 只做 Win32 互操作（`SetWindowPos` / `GetWindow` / `GetTopWindow` / `GetWindowLongPtr`），不含策略 |
| `WindowLayerManager.cs` | 策略：谁在谁上面、谁压得住其他应用、什么时候重排、怎么自检 |

层级从低到高（数字即顺序）：

| 层 | 谁 | 要求 |
|---|---|---|
| `Canvas` | 批注画布（全屏透明）与白板（全屏有底） | **只要在屏就必须压过其他应用**；在本应用内部最低。同层允许并存，但两块画布不会同时在屏 |
| `Toolbar` | 批注栏 | 在画布之上 |
| `Panel` | 笔 / 橡皮二级菜单 | 在批注栏之上 |
| `Dialog` | 设置 | 全应用最高，**画布也不许盖住它** |

**两条不变量**，由 `WindowLayerManager.Verify()` 回读**真实桌面 Z 序**来验（不是回读我们设过的属性）：

1. 同一时刻在屏的两个窗口，层级高的在层级低的之上；
2. 该压过其他应用的窗口都带 `WS_EX_TOPMOST`，不该带的都不带。

**新加窗口的规矩**：构造函数里 `WindowLayerManager.Register(this, WindowLayer.X, "名字")`，
然后这个类里**一行 `Topmost` 都不该有**（`.jalxaml` 里也不声明 `Topmost` —— 四处旧声明已删）。

### 这套系统换掉的是什么

以前是三个调用点各写一遍：显示画布时顺手把工具栏 Topmost 关一下再开、再 `Activate`，还排在 Dispatcher 队列末尾补一次（因为 Jalium 的透明窗口 `Show()` 会自己动 native Z 序）；打开设置时先把工具栏降下来；菜单再抄一份工具栏的 `Topmost`。
**谁也说不清"现在到底谁在上面"，加第四个窗口只能靠试。** 现在层级是登记表里的数据，剩下的事交给系统。

### 十条判断（都是这套系统的承重墙）

1. **带与序是两件事。** `Topmost` 只能表达"在不在置顶带里"，而**同一带内部的先后没有公开 API** —— 只能靠激活或 `SetWindowPos`。所以：在不在带里走框架属性（顺带把 `WS_EX_TOPMOST` 设对），谁在谁上面走 `SetWindowPos` 贴到上一个（更高）可见窗口的下面。从最高那层往最低那层排一遍即可。
2. **排 Z 序一律 `SWP_NOACTIVATE`。** 排序不是"把谁叫到前台"。少了它，每次维护层级都会把键盘焦点从当前控件上夺走 —— 表现是"拖滑杆拖到一半焦点跳走了"，很难往"窗口管理"上想。
3. **句柄走 `Window.Handle`。** Jalium 两处都有（`Window.Handle` 与 `WindowInteropHelper.Handle`），前者更短且同一件事。句柄为 0 表示原生窗口还没建出来 —— 当作不可见跳过，等 `Shown` 那一拍。
4. **可见性只听 `Shown` / `Hiding` 事件**，不看 `Window.Visibility`：后者在"从没 `Show` 过"的窗口上初值不可靠（WPF 那一族默认 `Visible`），而判错的后果是把没显形的窗口也拿去排 Z 序。登记那一刻用一个保守初值，之后一律听事件。
5. **钉住向上继承。** 下面有置顶窗口，上面那些就**必须**也置顶 —— 否则非置顶窗口永远在所有置顶窗口之下，"层级"自相矛盾。所以调用方只需要说"画布可见 / 工具栏要不要始终置顶"，菜单与对话框的置顶是从下面推出来的，不用各记一份。
6. **画布层"可见即置顶"写成层自带的性质**，不是某个窗口的开关：一个吃满全屏的透明画布若不置顶，就成了"被任何人压住、又压住桌面"的中间层。写在窗口上，迟早会有一个新窗口忘了打开它。
7. **对话框在场时整个应用退出置顶带**（`ComputeStack` 里那一句）。两条理由：设置窗口要能被压到别的应用后面（这是原有行为）；"压过其他应用"承诺的是**批注这件事**，不是这个应用永远在最上面。**注意这只影响带、不影响序** —— 对话框 > 菜单 > 工具栏 > 画布 两种情况下都照排，靠的是 `SetWindowPos`。
8. **画布在设置窗口打开期间仍然收起（`Hide`），但那不是为了层级。** 层级已经保证"设置压得住画布"，两者都可见时也成立（UiSmoke 就是这么验的）。收起它是为了让**桌面可用** —— 画布是一个吃满全屏输入的最顶层窗口，留着它别的应用点不动。
9. **自愈那道网先回读再动手。** 破坏源不只是本应用：外壳重排置顶窗口、目标机上的常驻置顶工具都能把顺序挪走，事件驱动盯不住，而"每拍无脑 `SetWindowPos`"又太吵。所以 `DispatcherTimer` 每 1.5 秒 `Verify()` 一次，**只有真偏了才 `Reconcile()`**，正常时只是一趟 Z 序遍历。`AutoRepairChecks` / `RepairCount` 是它的体检口（UiSmoke 有一条断言它确实在跑 —— 写了没接上的定时器不会有任何症状）。
10. **`DllImport` 而不是 `LibraryImport`**：后者要求整个工程开 `AllowUnsafeBlocks`（SYSLIB1062），而这里六个入口的参数全是 `IntPtr`/`int`/`bool`，源生成器给不出更快的东西。为六个调用把整个项目的不安全代码闸门打开，换不来任何收益。

### 验收方式

`UiSmoke.CheckWindowLayers` 起四个真窗口（画布缩成 320×240 推到屏幕外，别真盖住桌面），然后：

- 断言四个窗口**在桌面 Z 序里的排名**满足 设置 < 笔菜单 < 批注栏 < 画布（排名越小越靠前）—— 问的是操作系统"现在谁在上面"；
- **故意把画布顶到最前**（`NativeWindowZOrder.PlaceAfter(handle, Top)`），断言 `Verify()` 报得出来、`Reconcile()` 修得回去；
- 收起画布 + 对话框在场 → 断言工具栏与设置都**没有** `WS_EX_TOPMOST`（"应用退出置顶带"这条规则）；
- 画布回来 → 断言**画布与对话框都置顶**（向上继承的直接后果，也是它的验收）。

`Verify()` 只覆盖"本应用内部的先后"与"带"。**"不被其他应用的窗口盖住"这一条由 `WS_EX_TOPMOST` 本身保证** —— 置顶是外壳维持的，普通窗口永远盖不到置顶窗口上面，所以那条断言就落在"这个位有没有设对"上。


## Critical: 工具栏是数据驱动的（可自定义 + 每项自带数据）

自 2026-09-24 起，批注栏的按钮不再写死在标记里 —— `.jalxaml` 里只有一个空的 `ToolsPanel`，
钮由 `ToolbarTools` 那份列表建出来。**每一项自带一套数据**，于是"放两个笔按钮、他俩的数据还要独立"
不需要任何新机制：它就是两条记录（用户要的正是拿按钮当色板用）。

| 文件 | 职责 |
|---|---|
| `ToolbarToolKind.cs` | 八种用途：鼠标 / 笔 / 橡皮 / 撤销 / 重做 / 设置 / 分隔线 / 白板 |
| `ToolbarTool.cs` | 一项的数据（扁平的，按 `Kind` 决定哪几个字段有意义）+ 按值比较的集合 |
| `ToolbarTools.cs` | 模型：有哪些项、什么顺序、选中谁、增删改序、与笔锋的来回同步 |
| `ToolbarToolVisuals.cs` | 外观：图标码点、色标、"40×40 一格长什么样"（批注栏与设置页共用） |
| `ToolbarToolListEditor.cs` | 设置页「工具栏按钮」那份列表（增删换序） |
| `AnnotationToolbarWindow.jalxaml.cs` | 只做三件事：渲染列表、把"点哪一颗"翻译成"选中哪一项"、转发二级菜单的编辑 |

### 十二条判断

1. **画笔粗细 / 橡皮擦法 / 橡皮半径 / 笔锋取值都属于"某一项"**，不再是 `PreferenceSnapshot` 上的全局字段 ——
   留一份全局的做镜像就是第二个真相。**那些旧字段是删掉而不是留着兼容**，所以存档格式变了
   （旧档会得到一条默认工具栏）。
2. **笔锋的归属**：`InkTipOptions` 仍是"当前生效的那一份"（画布只认它，设置页也只改它）；
   选中一支笔时 `ToolbarTools` 把那支笔存的形状推过去（`LoadState`），用户改了参数再**写回选中的那一支**。
   一来一回之后两支笔各有一套 —— 若只存"档位标识"，手调的参数会在两支笔之间串味。
3. **写回要压住"读"那一趟**：`ApplySelectedTipToEngine` 里的 `_loading` 开关压的是**写回**，
   而引擎那边的通知照发（画布靠它更新 `TipSettings`）。不压的话，每选一次笔都会把"跟着档位走"
   的那支物化成一份显式拷贝，而通知还会白跑一趟。
4. **固定项不可删**：鼠标模式（退出这块画布）、白板（进那块画布的唯一入口）、设置（改工具栏的唯一入口）、撤销、重做 ——
   补齐时落在它该在的那一格（白板紧跟鼠标），不是一律追加到尾巴 —— 否则"首启"与"从旧档升级"看到的顺序不一样，
   而这只会以"我的按钮顺序怎么变了"的形式被用户看见。
   各只允许一个、不许缺失，`ValidateTools` 会把缺的补回来。
   **而"列表整个是空的"必须落回 `DefaultItems()`**：否则首启会得到一条一支笔都没有的工具栏
   （补的是五个门槛项，笔和橡皮是用户数据，不会凭空长出来）。
5. **与类型无关的字段一律洗回默认值**：分隔线身上不该留着颜色 —— 否则存档会被后人误读成"这个按钮也有颜色"。
6. **新项复制当前选中那一项的数据、并插在它后面**：用户说的"再放一个"几乎总是"再放一个跟这个差不多的"，
   空白的第二支笔只会让人再调一遍。加完顺手选中它，因为接下来几乎一定要调它的颜色。
7. **图标就是原来的六个形状，不按笔型 / 擦法细分**。试过按用途换（荧光笔用 Highlight、
   激光笔与笔迹擦用"实心"变体 E829 / E82C），用户一句话否了：实心那几个在 40×40 的小格里
   读起来就是一团黑（"有的按钮它是黑色的"），而笔迹擦"仍然是原来那个图标，你没有必要换"。
   **两个信息合起来是同一条判断**：擦法与笔型之间的差别靠名字、菜单与色标说清，
   不该靠把图标换成另一团黑。码点因此回到 E7C9 / E76D / E75C / E7A7 / E7A6 / E713 六个。
   （这个运行时确实没有激光笔与整笔擦的专用字形 —— `StrokeErase` / `PointErase` / `Marker`
   那批 `ink = 0`，`Laser` 这个名字压根不存在 —— 但"没有专用字形"不等于"要借用实心变体"。）
8. **色标是笔按钮的第二标识，且必须另画一条而不是给图标上色** ——
   图标的前景是继承来的，一设本地值就压住"选中态白字在 accent 上"那条绑定。
9. **结构签名决定"重建"还是"只刷新"**：数据变了有两种（改颜色 / 增删换序），
   前者只刷新那一颗的外观，后者才重建控件 —— 重建会丢掉键盘焦点，而拖滑杆每一拍都会走到这条路上。
10. **`ToolbarToolVisuals` 是唯一的码点来源**：批注栏与设置页那份列表都要画同一个按钮，
    码点抄两份的代价是"改了一处另一处不跟着改"，于是"列表里是荧光笔、工具栏上却是书写笔"会静默存在。
11. **色板只有一份**（`InkPalette`）：笔菜单的九格、工具栏摘要的文字、"红色 · 4 px"里的颜色名都读它。
    颜色之前抄在标记的 `Background="#..."` 里，等于两张表 —— 已改成由代码刷进去。
12. **笔菜单 / 橡皮菜单编辑的是"当前选中那一项"**，不需要记住"是哪个按钮打开的菜单"：
    菜单只能从已选中的那颗钮打开（`Reactivated` 只在选中态下才发），所以两者天然是同一个。
13. **改工具数据必须当场落到画布**（`SyncToolControls` 末尾的 `SyncSelectedToolToCanvas`）。
    这条是用户报的严重缺陷：菜单里拖粗细毫无反应，要切到别的工具再切回来才看得到 ——
    根因是数据模型改了、按钮图标刷了，**但没有人把新值写进引擎**。
    教训很具体：把"全局值"改成"每项一份数据"时，旧代码里"改值 → 顺手应用到画布"的那一步
    是跟着旧通路走的；通路换了，这一步就丢了，而构建、渲染、菜单全都正常。
    **数据驱动的界面必须有一条"数据变了 → 界面跟着变"的完整清单，缺一步就是静默失效。**
14. **工具栏的设置独立成页**（设置页导航六项：外观 / 墨迹 / 画布 / 工具栏 / 窗口与交互 / 关于）：
    「始终置顶工具栏」与「工具栏按钮」都搬了过去。用户点名的不是"多一个分组"，
    而是"工具栏这件事有自己的入口" —— 它会越长越多（对齐、吸附、透明度…），一开始就该有自己的页。
15. **出现位置是算出来的，不是写死的**（`ToolbarPlacement`，在 `Program.cs` 里 Show 之前一次）：
    主屏**工作区**下方居中、可见表面底边离任务栏 12 DIP。三件事分开看：
    工作区读 `Jalium.UI.SystemParameters.WorkArea`（**已实测是 DIP**，与 `Window.Left` 同坐标系，
    所以这里一次换算都没有 —— 单位换错不报错，只是栏跑到屏外）；
    宽高读构造函数里 `FitSizeToContent` 量出来的实测值（栏宽随按钮数走，标记里的 300 早就不成立）；
    标记里那对 `Left="120" Top="120"` 只剩兜底。另外 `FitSizeToContent` 在宽度变化时**守住中心**
    而不是把右缘推出去 —— 用户摆的是一条居中的栏，加第二支笔就整条往右挪半颗钮，"居中"当场失真。
    已知边界：`WorkArea` 说的是主屏（任务栏在副屏时会落在主屏下方）。

### 验收

`UiSmoke.CheckToolbarTools` 起一条真工具栏，钉的是：默认八项与顺序、加一支笔（复制了当前那支、插在它后面）、
**两支笔的颜色 / 粗细 / 笔锋互不影响**（含"手调之后切走再切回，那一支的形状还在"）、两把橡皮同理、
固定项删不掉、删掉选中项之后选中态落回一个能用的工具、以及设置页那份列表的**行数必须等于数据项数**。
`CheckToolbarTouch` 另外钉住七颗钮仍是 40×40、焦点环仍在、"选中铺 accent / 未选透明 / 图标继承 on-accent 墨色"。
`CheckToolbarPlacement` 钉摆位：算术（合成工作区，不碰真屏幕）、接线（摆的是实测宽高、加一颗钮之后中心不跑）、
单位（框架那份 `WorkArea` 与 `GetMonitorInfo` 的物理矩形 ÷ 该屏 DPI 对得上）。本机 2560×1516 @175% 实测：
工作区 1462.86×866.29 DIP，395×68 的栏落在 (534, 792)，可见表面底边 854 —— 离任务栏 12 DIP。


## Critical: 画布按场景配（穿透模式 / 冻结模式）

画布的行为**按场景存**：`CanvasScene`（`ScreenAnnotation` 屏幕批注 + `Whiteboard` 白板）+ `CanvasSceneSettings`
（`PassThrough` / `Freeze` / `BackgroundArgb`，进存档的 `PreferenceSnapshot.CanvasScenes`）+ `CanvasOptions`（按场景存取的**唯一**入口，
`For(scene)` / `Set*(scene, value)`，`Changed` 带着"是哪个场景变了"）。加一个场景 = 加枚举成员 + 设置页给它一节，**数据模型不用动**。
"此刻哪块画布在眼前"不在这里 —— 它是 `CanvasSceneState.Active`，**不落盘**（见下一节）。
设置页因此有一页「画布」，导航六项：外观 / 墨迹 / 画布 / 工具栏 / 窗口与交互 / 关于。

两个开关都不是"点一下立刻改画面"，而是"下次进画布时按这个来"，所以它们的分支都挂在
`AnnotationToolbarWindow.SyncAnnotationOverlay` 这条必经之路上（鼠标模式干什么 / 进入画布那一刻干什么），
设置页那边还用一句话把当前行为念出来 —— 拨完开关没有任何即时反馈时，那是用户唯一能确认"它记住了"的地方。

### 穿透模式（`WS_EX_TRANSPARENT`）

鼠标模式下画布照旧盖在最上层，但鼠标与触摸直接落到它下面的窗口上 —— 批注留着不动，人继续操作电脑。

1. **是窗口样式位 + 分层，不是另开一块画布**（`NativeWindowZOrder.SetClickThrough`）：所以对已经在屏上的画布当场生效，
   不重建窗口、不重排 Z 序。验收回读的是样式位本身（`IsClickThrough`），不是"我们设过什么"。
   **配方是三件套**：`WS_EX_LAYERED` + `SetLayeredWindowAttributes(255)` + `WS_EX_TRANSPARENT`，
   外加画布窗口的 `WM_NCHITTEST` 钩子回 `HTTRANSPARENT`。
   **只有 TRANSPARENT 不够**：它单独用只对同一线程的兄弟窗口生效，"点到别的应用上"需要分层 ——
   用户报的正是这个（"穿透开着，点下去还是在写字"）。
   而"没设过分层属性的分层窗口根本不显示"，所以补 LAYERED 的同时必须把 attributes 设成完全不透明
   （改变的只是命中，不是显示）。
   **验收只能问外壳**：`WindowFromPoint(画布中心)` 必须不是画布 —— 这一条比任何属性断言都硬，
   因为命中测试本来就是外壳做的。
   **留给用户确认的一件事**：分层 + GPU 内容的像素级透明由 DWM 处理，但"穿透模式下笔迹是否照常显示"
   探针判不了，需要人眼确认一次（若被破坏，表现为画布变成一整块不透明的底；关掉穿透开关即恢复）。
2. **要补第二次（排到下一拍）**：外壳在 `Show` 与改置顶时会按自己的规则重算扩展样式，顺手把这一位抹掉。
   抹掉的两种后果都不报错 —— 穿透失效（点到了画布上），或切回书写时仍然穿透（落笔没反应）。
   旧代码"排到队尾再顶一次"踩的是同一个坑。
3. **顺序**：`Show()` 之后才设穿透。先设会被外壳抹掉。
4. **从没画过东西时不留空画布**：那是白多一个全屏最顶层窗口，而画布本来就是懒创建的。
5. **切回书写 / 擦除必须取消穿透**，否则落笔没反应（验收里有一条钉着）。
6. **穿透时必须撤掉冻结底图**：否则用户看着一张冻结的旧屏、点击却落在真实窗口上。

### 冻结模式（进入画布时截一张屏）

进入书写 / 擦除时把当前屏幕截一张图铺在画布最底下（`InkHost.Background = ImageBrush`），
底下的画面就停在那一刻 —— 视频、滚动页面、闪烁的进度条都不再变。

1. `ScreenCapture`：GDI `GetWindowRect` + `BitBlt`（**必须带 `CAPTUREBLT`**，否则别的应用的亚克力 / 分层窗口
   截出来是黑的）→ `BitmapSource.Create(..., PixelFormats.Bgra32, ...)`。
   不用 Jalium 的 `RenderTargetBitmap`：它只能渲染自己的视觉树，而这里要的是**别的应用**的像素。
2. **必须自己补 alpha**：GDI 的 `BitBlt` 只写 BGR 三个字节，alpha 留着那块内存里的旧值（多半是 0）。
   用 `Bgra32` 而不补成 255 的话整张图是全透明的 —— 不报错，只是画面空白，很难往"截图"上想。
3. **DPI 按 `ActualWidth`/`ActualHeight` 算，不用 `Width`/`Height`**：画布是最大化的，而
   `Window.Width` 在最大化窗口上报的是"还原尺寸"（WPF 一路是这个脾气，Jalium 照抄）。
   算错的表现只是"底图被缩放了一块"，同样不报错。验收钉的是"图的自然尺寸 == 画布尺寸"。
4. **截的时机在 `Show()` 之前**：这一刻屏幕上还没有画布。放到 Show 之后就是把自己（连同上一轮的笔记）
   一起截进去 —— 而底图是画在笔记下面的，烙进去就擦不掉了。
5. **同一轮画布里换工具不重截；离开过书写才重截**：那一刻屏幕上已经有笔记了。所以只在"画布从隐藏变为显示"时截
   （`_canvasPresented` 自己记，不读 `Visibility`）。
   **但穿透模式要单独记一笔**（`_leftDrawingSession`）：那时画布一直留在屏上，
   只看 `_canvasPresented` 的话，"用户去鼠标模式操作了半天再回来写"与"在画布里换了一下橡皮"
   长得一模一样，而这两件事该不该重截正好相反 —— 前者该截，后者不该。
6. **截的时候把自己的窗口用 `WDA_EXCLUDEFROMCAPTURE` 排除掉**（`SetWindowDisplayAffinity`）：
   否则批注栏被烙进底图，它一挪走原地就留一块"旧批注栏"的鬼影。这个接口要 Windows 10 2004+，
   本应用本来就只面向 Win11；不做事后兜底（"设不上就把窗口藏起来再截"会让每次进画布都闪一下批注栏，
   而这里只影响观感）。
7. 截不到（句柄没建出来、矩形为空、DIB 分配失败）就**没有底图**：画布回到透明，功能降级但不崩。


## Critical: 白板是第二块画布（选择 / 变换 / 漫游）

自 2026-09-25 起，本应用有**两块**画布。形状一样（全屏、由批注栏操作、在 `WindowLayer.Canvas`），
区别只在"有没有底"，而这一件把两边的行为分开得很彻底。

| 事实 | 位置 |
|---|---|
| 入口 | 工具栏固定项「白板」（`ToolbarToolKind.Whiteboard = 7`，图标 `E786` Slideshow，实测 ink=389）；点一次进、再点一次回屏幕批注 |
| 窗口 | `WhiteboardWindow`：`AllowsTransparency=false`、登记名「白板」、类与 `.jalxaml` 里一行 `Topmost` 都没有 |
| 墨迹面 | `CanvasSurface`（**两个窗口共用这一个类**）：引擎控件 + `InkHistory` + 属性/笔锋/压力下发 + 钳位 + 显式 `Dispose` |
| 底色 | `CanvasSceneSettings.BackgroundArgb` + `CanvasBackgroundPalette`（白 / 米灰 / 浅绿三档，**全是浅底**），在设置页「画布 › 白板」选，**当场生效** |
| 眼前是哪块 | `CanvasSceneState.Active`（不落盘）；`CanvasOptions` 退成纯按场景存取，`Changed` 如实报哪个场景变了 |
| 「选择」那颗 | 白板里 `Mouse` 这一项**换呈现不换身份**：图标 `E8B3`、念作「选择」、存档里 `Id="mouse"`/`Name` 一字不动 |
| 选择与变换 | 引擎半边：`SelectAt/SelectRect/SelectLasso` + `InkSelection.Translate/Scale/Rotate`（天生可撤销）；视觉半边在应用侧：`SelectionFrame` + `SelectionAdorner` |
| 漫游 | 引擎半边：`InkViewport` + `JaliumInkCanvas.PanByScreen/ZoomAt`；手势半边：`TouchGestureTracker`（框架自带的 Manipulation 只到单指平移） |

### 十二条判断（都是实测换来的）

1. **两块画布互斥为"在眼前"，但都不销毁**：藏起来不等于拆掉，各自的墨迹与撤销账会话内留着。
   以后"白板文件"就挂在 `CanvasSurface.Document` 上（引擎有 `Strokes` / `Replace` / `Clear` 这一对读写口），
   读档之后必须 `History.Clear()`（引擎注释：换文档不清历史，撤销会跨到另一份文档上）。
2. **共享靠组合，不靠基类窗口也不靠抄一份**：`AllowsTransparency` 必须在 `InitializeComponent` 之前定，
   所以"一个窗口两种形态"迟早变成满地 `if (_whiteboard)`；而抄两份引擎接线的代价是
   "橡皮钳位只在一块画布上修好"。`AnnotationOverlayWindow` 上那批**纯转发的成员已删**
   —— 白板那一块没有那层转发，留着就是静默断口的形状。
3. **所有"写到画布去"的动作一律经 `ActiveSurface`**（撤销 / 重做 / 清空 / 把选中项的数据下发）。
   不这么改的症状很具体：白板在眼前时按撤销，会安静地撤掉**另一块**画布的历史。
4. **不许给 `JaliumInkCanvas` 加 `RenderTransform` / `LayoutTransform`**：引擎已经把自己的视口矩阵写在
   内部层载体上，并且每个输入点在进内核前就按视口换算过。宿主再包一层 = 画面双份变换 + 落点错位。
   漫游只有 `ZoomAt` / `PanByScreen` 一条门。验收钉的是"控件 `RenderTransform` 为 null 而层载体矩阵等于 `Viewport.Scale`"。
5. **选框是一份独立状态（`SelectionFrame`：中心 + 两半轴 + 转角），不每帧从 `InkSelection.Bounds` 反推**：
   那是"选中墨迹的正立外包盒"，转过 45° 再读它会随转角越算越大；拖完再从它反推一次会把转角清零
   （用户看见选框"啪"地回正）。只在"选择集换了"的那一刻取一次。
6. **引擎没有 `SelectionChanged`，也不反向清理选择集**（被擦掉的编号一直留在 `Selection` 里），
   所以"选择还新不新"只有宿主管得着：订阅 `Document.Changed` 一律作废，
   唯独自己正在做的那批变换例外（判据是 `IsTransforming`，**挪 / 缩 / 转三种都算** ——
   只放过"挪"会让缩放第一帧就把选择自己清掉，报出来是"对角漂了 300 DIP"）。
7. **一次手势 = 一步撤销**：宿主在手势边界 `BeginBatch/EndBatch`，`InkSelection.Apply` 尊重批所有权。
   但 **`EndBatch` 在零变更时不产生一步历史** —— 所以"作废这次手势"必须先读 `OpenBatchChangeCount` 再决定撤不撤，
   无条件 `Undo()` 弹掉的是**上一步真操作**（症状："捏一下合少一笔"，长得极像引擎历史回放有毛病；
   先在干净板上跑最小复现 3→3 才确认是自己的账，没有误报上游）。
8. **手指按在空处不当场清选择**：它可能只是一次还没捏起来的手势起点；拖出框（或抬手确认是点）才清。
   鼠标与笔不延迟 —— 它们不会变成双指，当场清才是"点空处取消"该有的手感。
9. **`EraserRadius` 是世界单位**（引擎文档明写"这一步四个适配端目前都没做"）：模型与滑块仍说像素，
   进引擎前除以 `Viewport.Scale`，并在 `Viewport.Changed` 上重下发。漏了后者的症状是
   "缩放之后橡皮忽然变笨 / 变肥"，不报错。
10. **屏幕批注的视口钉死 1:1**：滚轮缩放与中键漫游是引擎壳里硬开、无公开开关，而批注的墨迹讲的是
    "屏幕上这一块"，一漫游字就与它标的那句话错位、按屏幕空间铺的冻结底图也对不上。
    修法是在窗口根上截 `PreviewMouseWheel` 与中键的 `PreviewMouseDown`；**左键不许截**
    （框架是"鼠标事件未被处理才提升成指针事件"，截了就等于关掉鼠标书写）。
11. **算角度要拿上一拍的点**：`_lastScreen` 在 switch 之前就被覆盖成当前点，于是 `before == after`、
    旋转增量恒为 0 —— 症状是"拖旋转柄完全没反应"，而"中心没跑""半径不变"两条断言对"什么都没发生"照样绿。
    抓手柄类断言必须有一条量**转角本身变了多少**。
12. **画出来那颗与点得中那颗必须同一张表**：手柄编号 0..7 顺时针 + 8 旋转柄只有一份
    （`SelectionFrame` 里），命中在**屏幕空间**比（世界空间比的话，缩小之后手柄间距近到点不准），
    且容差比画出来的半径宽 4 DIP。

### 白板特有的输入规矩

选择态把引擎模式设成 `InkEditingMode.None`（注释里写明的宿主槽："不接收墨迹输入，宿主自己在处理"），
白板窗口用 `AddHandler(PointerDown/Move/Up/Cancel, …, handledEventsToo: true)` 收 ——
必须带 `handledEventsToo`：引擎在选择态仍会把指针事件标成已处理，不带就**永远收不到落点**，
而"收不到"没有任何症状，只是选择不动。

手指分工是 owner 定的规则：**选择态**下单指点选 / 拖框、双指缩放与平移；**笔或橡皮态**下单指写、
多指各写各的（不做手势）。参与手势的只有手指，笔与鼠标左键永不截。

### 验收

`CheckWhiteboardCanvas`（换画布、共用工具参数、不透明的底、橡皮单位换算、批注视口钉死）、
`CheckWhiteboardUndoLands`（撤销接的是眼前这块的账，下一拍才落地）、
`CheckWhiteboardSelect`（点选 / 框选 / 套索 / 整块挪 / 一步撤销 / 选择集过期两条方向 / 删除所选）、
`CheckWhiteboardTransforms`（九颗手柄、对角为锚的缩放、真的转了多少、双指捏合与漫游、缩放后橡皮重下发）。
**变换与漫游那组刻意用一块干净的板**：长流程里堆了多轮撤销之后，一条断言红了说不清是谁的账。

仍需真手确认（写进 `tools/UiSmoke/README.md`，不假装验过）：物理双指的识别时序与手感、
大选择集逐帧实墨跟随时是否掉帧、笔在白板上的落笔手感、白板压得住真正常驻置顶的应用吗。

## Critical: 安装包走 CI（Windows 1 件 + Linux 每种架构 2 件），细节看 `packaging/README.md`

`.github/workflows/release.yml` 一次跑五个 job：`resolve`（版本号只算一次）→ `build`（矩阵三条腿：
Windows NSIS / Linux x64 / Linux arm64，后两条各出 deb + AppImage）→ `flatpak`、`linglong`
（都只装包，取 `linux-publish-x64-*` 那份产物，不重编）→ `release`。下面这些是"红一轮才知道"的，改工作流前先读：

1. **还原走的是双目标整张图**：应用在 `net10.0-windows` + `net10.0` 两条腿上，所以在 **Windows** 上
   `publish -f net10.0-windows` 一样会去问 FluentJalium 的 net10.0 —— 单目标时报
   `NU1201 Project FluentJalium is not compatible with net10.0`（实测红过一次，两端一起红）。
   FluentJalium 因此必须保持多目标（上游 `e1f3366` 起是；CI 里那条 sed 兜底已删），
   且签出 ref 是 **Astra** 而不是 `main`（main 那份布局里没有 `src/FluentJalium` 这个路径）。
2. **RID 不等于平台名**：`win-x64` / `linux-x64` / `linux-arm64`，写成 `windows-x64` 还原直接失败。
   dpkg 那一套又是另一种拼法（`amd64` / `arm64`），AppImage 内部还要再换成 `x86_64` / `aarch64` ——
   所以矩阵里 `rid` / `deb_arch` / `arch` 是三个键，别合并成一个。
3. **CI 上没有 FUSE**：AppImage 形态的工具挂不起来，`linuxdeploy` 又不认 `--appimage-extract-and-run`
   （回 `Flag could not be matched` 然后退出码 1）。改成用底下的 `appimagetool`：
   `--appimage-extract` 解包（不需要 FUSE）→ 点名跑 `extracted/usr/bin/appimagetool`。
   它还硬要 `desktop-file-validate`（缺了只打一行就退出码 1，runner 镜像没预装），
   且 `.desktop` / `.png` 必须有一份在 **AppDir 根**下（只在 `usr/share` 里 → `Desktop file not found`）。
4. **`choco install nsis` 之后同一步里 `makensis` 不在 PATH 上**（PATH 是进程启动那份）→ 按安装位置点名。
   NSIS 那两条：卸载侧 `UninstPage uninstConfirm` + `UninstPage instfiles` **两条都要**
   （关键字不是 `Confirm`；而少了 instfiles 那一页只警告不报错，卸载器一节都不跑）——
   工作流因此把 makensis 的日志读一遍，见 `no sections will be executed` 就红。
   **退出码 0 不等于包是对的**：这条是回读日志才发现的。

5. **Linux 每种架构的两个包共用该架构那一次 `dotnet publish`**：`flatpak` 与 `linglong` 两个 job
   下载 `linux-publish-x64-*` 那个中间产物，只装包不再编第二遍（各编一次迟早分出两个版本）。
   中间产物**不许**进 Release。名字必须带架构 —— upload-artifact v4 要求一次运行里产物名唯一。
6. **`download-artifact` 丢 Unix 的 +x 位**：所以每个装包脚本自己 `chmod +x` 那个 apphost，
   而 `build-deb.sh` 的入口守门只断言"文件在"。曾经过早上用 `test -x`：deb 静默变空串，
   错误报在玲珑的"取源失败"上，跟根因隔了三层。
7. **`ll-builder` 取 `kind: file` 用的是 `/usr/bin/wget`** → `file://` 与相对路径都不吃；CI 就地起
   `python3 -m http.server` 喂同一个 .deb。工具链在 `ppa.linyaps.org.cn` 的 OBS（Ubuntu 那份目录名
   `Ubuntu_24.04`，flat repo；官方 install.md 写的 `ci.deepin.com/…/xUbuntu_24.04` 已 404），
   而且 `linglong-box` 必须点名装 —— 它的 Depends 写 `linglong-box | crun`，runner 上有 crun，
   apt 便跳过 box，而 `ll-builder` 找的是 `ll-box` 这个可执行文件。
   产出的离线单文件是 **`.uab`**（`ll-builder export` 的默认），不是 `.lca`。
8. **APPID 一处定义、CI 扫一致性**：`packaging/linux/appid` = `io.github.wwiinnddyy.lanstartwrite`，
   `.desktop` / 图标按它命名，deb / Flatpak 清单 / 玲珑模板都读它；deb 那一步扫 `packaging/linux`
   下所有 `*.lanstartwrite` 形状的名字，出现第二个值就红（抄不一致的症状是"装了三个各带一张
   默认图的同名应用"，不会报错）。
9. **deb 的验收是"在 docker 的 ubuntu:24.04 里真装一遍"**：`apt install` + 落位条数 +
   `desktop-file-validate` + 白名单外的缺库即红。实测唯一缺的是 `liblttng-ust.so.0`
   （coreclr 的 LTTng provider，24.04 只给 `.so.1`，缺了只是没追踪）—— 所以它进白名单，
   不是往 `Depends` 里写一条装不上的包；`Depends` 实测只需 `libc6, libstdc++6`。
   **arm64 那一腿的读数与 x64 完全相同**（见第 12 项），白名单不扩、`Depends` 不改。
10. **Linux 腿的 `if` 一律看 `matrix.kind`，不看 `matrix.os`**：加了 `ubuntu-24.04-arm` 之后，
    "这是不是一台 Linux"用 os 等值判断会静默失配 —— AppImage / deb 两步整步不跑，
    而绿会一路留到"上传构建产物"那步才因 `if-no-files-found: error` 红，报得像打包坏了。
11. **16K 页对齐是一条门禁，不是口头承诺**：每个 Linux 腿在打包前跑
    `python3 packaging/linux/check-page-align.py publish --require-machine <名> --require-align <字节>`。
    arm64 要求 ≥16384（arm64 内核有 4K/16K/64K 三档，`p_align` 停在 4K 的包在页大于 4K 的内核上
    **装得上、跑不起来**，错在 exec / dlopen，CI 里零征兆）。**门槛按架构分档，不许一刀切**：
    实测 x64 那份产物 23 个 ELF 全是 `p_align = 0x1000`，抬到 16K 得到 23 条红 —— 而 arm64 那份
    实测是 `0x10000`（64K），比 16K 高一档。脚本纯 stdlib（本机没有 readelf，同一份要在本机与
    runner 上都能跑），四条红路各验过：改 `p_align`、换机器类型、空目录、抬门槛；
    **"一个 ELF 都没扫到"也算红**，目录写错的绿比没检查更坏。
12. **arm64 那一腿在真 runner 上已经跑绿**（探针 run 36106109770，`ubuntu-24.04-arm`）：deb 33 MiB、
    AppImage 40 MiB，容器里 apt 装得上、落位 296 条、`desktop-file-validate` OK，
    缺库只有白名单那一条（与 x64 完全相同，所以白名单没扩）。那台 runner **自带 docker**，
    不需要 qemu 模拟 —— `ubuntu:24.04` 在 arm 上解出来的就是 arm64 镜像，装的就是刚造的 arm64 deb。

实测（run 36030896388，v1.0.1，五个 job 全绿，Release 上正好五件资产）：setup.exe 32 MiB、
deb 35 MiB（296 条落位、apt install OK、desktop-file-validate OK）、AppImage 42 MiB、
flatpak 32 MiB（沙箱内自检 `/app/bin/lanstartwrite` 与 apphost 均可执行）、玲珑 uab 65 MiB。
**arm64 那一腿的数是后来探针跑 36106109770 补的**（见上一条第 12 项：deb 33 MiB、AppImage 40 MiB，
容器装包、落位条数与缺库清单都与 x64 一致）。所以一次完整跑的 Release 上是**七件**资产 ——
setup、两份 deb、两份 AppImage、flatpak、玲珑 —— 不再是五件。

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
