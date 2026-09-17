# WinUI3 Fluent 风格界面像素级还原 - 产品需求文档

## Overview
- **Summary**: 以 `C:\git\Jalium\microsoft-ui-xaml`（WinUI3 官方实现）为视觉与交互的唯一事实来源，以 `C:\git\Jalium\ModernWpf`（将 WinUI Fluent 适配到 WPF 风格框架的工程方法）为移植范式，用 Jalium.UI 26.10.9 的 Style / ControlTemplate / Triggers / ThemeResource / Storyboard 能力，对本项目全部界面（浮动批注栏、笔二级菜单、设置窗、全屏墨迹层）进行像素级 WinUI3 风格还原，覆盖视觉效果、交互状态、布局、动画、逻辑与样式组织。
- **Purpose**: 消除当前界面中不符合 WinUI3 规范的自绘元素（工具选中下划线、自绘开关、圆角/间距/字号偏差、无状态过渡），建立可维护的 Fluent 设计 token 层，使应用呈现与 Windows 11 原生应用一致的观感。
- **Target Users**: 揽星书写（屏幕批注/墨迹工具）的最终用户，以及后续维护 UI 的开发者。

## Goals
- 建立项目级 Fluent 设计 token 资源层（颜色、画笔、圆角、字号、间距、动画时长/缓动），命名与取值对齐 WinUI3 主题资源，组织方式借鉴 ModernWpf（`ThemeResources/Light.xaml` + 按控件拆分的样式字典 + 单一聚合入口）。
- 批注栏达到 WinUI3 浮动 CommandBar 视觉：工具按钮选中态为强调色实底 + 白色图标（替换现有底部下划线），具备 PointerOver / Pressed / Checked / Disabled 状态与过渡动画。
- 笔二级菜单达到 WinUI3 MenuFlyout + 设置卡片视觉：圆角 8 浮层、项 hover 填充、WinUI3 RadioButton / Slider 样式、弹出 pop-in 动画、内容自适应尺寸保持。
- 设置窗达到 WinUI3 设置页视觉：NavigationView 左栏（36px 项高、3×16 强调色选中指示条、subtle 选中底色、汉堡/紧凑宽度 48）、28 SemiBold 页面标题、圆角 8 白色设置卡片、WinUI3 ToggleSwitch（40×20 轨道 / 20 圆点）、WinUI3 Slider / ComboBox、卡片分隔线。
- 全部色值、圆角、字号、间距、时长取自 token，窗口业务逻辑（工具状态机、墨迹参数、置顶、设置开关联动）不回归。

## Non-Goals
- 不做深色（Dark）与高对比度（HighContrast）主题：本期仅交付 Light，但 token 层按 ModernWpf 的 Light/Dark 分字典结构组织，为后续切换预留（不实际编写 Dark 字典、不放开“跟随系统主题”开关）。
- 不启用系统 Mica / Acrylic 材质控制器：保持现有纯透明分层窗口 + 纯色/半透明纯色卡片策略（Design.MD 窗口分层原则不变）；菜单亚克力以 WinUI3 light 亚克力混合后的近似纯色表达。
- 不替换为 Jalium `NavigationView` 控件本体：维持现有手写导航结构，仅将其视觉与交互像素级对齐 WinUI3 NavigationView（避免控件模板不可控风险）。
- 不改变任何墨迹业务逻辑（采集、反射调优、激光笔淡出、笔迹重建、RTS）、窗口尺寸策略（300×88 批注栏 / 920×680 设置窗 / 全屏覆盖层）、便携与本地存储形态。
- 不做设置项持久化、不新增/删减设置项（“预留”项继续禁用）。
- 不参考 `microsoft-ui-xaml` 与 `ModernWpf` 以外的任何 UI 库（明确排除 FluentJalium、WPF UI、FluentAvalonia 等同机其他仓库）。

## Background & Context
- 项目现状（Jalium.UI 26.10.9，已可构建运行）：
  - 4 个 `.jalxaml` 窗口 + code-behind；颜色大量硬编码（`#242424`、`#5C5C5C`、`#E8E8E8`、`#260078D4` 等）；控件外观依赖 Jalium 框架默认主题。
  - 批注栏工具选中态为自绘底部 2–3px 色条，与 WinUI3 AppBarToggleButton 的 Checked=强调色实底规范不符；按钮无 PointerOver/Pressed 反馈。
  - 笔菜单卡片圆角 10、窗口默认 800×600 已通过手动 Measure 适配修复（`FitSizeToContent`，必须保留）；调色板选中环、RadioButton、Slider 均非 WinUI3 视觉。
  - 设置窗卡片圆角 12、页面标题 30、导航选中为浅蓝整块底无指示条、开关为 44×22/thumb18 自绘。
- 从 `microsoft-ui-xaml` 核实的 WinUI3 Light 主题基准（源文件均在 `controls/dev/CommonStyles/*_themeresources*.xaml`、`controls/dev/NavigationView/NavigationView_themeresources.xaml`、`dxaml/xcp/dxaml/themes/generic.xaml`）：
  - 文本：TextFillColorPrimary `#E4000000`、Secondary `#9E000000`、Tertiary `#72000000`、Disabled `#5C000000`。
  - 填充：ControlFillColorDefault `#B3FFFFFF`、Secondary `#80F9F9F9`、Tertiary `#4DF9F9F9`、Transparent `#00FFFFFF`；SubtleFillColorSecondary（hover）`#09000000`、Tertiary（pressed）`#06000000`、Transparent `#00FFFFFF`；ControlAltFillColorSecondary `#06000000`、Tertiary `#0F000000`、Quarternary `#18000000`。
  - 描边：ControlStrokeColorDefault `#0F000000`、Secondary `#29000000`；ControlStrongStrokeColorDefault `#72000000`；DividerStrokeColorDefault `#0F000000`；ControlStrongFillColorDefault `#72000000`。
  - 表面：SolidBackgroundFillColorBase `#F3F3F3`、Secondary `#EEEEEE`、Tertiary `#F9F9F9`；CardBackgroundFillColorDefault `#B3FFFFFF`。
  - 强调：AccentFillColorDefault `#0078D4`；Secondary/PointerOver、Tertiary/Pressed、Disabled（light disabled `#37000000` 为已核实值）以源文件 `Common_themeresources_any.xaml` 的 Light 段最终取值为准（标准阶梯：hover `#106EBE`、pressed `#005A9E`）；TextOnAccent Primary `#FFFFFF`。
  - 形状：ControlCornerRadius `4`、OverlayCornerRadius `8`；AppBarButton compact 40×40、Fluent2 圆角 7（实现时在 AppBarButton_themeresources 模板中复核最终值）；CommandBar `AppBarThemeMinHeight=56`。
  - ToggleSwitch：轨道 40×20、圆角 10、外描边 1px；thumb 20×20 圆点，开态位移 20px；OnFill=AccentFillColorDefault、OnKnob=白；OffFill=ControlAltFillColorSecondary、OffStroke=ControlStrongStrokeColorDefault、OffKnob=TextFillColorSecondary。
  - Slider：轨道高 4；未填充 ControlStrongFillColorDefault，已填充/thumb 强调色；thumb 18（PointerOver 20）。
  - RadioButton：外圈 20、描边 1；未选 ControlAltFillColorSecondary 底 + ControlStrongStrokeColorDefault 边；选中整圈 AccentFillColorDefault 实心 + 内部 10px 白点。
  - ComboBox：高 32、圆角 4、默认底 ControlFillColorDefault、描边 ControlStrokeColorDefault，焦点 2px 强调色。
  - MenuFlyout：Presenter 圆角 8、Padding `0,2`；项 Padding `11,8,11,9`（笔/鼠标 narrow `11,4,11,5`）、项圆角 4、hover SubtleFillColorSecondary、分隔线 DividerStrokeColorDefault 1px。
  - NavigationView（Left）：CompactPaneLength `48`、OpenPaneLength `320`（默认）；项高 `36`、图标框 16、选中指示条 3×16、圆角 2、AccentFillColorDefault；项 hover/选中底色 SubtleFillColorSecondary；汉堡按钮 40×36。
  - 字号：Title `28` SemiBold、Subtitle `20`、BodyLarge `18`、Body `14`、Caption `12`；字体 Segoe UI Variable Text（回退 Segoe UI）。
  - 动效：FastOutSlowIn 控制点 `(0.1, 0.9, 0.2, 1)`（ModernWpf `NavigationTransitionInfo.DecelerateKeySpline` 同源）；交互反馈约 120ms、按压约 100ms、浮层 pop-in 约 167–250ms（fade + 8px 上移）。
- 从 `ModernWpf` 核实的移植范式：
  - token 命名完全沿用 WinUI 原名（`ControlCornerRadius`、`TextFillColor*`、`SubtleFillColor*` 等），分 `ThemeResources/Light.xaml`、`Dark.xaml`、`HighContrast.xaml`；运行时通过 `ThemeManager` 切换合并字典；样式内引用用动态资源（Jalium 对应 `{ThemeResource ...}`）。
  - 控件样式按控件拆分为独立字典（`Styles/Button.xaml`、`Slider.xaml`、`ComboBox.xaml`、`RadioButton.xaml`、`CommandBar.xaml`、`NavigationView.xaml` 等），由聚合入口合并；模板以 Border/Grid/ContentPresenter + Trigger/MultiTrigger 表达 VisualState。
  - 缓动用自定义贝塞尔缓动类复刻 WinUI 控制点（Jalium 已内置 `TransitionTimingFunction` 与 Storyboard 动画体系，优先使用内置能力）。
- Jalium 26.10.9 能力核实：存在 `ControlTemplate`、`Setter/SetterBase`、`VisualStateManager/VisualStateHelper`、`Storyboard`、`DoubleAnimation/ColorAnimation/BrushAnimation`、`TransitionTimingFunction`、`{ThemeResource}`、`ControlTemplate.Triggers`（Trigger/MultiTrigger/Condition/Setter TargetName），框架自带主题即以此套机制实现（见 `Jalium.UI/src/managed/Jalium.UI.Controls/Themes/Controls/Button.jalxaml`）。可行性成立，但控件模板可覆盖性需在首个任务做最小 spike 验证。

## Functional Requirements

- **FR-1（设计 token 层）**：项目内提供单一聚合的 Fluent Light 主题资源字典，包含本项目用到的全部 WinUI3 颜色/画笔、圆角、字号、间距、动画时长/缓动 token；所有窗口与控件样式仅通过 token 取视觉值；窗口在应用启动时合并该字典。
- **FR-2（批注栏 CommandBar 化）**：浮动容器为圆角 8、高 56 的命令栏实体（半透明白底 + 1px token 描边）；鼠标/笔/橡皮/设置四个工具为 40×40、圆角 7、16px SymbolIcon 的 AppBar 风格按钮；具备 Resting（透明底）、PointerOver（SubtleFillColorSecondary）、Pressed（SubtleFillColorTertiary）、Checked（AccentFillColorDefault 底 + 白色图标 + 白/浅蓝图标）、Disabled 五态；Checked 不再出现底部色条；工具间 1px 竖向分隔条使用 DividerStrokeColorDefault；拖动手柄为同款 40×40 静默按钮（hover 有 subtle 反馈、无选中态）。
- **FR-3（批注栏交互与动画）**：工具单选语义、再次点击笔工具唤出/收起二级菜单、拖拽移动、置顶刷新、设置打开时收起覆盖层等既有逻辑全部保留；各视觉状态切换有 100–120ms 的颜色/缩放过渡（FastOutSlowIn），Checked 切换无跳变。
- **FR-4（笔菜单浮层）**：窗口保持透明宿主 + 内容自适应尺寸（保留 `FitSizeToContent`）；实体卡片为圆角 8、WinUI3 浮层近似底色（`#F9F9F9` 不透明或经评估的菜单 acrylic 近似）、1px DividerStrokeColorDefault 描边、内边距遵循 MenuFlyout 节奏（外边距 3→8 梯度内收敛）；出现时播放 fade + 8px 上移的 pop-in（约 200ms，FastOutSlowIn），隐藏时反向淡出（约 120ms）。
- **FR-5（笔菜单内容控件）**：9 个调色块为 20px 圆形色点，未选透明/无边，hover subtle 圆底，选中为 2px AccentFillColorDefault 圆环（环与点间留 2px 白底间隙）；笔类型三个选项使用 WinUI3 RadioButton 视觉（20 外圈、选中蓝底白点）与 14px TextFillColorPrimary 文本；粗细标题 12px TextFillColorSecondary，滑块为 WinUI3 Slider 视觉（4px 轨道、强调色已选段、18 圆点 thumb、值吸附）；色块与单选项点击具备 hover/press 反馈。
- **FR-6（设置窗 NavigationView 骨架）**：内容底色 SolidBackgroundFillColorBase `#F3F3F3`；左导航展开宽 320、紧凑宽 48，背景 SolidBackgroundFillColorSecondary/Tertiary 层级与 1px 右分隔；汉堡按钮 40×36；四个导航项高 36、左 12 外边距、圆角 8、16px SymbolIcon + 14px 文本；选中项同时具备 3×16 圆角 2 强调指示条（左缘）与 SubtleFillColorSecondary 底色，hover 同色体系；折叠/展开宽度有 ~200ms 平滑动画，标签显隐与行对齐随动；系统标题栏与窗口尺寸 920×680 / 最小 760×560 不变。
- **FR-7（设置内容区）**：页面标题 28 SemiBold TextFillColorPrimary（现为 30，需改），副标题 14 TextFillColorSecondary；设置分组为白色（CardBackgroundFillColorDefault/纯白按卡片规范）圆角 8 卡片，行内“左 14 SemiBold 标题 + 12 Secondary 说明 / 右控件”，行 Padding 20,16，行间 1px DividerStrokeColorDefault 分隔；开关一律 WinUI3 ToggleSwitch 视觉（40×20、thumb 20、On 强调底白圆点滑动 20px、Off 灰底灰描边深灰圆点），替换全部现有 44×22 自绘开关，绑定逻辑（实时采样/压力/倾斜/高对比/跟随系统预留禁用/置顶）与现状一致；默认笔宽与最小点距滑块、平滑等级 ComboBox 使用 FR-5/WinUI3 样式；关于页版本信息保留。
- **FR-8（状态反馈完整性）**：所有可交互元素（工具按钮、色块、RadioButton、Slider、ComboBox、导航项、开关）在 PointerOver/Pressed/Checked/Disabled 下均呈现 WinUI3 对应 token 的视觉且无硬编码颜色；禁用控件（跟随系统主题开关）视觉为 WinUI3 disabled 态且不可点击。
- **FR-9（墨迹层保持）**：全屏透明 InkCanvas 覆盖窗口行为与渲染不变；笔/荧光笔/激光笔颜色、粗细、BrushType、DynamicRenderer 反射适配在新样式下继续生效。

## Non-Functional Requirements
- **NFR-1（像素一致性）**：关键几何（按钮 40×40/圆角 7、轨道 40×20、导航项 36 高、指示条 3×16、卡片圆角 8、字号 28/14/12）与 WinUI3 源值偏差为 0px/0pt；颜色取 ARGB 精确值。
- **NFR-2（工程一致性）**：样式组织借鉴 ModernWpf——token 与控件样式分文件、单一聚合入口、key 命名对齐 WinUI3；窗口 `.jalxaml` 中不残留业务色值（允许模板内部引用 token）；code-behind 中集中设色的既有写法（如图标 Foreground）迁移到样式/模板。
- **NFR-3（性能）**：状态动画稳定 60fps（仅对画刷/变换/透明度做动画）；窗口冷启动到首窗可见的耗时相对当前版本无可见劣化（主观无额外卡顿；pop-in 不得阻塞输入）。
- **NFR-4（可维护性/兼容）**：Debug 与 Release 双配置 0 错误；新增警告需说明；不改动业务状态机公开行为；保持 .jalxaml + code-behind 与 SourceGenerator 编译管线。
- **NFR-5（可移植形态不变）**：不引入 AppData 写入、不改变输出/打包方式、不新增第三方 NuGet 依赖。

## Constraints
- **Technical**:
  - 仅使用 Jalium.UI 26.10.9 已验证能力：Style / ControlTemplate / ControlTemplate.Triggers（Trigger、MultiTrigger）/ ThemeResource / Storyboard / DoubleAnimation / ColorAnimation / TransitionTimingFunction；若某项 Jalium 能力（如模板内 Storyboard 触发、Brush 颜色动画）不支持，按“模板可达性优先、动画降级次之、code-behind 补视觉末位”的顺序降级，并在任务证据中记录。
  - 样式覆盖仅限应用自身资源字典作用域，不修改 Jalium 框架包文件；token key 使用 WinUI 原名，作用域在应用合并字典内（与框架自带 key 如 `ControlBackground` 不同名，避免全局污染）。
  - 固定 Light 主题（`ResourceDictionary.CurrentThemeKey = "Light"` 现状保留）。
  - 参考来源仅限 `C:\git\Jalium\microsoft-ui-xaml` 与 `C:\git\Jalium\ModernWpf`；两仓库仅作只读参考，不修改、不复制其许可证之外资产（不拷贝二进制字体/图片；Segoe Fluent Icons 经 Jalium `SymbolIcon/Symbol` 使用）。
- **Business**: 保持 Design.MD 既有约束（透明浮窗 vs 标准窗口分层、四组固定导航、预留项禁用、交互稳定性清单）。
- **Dependencies**: Jalium.UI 26.10.9 NuGet（已升级完成）；Windows 10/11、net10.0-windows、本机 SDK 10.0.303。

## Assumptions
- 设计目标 Fluent 版本为 Win11 Fluent2 基线（ControlCornerRadius 4 / Overlay 8 / AppBar 圆角 7 / ToggleSwitch 40×20），即仓库中 microsoft-ui-xaml 当前 HEAD 的 WinUI3 资源值。
- 批注栏 300×88 窗口外轮廓维持现状，CommandBar 实体在其内部按高 56 布局；拖动手柄独立芯片视觉保留但 WinUI3 化。
- 设置窗保持系统原生标题栏（不自绘 caption 按钮），标题栏按钮不纳入像素还原范围。
- 菜单浮层不追求真实亚克力噪点材质，采用 WinUI3 light acrylic 混合结果的纯色近似；如评审认为对比度不足，以 NFR-1/可读性为准微调至 token 允许范围。
- 动画时长若 WinUI 源码为区间值，取区间中值并在 token 中集中定义，便于统一调整。

## Acceptance Criteria

### AC-1: 双配置构建无错误无新增警告
- **Type**: `rule`
- **Given**: 全部 UI 改造完成后的代码
- **When**: 依次执行 `dotnet build -c Debug -t:Rebuild` 与 `dotnet build -c Release -t:Rebuild`
- **Then**: 二者均退出码 0；错误数 0；警告数不高于改造前基线（0），如有新增需逐条说明且不涉及样式资源找不到
- **Pass Condition**: 两条命令输出“0 个错误”，警告数为 0 或全部有书面说明
- **Evidence**: 两条构建命令完整尾部输出

### AC-2: 全部视觉值取自 Fluent token 层且 key 对齐 WinUI3
- **Type**: `rule`
- **Given**: 新增的主题资源字典与 4 个窗口/样式文件
- **When**: 全仓检索窗口 `.jalxaml` 与 code-behind 中的十六进制色值与硬编码圆角/字号
- **Then**: 业务视图文件中不存在直接色值（token 定义文件除外）；token key 覆盖 TextFillColor*、SubtleFillColor*、ControlFillColor*、ControlAltFillColor*、ControlStrongStrokeColor*、DividerStrokeColorDefault、SolidBackgroundFillColor*、CardBackgroundFillColorDefault、AccentFillColor*、TextOnAccent*、ControlCornerRadius、OverlayCornerRadius、字号 28/14/12，且 Light ARGB 值与本规格 Background 所列一致
- **Pass Condition**: 检索结果仅命中 token 定义文件；差异清单为空
- **Evidence**: 全仓 grep 结果 + token 文件路径行号对照表

### AC-3: 批注栏几何与 CommandBar 视觉逐像素符合规范
- **Type**: `rule`
- **Given**: 应用启动后显示批注栏（175% 缩放、2560×1600 环境）
- **When**: 枚举窗口矩形并对批注栏区域截图，测量容器与按钮几何
- **Then**: 窗口仍为 300×88 DIP、位置 120,120；容器高 56、圆角 8；四个工具按钮均为 40×40、圆角 7、图标 16px；分隔条 1×26；手柄芯片 40 宽/56 高区域内；Checked 工具无底部色条
- **Pass Condition**: 各项测量值与规格完全一致（允许 ±1 物理像素测量误差）
- **Evidence**: 窗口枚举矩形 + 截图像素测量记录

### AC-4: 工具按钮五态视觉与切换动画正确
- **Type**: `rule`
- **Given**: 批注栏可见
- **When**: 依次对每个工具按钮执行：静置、悬停、按下、选中、（如可用）禁用观察
- **Then**: Resting 透明底；PointerOver 为 `#09000000` 填充；Pressed 为 `#06000000`；Checked 为 `#0078D4` 底 + 白色图标；状态间为 ~120ms FastOutSlowIn 过渡；单选互斥逻辑保持（任一时刻仅一个工具 Checked；鼠标态无 Checked 实底时对应“鼠标”工具按其初始语义——以现有 MouseTool IsChecked=true 初始态也必须显示 Checked 实底）
- **Pass Condition**: 五态颜色与 token 一致且录屏/连续截图可观察到过渡而非瞬切
- **Evidence**: 各状态截图（含色值采样）与交互操作记录

### AC-5: 笔二级菜单为 WinUI3 浮层并保持既有业务行为
- **Type**: `rule`
- **Given**: 笔工具 Checked，再次点击笔工具
- **When**: 菜单显示/隐藏循环、改颜色、改粗细、切换笔类型
- **Then**: 窗口紧密包裹内容（无 800×600 空白）；卡片圆角 8、描边 1px token；pop-in 为 fade+8px 上移约 200ms；调色块 20px、选中 2px 蓝环；RadioButton 为蓝底白点 WinUI3 样式；Slider 为 4px 轨道+18 thumb；颜色/粗细/笔类型变更实时作用于覆盖层（上一轮已验证的红色笔迹链路不回归）；菜单位置仍相对批注栏左下偏移
- **Pass Condition**: 几何/动画/控件形态全部满足，且选项变更后进入墨迹模式绘制的笔迹采用所选颜色/粗细/笔种
- **Evidence**: 弹出过程连续截图 + 选项操作后绘制笔迹截图

### AC-6: 设置窗 NavigationView 骨架与内容区符合规范
- **Type**: `rule`
- **Given**: 点击设置按钮打开设置窗
- **When**: 测量导航与内容区，切换四个页面，折叠/展开导航
- **Then**: 窗 920×680；左栏展开 320 / 紧凑 48；导航项高 36、指示条 3×16 圆角 2 居左、选中浅黑底（SubtleFillColorSecondary `#09000000`）；页面标题 28 SemiBold；卡片圆角 8、白底、行 Padding 20,16、分隔线 1px token；六个开关均为 40×20 WinUI3 ToggleSwitch 且 On 时 `#0078D4` 轨道 + 白圆点位移 20px；滑块/组合框为 WinUI3 形态；“跟随系统主题”开关保持禁用态；折叠/展开有 ~200ms 宽度动画且标签显隐正确、不与内容重叠
- **Pass Condition**: 全部几何与状态项逐项测量通过；四页切换无多面板重叠/异常
- **Evidence**: 四页截图 + 折叠前后截图 + 像素测量记录

### AC-7: 业务逻辑零回归
- **Type**: `rule`
- **Given**: 改造后版本
- **When**: 完成全链路操作：启动→鼠标/笔/橡皮切换→笔菜单设置→覆盖层书写（笔/荧光笔/激光笔）→设置窗开关与滑块/组合框联动→关闭设置恢复覆盖层→拖拽批注栏→关闭应用
- **Then**: 进程全程存活无崩溃；InkRuntimeOptions 联动（RTS/压力/倾斜/最小点距/平滑等级）生效；橡皮 EraseByStroke 生效；激光笔笔迹自动淡出保留；置顶/Z 序刷新行为保持；关闭设置窗后批注栏状态机恢复
- **Pass Condition**: 操作清单全部通过，进程无未处理异常退出
- **Evidence**: 操作记录 + 进程存活检查 + 关键笔迹/UI 状态截图

### AC-8: 与 WinUI3 参考的整体视觉保真度
- **Type**: `rubric`
- **Dimension**: 界面与 WinUI3 规范（microsoft-ui-xaml 资源值 + Windows 11 原生 CommandBar/MenuFlyout/Settings 观感）的整体一致性
- **Scale**: 1-5
- **Anchors**: 1 = 仍为当前自绘风格或出现明显非 WinUI 元素（色条选中、自绘开关、错误圆角）；3 = 主要控件形态正确但间距/字重/状态反馈/材质有可见偏差；5 = 不借助代码无法区分截图与 WinUI3 原生对应表面（颜色、圆角、密度、图标、状态反馈全部一致）
- **Pass Threshold**: >= 4
- **Evidence**: 四个窗口各状态截图与 WinUI3 规范值/参考观感的对比评审记录

### AC-9: 动效与交互质感
- **Type**: `rubric`
- **Dimension**: 状态过渡与浮层动画的时机、缓动、连贯性
- **Scale**: 1-5
- **Anchors**: 1 = 全部瞬切或动画卡顿/错位；3 = 有过渡但时机/缓动不统一或轻微闪烁；5 = hover/press/check 100–120ms 统一 FastOutSlowIn，菜单 pop-in 与导航收展自然、无丢帧/无输入阻塞
- **Pass Threshold**: >= 4
- **Evidence**: 交互录屏或连续帧截图 + 时长测量

### AC-10: 样式工程组织的可维护性
- **Type**: `rubric`
- **Dimension**: 资源分层、key 命名、复用度对 ModernWpf 范式的贴合程度
- **Scale**: 1-5
- **Anchors**: 1 = 样式散落在各窗口、硬编码依旧；3 = 有 token 文件但控件样式未拆分或存在重复定义；5 = token/控件样式/聚合入口分层清晰、命名对齐 WinUI3、四窗口共享复用、新增控件仅需引用 token
- **Pass Threshold**: >= 4
- **Evidence**: 主题目录结构与样式文件内容评审

## Open Questions
- [ ] AppBarButton 在 Win11 Fluent2 下的圆角最终值以实现阶段对 `AppBarButton_themeresources.xaml` 模板的复核为准（规格基准 7px）；若模板为其他值，以源值为准并在证据中记录。
- [ ] AccentFillColorSecondary/Tertiary 的 Light 精确值实现时以 `Common_themeresources_any.xaml` Light 段取值为准（标准阶梯 `#106EBE`/`#005A9E`）。
- [ ] 菜单卡片底色采用 `#F9F9F9` 纯色还是白色半透明叠加，在 Task 1 spike 后依据 Jalium 透明分层窗口的实际合成效果确定（必须满足文字对比度与浮层层次）。
- [ ] 若 Jalium ControlTemplate 覆盖内置 AppBarButton/Slider/ComboBox 存在阻断，降级边界（自绘控件 vs 保留内置仅调色）在 spike 任务结论中固化，并经本规格评审后执行。
