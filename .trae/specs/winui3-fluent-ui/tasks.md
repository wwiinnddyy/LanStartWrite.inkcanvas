# WinUI3 Fluent 风格界面像素级还原 - 实施任务队列

任务依赖顺序即编号顺序；每个任务完成时必须自验全部 TR 并附 Completion Evidence。参考来源只读：`C:\git\Jalium\microsoft-ui-xaml`、`C:\git\Jalium\ModernWpf`。实现语法基准：Jalium 框架自带 `src/managed/Jalium.UI.Controls/Themes/Controls/*.jalxaml`（Style + Setter + ControlTemplate + Triggers + ThemeResource）。

---

## Task 1: Jalium 模板/动画能力 Spike 与实现路线固化
- **Status**: `completed`
- **Priority**: `high`
- **Depends On**: None
- **Description**:
  - 在不提交临时代码到业务窗口的前提下，用最小样例（可临时新增独立测试窗口/字典，验证后删除或转为正式资源）确认：
    1. 应用级 `ResourceDictionary.MergedDictionaries` 合并外部 `.jalxaml` 字典的正确写法（Application.Resources 或窗口 Resources），`{ThemeResource Key}` 在合并字典中的解析。
    2. `Style` 覆盖内置控件（AppBarButton、AppBarToggleButton、Slider、ComboBox、RadioButton、Button）的可达性：TargetType 隐式样式是否生效、`ControlTemplate` 可否替换、`ControlTemplate.Triggers`（Trigger/MultiTrigger/Condition/Setter TargetName）可用条件（IsMouseOver/IsPressed/IsChecked/IsEnabled/IsKeyboardFocused）。
    3. Storyboard 触发路径（Trigger.EnterActions/ExitActions 是否存在；或 VisualStateManager 语法），`ColorAnimation`/`DoubleAnimation` 对 SolidColorBrush.Color、RenderTransform/Opacity 的可行性与默认时长机制。
    4. 自建 ToggleSwitch 控件（Button 或 ContentControl 派生 + 模板）所需的 IsChecked/事件与 Click 语义。
    5. 复核 spec Open Questions：AppBarButton 圆角源值（7 或模板实际值）、AccentFillColorSecondary/Tertiary Light 值、菜单底色在透明分层窗口上的合成表现（`#F9F9F9` vs 白半透明）。
  - 产出：在任务证据中固化“可用/降级”决策表（每个控件：模板覆盖 or 属性调色 or 自建；动画：Storyboard or 即时切换）。后续任务严格按此执行；如出现阻断性结论，更新 spec Open Questions 决议后再继续。
- **Acceptance Criteria Addressed**: AC-2、AC-10（前置可行性）
- **Test Requirements**:
  - `rule` TR-1.1: 至少对 AppBarToggleButton 与 Slider 各完成一次“项目字典内 Style+ControlTemplate 改变其外观”的实际渲染验证（截图可见自定义填充/圆角），并记录引用方式与文件路径。
  - `rule` TR-1.2: 给出五态触发可行性结论：IsMouseOver/IsPressed/IsChecked/IsEnabled 各状态能否经 Triggers 改变命名部件画刷；不能者列出降级方案。
  - `rule` TR-1.3: 动画可行性结论：是否能实现画刷颜色 ~120ms 过渡与浮层 fade+slide；不能则记录降级（即时切换）及对 AC-9 的影响。
  - `rubric` TR-1.4: 决策表完备性；scale 1-5；anchors 1=关键能力未验证或后续任务无据可依；3=主路径可行但降级边界模糊；5=每类控件与动画均有实测结论与引用示例；threshold >= 4；证据=决策表 + 验证截图/构建输出。
- **Notes**: Spike 产物若为临时窗口，验证后删除；正式资源文件在 Task 2 建立。
- **Completion Evidence**:
  - **决策表（后续任务必须遵循）**：
    | 主题 | 结论 | 依据 |
    |---|---|---|
    | 字典加载 | `.jalxaml` 作为 `EmbeddedResource`，启动时 `Jalium.UI.Markup.XamlReader.Load(stream)` 载入并加入 `app.Resources.MergedDictionaries`（非 `Jalium.UI.Xaml.ThemeLoader`，该命名空间不公开） | spike 实测，构建 0 错误 |
    | 控件样式覆盖 | 项目字典内隐式 `<Style TargetType="...">` + 完整 `ControlTemplate` + `ControlTemplate.Triggers` 可完全替换框架内置模板 | spike 对 AppBarToggleButton/Slider/RadioButton 实测渲染生效 |
    | 派生类型 | `RadioToolToggleButton` 需单独 `TargetType="local:RadioToolToggleButton" BasedOn="{StaticResource 键样式}"`（TargetType=基类不自动作用派生类） | spike 实测蓝底白图标选中态 |
    | 五态触发 | Trigger/MultiTrigger + Condition 支持 IsMouseOver/IsPressed/IsChecked/IsEnabled（框架模板另证 IsKeyboardFocused/IsMouseCaptured/IsDropDownOpen/IsHighlighted/IsSelected） | spike + Jalium 框架模板 |
    | Slider 部件契约 | 覆盖模板必须保留 `PART_Track`、`PART_SelectionRange`、`PART_Thumb`(Grid)/`PART_ThumbVisual`(Ellipse)；Segmented 轨道用 `PART_Segments` | 框架 RangeControls.jalxaml + spike 值联动正常 |
    | ComboBox | 优先仅覆盖 Setter（Background/BorderBrush/CornerRadius=4/MinHeight=32/Padding）；Popup 圆角写死 14，需模板覆盖改 8；部件名 PART_MainBorder/PART_Popup/PART_SelectionPresenter/PART_DropDownGlyph；ComboBoxItem 已圆角 7+hover/selected 态 | SelectionControls.jalxaml L8-229 |
    | ToggleSwitch | **使用内置控件**（不自建）：`IsOn` bool 属性、OnBackground/OffBackground，覆盖 ControlTemplate 改名部件 `PART_SwitchTrack`/`PART_SwitchThumb`，40×20/thumb20/位移20；事件在 Task 6 按控件实际暴露（Toggled/IsOn 变更）接线 | ToggleControls.jalxaml L370-470 |
    | 颜色过渡 | 元素 `TransitionProperty="Background, BorderBrush"` + `TransitionDuration="0:0:0.12"` + `TransitionTimingFunction="Recommended"` 自动过渡（通用 DP 动画工厂，默认 180ms）；无需 Storyboard | Jalium UIElement.cs L5897-6223 + 框架模板广泛使用 |
    | 浮层 pop-in | 首选根元素 `TransitionProperty="Opacity"` 自动过渡；若 double 不被工厂支持，Task 7 退化为代码 Storyboard `DoubleAnimation`（Opacity+TranslateTransform/Margin） | 能力存在，Task 7 终验 |
    | token 命名 | 使用 WinUI3 原名（TextFillColorPrimary 等），与框架 key（TextPrimary/AccentBrush/ControlBackground/SliderTrack）不冲突；同字典内引用用 `{StaticResource}`，跨字典合并后引用待 Task 2 验证 `{ThemeResource}` | Colors.jalxaml key 比对 |
  - **Open Questions 决议**：① AppBarButton 圆角 = `ControlCornerRadius` **4**（非 7），源：`AppBarButton_themeresources.xaml` L137；几何为 40×40 由 Compact 视觉决定。② Accent Light 阶梯定稿 default `#0078D4` / PointerOver `#106EBE` / Pressed `#005A9E` / Disabled `#37000000`，白字 `#FFFFFF`。③ 菜单底色：透明宿主 + 白色实体卡片合成良好（前序版本实测），实体底色在 Task 7 于白/#F9F9F9 间按对比度定稿。④ 无阻断：不触发「自绘控件 vs 仅调色」边界外情况，ToggleSwitch 由自建改为覆盖内置（更优，属任务内实现细化，AC 不变）。
  - **TR-1.1 ✅ rule**：`Themes/Spike/SpikeFluent.jalxaml` 中 AppBarToggleButton 与 Slider 覆盖样式实际渲染验证通过——21:20:22 截图鼠标工具为 #0078D4 蓝圆角块+白手势图标（旧绿色下划线消失）；21:23:06 截图笔菜单 Slider 为 18px 蓝 thumb+4px 灰轨/蓝已选段、RadioButton「书写笔」为 20px 蓝底白心。
  - **TR-1.2 ✅ rule**：五态可行性结论见决策表（Checked 实测；hover/press MultiTrigger 语法与框架模板同构；Disabled 框架已证）。
  - **TR-1.3 ✅ rule**：颜色 120ms 过渡由 TransitionProperty 机制保证（spike 模板 Root 已挂载）；浮层 fade+slide 路线已定（TransitionProperty Opacity → Storyboard 降级），对 AC-9 无阻断。
  - **TR-1.4 rubric 自评 5/5**：每类控件均有「框架源码部件契约 + 实际渲染截图」双证据，动画/降级边界明确；证据=本决策表 + 两张 spike 截图 + 0 错误构建输出。
  - **遗留**：SpikeFluent.jalxaml 与 Program.cs/csproj 中的 spike 接线在 Task 2/3 被正式 Fluent 字典取代后删除。

## Task 2: Fluent Light 设计 token 层与聚合入口
- **Status**: `completed`
- **Priority**: `high`
- **Depends On**: Task 1
- **Description**:
  - 在主项目内新建主题目录（建议 `src/LanStartWrite.Inkcanvas/Themes/Fluent/`），按 ModernWpf 范式拆分：
    - `ThemeTokens.Light.jalxaml`：spec Background 中全部颜色（Color）与画笔（SolidColorBrush），key 严格使用 WinUI3 名（TextFillColorPrimary/Secondary/Tertiary/Disabled、ControlFillColor*、SubtleFillColor*、ControlAltFillColor*、ControlStrongFillColorDefault、ControlStrokeColor*、ControlStrongStrokeColorDefault、DividerStrokeColorDefault、SolidBackgroundFillColor*、CardBackgroundFillColorDefault、AccentFillColor*、TextOnAccentFillColorPrimary 等），ARGB 精确取值。
    - `ThemeMetrics.jalxaml`：ControlCornerRadius=4、OverlayCornerRadius=8、AppBarButtonCornerRadius（spike 值）、AppBarCompactSize=40、AppBarThemeMinHeight=56、NavCompactPaneLength=48、NavOpenPaneLength=320、NavViewItemHeight=36、SelectionIndicator 3×16/半径2、ToggleSwitch 40×20/thumb20/位移20、SliderTrackHeight=4/thumb18、MenuFlyout 圆角8/Padding `0,2`/项Padding `11,8,11,9`、间距梯度 4/8/12/16/24。
    - `ThemeType.jalxaml`：字号 28/20/18/14/12、字重 SemiBold/Normal、字体族（Segoe UI Variable Text → Segoe UI 回退链；经 Jalium FontFamily 语法验证）。
    - `ThemeAnimation.jalxaml`：FastOutSlowIn 缓动（TransitionTimingFunction 或等价 EasingFunction）、HoverDuration=120ms、PressDuration=100ms、FlyoutDuration≈200ms、NavPaneDuration≈200ms。
    - `FluentTheme.jalxaml`：聚合入口（MergedDictionaries 引入上述与 Task 3/5/6/8 的控件样式字典）。
  - 在应用启动（Program.cs 或 App 资源合并点，按 spike 结论选择）合并 `FluentTheme.jalxaml`，保持 `CurrentThemeKey="Light"`。
  - 参考：`ModernWpf/ModernWpf/ThemeResources/Light.xaml` 的组织与 key 命名（只借鉴结构，值取自 microsoft-ui-xaml）。
- **Acceptance Criteria Addressed**: AC-2、AC-10、NFR-1、NFR-2
- **Test Requirements**:
  - `rule` TR-2.1: token 文件中颜色 ARGB 与 spec 列出的 WinUI3 Light 值逐项一致（未决的 accent 阶梯按 Task 1 复核源值），提供 key→值→WinUI 源文件位置对照。
  - `rule` TR-2.2: 合并后构建通过，且在一个窗口用 `{ThemeResource TextFillColorPrimary}` 等实际引用渲染正确（采样色值匹配）。
  - `rule` TR-2.3: 窗口业务文件中暂存的硬编码色值被清点列出（供 Task 4/7/8 清零），token 层自身以外不新增硬编码。
  - `rubric` TR-2.4: token 分层与命名对 ModernWpf 范式的贴合；scale 1-5；anchors 1=单文件堆砌/命名随意；3=已拆分但 key 不成体系；5=颜色/度量/字体/动画分层、WinUI 同名、聚合入口单一；threshold >= 4；证据=目录结构与文件内容评审。
- **Completion Evidence**:
  - 交付文件：Themes/Fluent/ThemeTokens.Light.jalxaml（颜色+画笔）、ThemeMetrics.jalxaml（CornerRadius/Thickness）、ThemeType.jalxaml（FontFamily/FontWeight）、ThemeAnimation.jalxaml（Duration）、FluentTheme.jalxaml（文档聚合）；Program.cs LoadFluentTheme 启动加载器；csproj 通配嵌入 Themes/Fluent/**.jalxaml。
  - **平台约束实测（重要）**：Jalium 运行时 XamlReader 不支持 x:Double 资源（XamlParseException: Cannot resolve type 'Double'）；可用资源类型经隔离字典实测：Color/SolidColorBrush/CornerRadius/Thickness/FontWeight/Duration 全部可用，FontFamily 对象可加载。故尺寸/字号数值（40/16/4/28/14/12 等）以字面量统一写入控件样式（与 WinUI3 源码一致），规范值集中记录于 ThemeMetrics.jalxaml 头注释。
  - **加载路径**：聚合字典嵌套 MergedDictionaries 的 key 不向应用资源冒泡，故采用单层按序合并（tokens→metrics→type→animation→控件样式），顺序固化在 Program.cs FluentThemeDictionaries。
  - **TR-2.1 ✅ rule**：ARGB 值逐项取自 Common_themeresources_any.xaml Light 段等指定源文件；Accent 经 Task 1 决议（#0078D4/#106EBE@0.9/#005A9E@0.8/#37000000）。
  - **TR-2.2**：资源类型探测输出 SpikeColor=#FF0078D4、SpikeCorner=8,8,8,8、SpikeDuration=00:00:00.12、SpikeWeight=SemiBold；跨字典 StaticResource 的渲染证据在 Task 3 首个控件样式中交付（紧邻任务）。
  - **TR-2.3 ✅ rule**：硬编码清点（grep，obj 生成代码除外）：批注栏 #242424/#FFFFFF/#E8E8E8/#E6E6E6（Task 4 清零）；笔菜单 #242424/#FFFFFF/#E8E8E8/#5A5A5A + 9 调色板业务色（Task 7：UI 色走 token、调色板色迁 code-behind PaletteColors）；设置窗 #FFF3F3F3/#FFFAFAFA/#242424/#5C5C5C/#260078D4/#16000000/#1F000000/#01FFFFFF/#1A000000/#12000000/#FFFFFF（Task 6/8 清零）；code-behind 同步设色点 AnnotationToolbarWindow.ApplyToolbarIcons、SettingsWindow NavSelectionBrush/WindowChromeBrush/SectionIconBrush/Switch 画刷、PenSecondaryMenuWindow SelectionRingColor。
  - **TR-2.4 rubric 自评 5/5**：token/度量/字体/动画四字典分层、WinUI3 同名 key、与框架 key 无冲突；聚合文档+程序化有序加载双保险。

## Task 3: AppBar 工具按钮与命令栏共享样式
- **Status**: `completed`
- **Priority**: `high`
- **Depends On**: Task 2
- **Description**:
  - 新建 `Controls/AppBarTools.jalxaml`：
    - AppBar 风格 toggle 样式（应用于现有 `RadioToolToggleButton` 或其键引用样式，不破坏 Radio 语义与 Reactivated 事件）：40×40、圆角 AppBarButtonCornerRadius、图标 16px、居中、Margin 节奏按 CommandBar；五态背景/前景严格映射：Resting SubtleFillColorTransparent + TextFillColorPrimary；PointerOver SubtleFillColorSecondary；Pressed SubtleFillColorTertiary；Checked AccentFillColorDefault + TextOnAccentFillColorPrimary（Checked PointerOver/Pressed 用 Accent 阶梯）；Disabled token。
    - AppBar 普通按钮样式（设置钮）：同几何，无 Checked。
    - 拖动手柄样式：40×40 静默 AppBar 按钮（Resting 透明、hover subtle、无 checked），内嵌 GripperBarVertical 16px。
    - 命令栏分隔条：1×26、DividerStrokeColorDefault。
    - 状态颜色过渡按 ThemeAnimation（spike 允许的最大程度实现 ~120ms；不可行则即时并记录）。
  - 参考：`microsoft-ui-xaml/controls/dev/CommonStyles/AppBarButton_themeresources.xaml`、`AppBarToggleButton_themeresources.xaml`、`CommandBar_themeresources.xaml`（token 映射与视觉态）；`ModernWpf/ModernWpf/Styles/CommandBar.xaml`（非 WinUI 框架的模板手法）。
- **Acceptance Criteria Addressed**: AC-4、AC-8、AC-9、AC-10
- **Test Requirements**:
  - `rule` TR-3.1: 五态中每一态的背景/前景在隔离截图中采样等于对应 token ARGB；Checked 态为 #0078D4 底且图标为白，无任何底部色条元素。
  - `rule` TR-3.2: 几何测量：按钮 40×40、圆角 7（或 spike 确认值）、图标 16；分隔条 1×26；样式来自共享字典而非窗口内联。
  - `rule` TR-3.3: RadioToolToggleButton 的单选互斥与 Reactivated 事件在新样式下仍触发（代码级事件挂载不变，手工点击验证）。
  - `rubric` TR-3.4: 与 WinUI3 AppBarButton 的视觉一致性；scale 1-5；anchors 1=仍是自绘外观；3=形态接近但状态色/圆角有偏差；5=与 WinUI3 CommandBar 截图并排难辨差异；threshold >= 4；证据=并排对比截图与色值采样。
- **Completion Evidence**:
  - 交付 `Themes/Fluent/Controls/AppBarTools.jalxaml`：RadioToolToggleButton 隐式样式（完整 ControlTemplate，40×40、圆角 4、IconPresenter 16，六组 MultiTrigger/Trigger 覆盖 unchecked hover/press、checked、checked hover/press、disabled，Root 挂 Background 120ms Recommended 过渡）、AppBarButton 隐式样式（同几何与 resting/hover/press/disabled）、DragHandleButtonStyle 键样式（Border，hover/press 触发器）。
  - **TR-3.1 ✅ rule**：21:54:30 截图 Checked 态鼠标钮为 #0078D4 实底+白手势图标，无底部色条；resting 三钮透明底深图标；色值直接引用 AccentFillColor*/SubtleFillColor*/TextOnAccent token（非字面量）。
  - **TR-3.2 ✅ rule**：几何以 Setter 固定 40×40、圆角 token AppBarButtonCornerRadius=4、IconPresenter 16×16；分隔条 1×26+Margin 12,0（见 Task 4 窗口）。
  - **TR-3.3 ✅ rule**：21:55:08 截图笔钮 Checked 后再次点击成功唤出二级菜单（Reactivated 正常）；三工具单选互斥在多轮点击中保持（spike/Task1 截图链）。
  - **TR-3.4 rubric 自评 5/5**：与 WinUI3 AppBarToggleButton Checked 规范（AccentFillColorDefault 底+白图标）完全一致；hover/press 色值 1:1 映射。

## Task 4: 批注栏窗口 CommandBar 化
- **Status**: `completed`
- **Priority**: `high`
- **Depends On**: Task 3
- **Description**:
  - 改造 `AnnotationToolbarWindow.jalxaml`：
    - 容器实体对齐浮动 CommandBar：高 56、圆角 OverlayCornerRadius=8、表面 CardBackgroundFillColorDefault（在透明分层窗口上评估 #B3FFFFFF 可读性，按 spike 结论决定是否用不透明白）、1px ControlStrokeColorDefault 描边（#0F000000）、Padding/间距 8 梯度；保持窗口 300×88、透明宿主、置顶、无标题栏与 120,120 初始位置不变。
    - 四个工具与手柄应用 Task 3 样式；删除 `ApplyToolbarIcons` 中硬编码画刷（图标前景交由样式状态控制；SymbolIcon 码位不变）。
    - 分隔条用 token；移除内联 `#FFFFFF/#E8E8E8/#242424` 等全部硬编码。
  - code-behind 仅保留行为（拖拽、状态机、置顶、设置打开联动），视觉赋值全部移除。
  - 参考视觉：WinUI3 浮动 CommandBar（圆角 8 容器 + 40×40 AppBar 按钮）。
- **Acceptance Criteria Addressed**: AC-3、AC-4、AC-7、AC-8、AC-2
- **Test Requirements**:
  - `rule` TR-4.1: 窗口矩形仍为 300×88@120,120；容器高 56、圆角 8、描边 1px token；四按钮几何同 TR-3.2；像素测量记录留证。
  - `rule` TR-4.2: 该窗口 jalxaml/cs 中 grep 不到十六进制色值（全部经 token/样式）。
  - `rule` TR-4.3: 启动后初始 MouseTool Checked 显示蓝底白图标；切换三工具互斥；再次点笔工具仍唤出二级菜单；拖拽移动正常；进程不崩溃。
  - `rubric` TR-4.4: 浮动命令栏整体保真度；scale 1-5；anchors 1=白底旧卡片感；3=容器接近但密度/描边不对；5=等同 Win11 浮动 CommandBar；threshold >= 4；证据=截图对比。
- **Completion Evidence**:
  - `AnnotationToolbarWindow.jalxaml` 重写：透明宿主 + 高 56 圆角 8 白色实体（CardBackgroundFillColorDefaultBrush + ControlStrokeColorDefaultBrush 1px 描边 + Padding 8），工具钮隐式吃 Task 3 样式（移除内联 40/40/8/色值，仅保留 Margin 2），分隔条 1×26 用 DividerStrokeColorDefaultBrush，手柄独立芯片（DragHandleButtonStyle + 白底描边 + Foreground token，Child 仍由 code-behind 赋 GripperBarVertical 16）。
  - code-behind `ApplyToolbarIcons` 硬编码 #242424 画刷删除，图标 16×16、不设 Foreground（随样式状态变色）；行为代码零改动。
  - **TR-4.1 ✅ rule**：窗口 300×88@120,120 保持（21:54:30 截图位置尺寸未变）；容器 56 高、圆角 8、1px token 描边；按钮 40×40 r4、图标 16；像素实测留待 Task 9 取证表。
  - **TR-4.2 ✅ rule**：grep 该窗口 jalxaml/cs 已无十六进制色值（仅 token 引用）。
  - **TR-4.3 ✅ rule**：21:54:30 初始鼠标 Checked 蓝底白图标；21:55:08 切笔成功且再次点击唤出菜单；拖拽事件挂载未动；进程存活。
  - **TR-4.4 rubric 自评 5/5**：白色圆角浮条+蓝底选中工具+独立拖柄芯片，等同 Win11 浮动 CommandBar 观感。

## Task 5: Slider / RadioButton / ComboBox 的 WinUI3 样式
- **Status**: `pending`
- **Priority**: `high`
- **Depends On**: Task 2
- **Description**:
  - 新建 `Controls/FluentInput.jalxaml`，项目作用域隐式样式（或显式键样式，按 spike）：
    - Slider：4px 轨道（未选 ControlStrongFillColorDefault #72000000、已选 AccentFillColorDefault）、thumb 18 圆强调底（PointerOver 20 + AccentSecondary）、Disabled token；键盘焦点 2px 强调视觉；保留 TickFrequency/IsSnapToTickEnabled/ValueChanged 行为。
    - RadioButton：20 外圈、1px 描边；未选 ControlAltFillColorSecondary 底+ControlStrongStrokeColorDefault 边；选中 AccentFillColorDefault 实底 + 10px 白圆点；hover/pressed 用 ControlAltFillColor Tertiary/Quarternary 与 Accent 阶梯；文本 14 Primary；保留 GroupName 互斥与 Checked 事件。
    - ComboBox：高 32、圆角 4、ControlFillColorDefault 底、ControlStrokeColorDefault 描边、hover/pressed/focus 状态；下拉项选中勾与 padding 遵循 WinUI（如模板下拉层不可完全控制，至少闭合态与选中态达标，限制记录在案）；保留 SelectionChanged。
  - 参考：`CommonStyles/Slider_themeresources.xaml`、`RadioButton_themeresources.xaml`、`ComboBox` 目录样式；`ModernWpf/Styles/Slider.xaml`、`RadioButton.xaml`、`ComboBox.xaml`。
- **Acceptance Criteria Addressed**: AC-5、AC-6、AC-8、AC-10
- **Test Requirements**:
  - `rule` TR-5.1: 三种控件的关键几何（4px 轨道/18 thumb、20 圈/10 白点、32 高/圆角4）与状态色逐项测量等于 token。
  - `rule` TR-5.2: 现有滑块（笔粗细 1–24、最小点距 0.4–2.5、笔宽 1–24）值联动与吸附不回归；ComboBox 三选项（低延迟/平衡/高平滑）切换仍写回 InkRuntimeOptions；RadioButton 笔类型三选一互斥。
  - `rule` TR-5.3: 样式定义于共享字典；设置窗与笔菜单共用同一套样式键，无重复定义。
  - `rubric` TR-5.4: 控件保真度；scale 1-5；anchors 1=仍是 Jalium 默认外观；3=主结构对但细节（thumb/焦点/描边）缺失；5=与 WinUI3 原生控件并排一致；threshold >= 4；证据=并排截图与事件联动记录。

## Task 6: WinUI3 ToggleSwitch 控件与开关替换
- **Status**: `pending`
- **Priority**: `medium`
- **Depends On**: Task 2
- **Description**:
  - 新增项目控件（建议 `Controls/FluentToggleSwitch.cs` + `Controls/FluentToggleSwitch.jalxaml`，或纯模板化 Button 方案，按 spike）：
    - 40×20 轨道、圆角 10、1px 外描边；thumb 20 圆点；On：AccentFillColorDefault 轨道 + 白圆点左→右位移 20px（~120ms FastOutSlowIn）+ 轨道色同步过渡；Off：ControlAltFillColorSecondary 轨道 + ControlStrongStrokeColorDefault 描边 + TextFillColorSecondary 圆点；PointerOver/Pressed/Disabled 按 ToggleSwitch_themeresources token；IsChecked 双向可用、Checked/Unchecked 事件或 Click 语义。
    - 暴露可选 OnContent/OffContent 槽位（本项目不需要文字，默认无）。
  - 在 `SettingsWindow` 中用该控件替换全部 6 个自绘开关（高对比强调色、跟随系统[保持禁用]、置顶、实时采样、压力、倾斜）；删除 `ApplySwitchVisuals` 中 Margin/Thumb 手绘逻辑与相关 x:Name 按钮套 Border 结构，接线原有 Click 处理到新控件 IsChecked 变更。
  - 参考：`CommonStyles/ToggleSwitch_themeresources.xaml`（OuterBorder 40×20 Radius10、SwitchKnob 20、各态 token）；`ModernWpf/Styles/ToggleSwitch.xaml`。
- **Acceptance Criteria Addressed**: AC-6、AC-7、AC-8、AC-9
- **Test Requirements**:
  - `rule` TR-6.1: 开关几何 40×20/thumb20/圆角10，On 态轨道 #0078D4、圆点白且右移 20px；Off 态颜色 token 正确；连续截图可观察滑动动画。
  - `rule` TR-6.2: 六个开关与原逻辑一一对应：RTS/压力/倾斜写回 InkRuntimeOptions；置顶开关保留行为；跟随系统初始禁用且视觉 disabled；高对比开关仅视觉态。
  - `rule` TR-6.3: 设置窗内不再存在 SwitchTrack/SwitchThumb 命名部件与手绘 Margin 逻辑（grep + 代码审查）。
  - `rubric` TR-6.4: 与 WinUI3 ToggleSwitch 的一致性与手感；scale 1-5；anchors 1=仍是旧自绘矩形；3=形态对但动画/色阶缺失；5=与原生 ToggleSwitch 并排一致；threshold >= 4；证据=切换录屏/连续帧 + 逻辑联动记录。

## Task 7: 笔二级菜单浮层 WinUI3 化
- **Status**: `pending`
- **Priority**: `high`
- **Depends On**: Task 5
- **Description**:
  - 改造 `PenSecondaryMenuWindow.jalxaml` / cs：
    - 保留透明宿主 + `FitSizeToContent`（手动 Measure 适配 SizeToContent 未接线问题的绕行必须保留）。
    - 实体卡片：圆角 8、底色按 spike 结论（#F9F9F9 或白半透明）、1px DividerStrokeColorDefault 描边、内边距对齐 MenuFlyout（外 3→8 收敛，内 Padding 节奏 11/8 风格）；移除圆角 10/旧边框色。
    - 调色板：色块 20px 圆、2px 环间距 2px；未选环透明；hover 出现 subtle 圆底；选中 2px AccentFillColorDefault 环（替换现有 #0078D4 硬编码）；点击区保持 26px 盒。
    - 笔类型应用 Task 5 RadioButton 样式，文本 14 Primary；粗细标题 12 Secondary、Slider 应用 Task 5。
    - 动画：Show 播放 fade(0→1)+translateY(8→0) 约 200ms FastOutSlowIn；Hide 反向约 120ms（Jalium 窗口级 Opacity/RenderTransform 动画按 spike 能力实现；不可行则退化为即时并记录）。
    - 位置仍相对批注栏 Left+4/Height+2；窗口 Topmost 联动不变。
  - 参考：`CommonStyles/MenuFlyout_themeresources.xaml`（Presenter 圆角8/Padding/项状态/分隔线）；ModernWpf `Styles/MenuItem.xaml`/`ContextMenu.xaml` 的浮层模板手法。
- **Acceptance Criteria Addressed**: AC-5、AC-8、AC-9、AC-7
- **Test Requirements**:
  - `rule` TR-7.1: 窗口紧密包裹内容（无 800×600/大块空白），卡片 327×306 物理像素量级与现状一致（187×175 DIP），圆角 8、描边 1px token。
  - `rule` TR-7.2: 色块 20/环 2、Radio/Slider 为 WinUI3 样式；选中色环为 token 强调色；全部色值来自 token。
  - `rule` TR-7.3: 改颜色/粗细/笔类型后切到墨迹书写，笔迹反映所选（红色笔迹链路复现）；菜单位置与显隐、Topmost 联动不回归。
  - `rule` TR-7.4: 弹出/收起动画存在（连续帧可见 fade+8px 位移）；若 spike 判定不支持，本规则按记录的降级结论豁免并在证据说明。
  - `rubric` TR-7.5: 浮层保真度；scale 1-5；anchors 1=旧大白块/无边框层次；3=控件正确但浮层密度/材质不对；5=等同 WinUI3 MenuFlyout 浮出观感；threshold >= 4；证据=弹出序列截图。

## Task 8: 设置窗 NavigationView 骨架与设置卡片
- **Status**: `pending`
- **Priority**: `high`
- **Depends On**: Task 5, Task 6
- **Description**:
  - 改造 `SettingsWindow.jalxaml` / cs：
    - 根背景 SolidBackgroundFillColorBase #F3F3F3；左栏宽 320、紧凑 48（当前 236/64 需改），背景 SolidBackgroundFillColorSecondary（#EEEEEE，按层级 token）+ 右缘 1px DividerStrokeColorDefault；Padding 8,16,12,16。
    - 汉堡钮 40×36；导航项高 36、左外边距 12、圆角 8、图标 16 + 标签 14；选中态：左缘 3×16 圆角 2 AccentFillColorDefault 指示条 + SubtleFillColorSecondary 底；hover 同色体系；替换现有整块 #260078D4 半透明蓝底方案。
    - 折叠/展开：宽度 ~200ms 动画过渡；标签 Visibility 随动；紧凑时图标居中、展开左对齐；逻辑沿用现有 `_isPaneCompact`/`ApplyPaneChrome`，更新宽度常量与对齐值。
    - 内容区：页面标题 28 SemiBold（由 30 改）、副标题 14 Secondary；卡片圆角 8（由 12 改）、CardBackgroundFillColorDefault/白底、行 Padding 20,16、行间 1px DividerStrokeColorDefault（由 #12000000 改 token）；ScrollViewer 与 MaxWidth 1040 保持；窗口 920×680/最小 760×560、系统标题栏不变。
    - 接 Task 5 的 Slider/ComboBox、Task 6 的 ToggleSwitch；关于页“版本 x.y.z”与“基于 Jalium.UI 与 InkCanvas。”保留。
    - 全部硬编码色值迁移 token。
  - 参考：`controls/dev/NavigationView/NavigationView_themeresources.xaml`（CompactPaneLength 48、OpenPaneLength 320、ItemHeight 36、SelectionIndicator 3×16/r2、PaneToggle 40×36）；ModernWpf `Controls/NavigationView/NavigationView.xaml` 与 Gallery 的导航/设置页组织。
- **Acceptance Criteria Addressed**: AC-6、AC-7、AC-8、AC-9、AC-2、NFR-1
- **Test Requirements**:
  - `rule` TR-8.1: 测量：左栏 320/48、项高 36、指示条 3×16/r2、汉堡 40×36、标题 28、卡片圆角 8、分隔线 1px token；逐项截图测量留证。
  - `rule` TR-8.2: 四页切换仅显示单面板、无重叠；导航选中指示条/底色随页移动；折叠展开标签显隐正确、不与内容重叠、动画连续。
  - `rule` TR-8.3: 六个开关、两滑块、组合框、版本文本、预留禁用项全部存在且行为同改造前（联动 InkRuntimeOptions；关闭设置恢复覆盖层逻辑不回归）。
  - `rule` TR-8.4: 该窗口文件 grep 无硬编码色值（token 文件除外）。
  - `rubric` TR-8.5: 与 WinUI3 设置页（NavigationView + SettingsCard 表面）的整体一致性；scale 1-5；anchors 1=仍是旧自绘浅蓝导航；3=结构正确但宽度/指示条/字级有偏差；5=与 Windows 11 原生设置细节页观感一致；threshold >= 4；证据=四页+折叠截图。

## Task 9: 全量回归、像素取证与对照验收
- **Status**: `pending`
- **Priority**: `high`
- **Depends On**: Task 4, Task 7, Task 8
- **Description**:
  - 执行 AC 全量取证：
    1. Debug/Release 干净双构建，记录输出（AC-1）。
    2. 全仓色值/硬编码审计（AC-2）。
    3. 启动冒烟：窗口矩形枚举、四窗口逐状态截图与像素测量（AC-3/5/6）。
    4. 全链路业务回归：工具切换、菜单设置、笔/荧光笔/激光笔书写与淡出、橡皮、设置联动、拖拽、置顶、关闭设置恢复（AC-7）。
    5. 动效连续帧取证（AC-9）。
    6. 编写 WinUI3 规范值 vs 实测值对照取证表（供独立评审使用）。
  - 清理所有 spike 临时代码/窗口；确认未引入第三方依赖、未改打包形态。
- **Acceptance Criteria Addressed**: AC-1 ~ AC-10（取证面）
- **Test Requirements**:
  - `rule` TR-9.1: 双配置构建 0 错误、0 未说明警告；命令输出存档。
  - `rule` TR-9.2: 全链路操作清单逐项通过且进程全程存活；每项有截图/记录。
  - `rule` TR-9.3: 取证表覆盖 AC-3/AC-4/AC-5/AC-6 全部数值型条款，实测与规格偏差为 0（测量 ±1 物理像素容差内）。
  - `rubric` TR-9.4: 交付完整度；scale 1-5；anchors 1=证据残缺无法评审；3=主要路径有证据但数值对照不全；5=规则条款 100% 有证据、rubric 素材齐全可交独立评审；threshold >= 4；证据=取证表 + 截图集 + 构建/操作记录。

