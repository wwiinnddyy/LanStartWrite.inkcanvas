# Jalium.UI 依赖升级（26.10.2 → 26.10.9）实施计划

## Repository Research（仓库调研结论）

### 项目构成
- 解决方案 `LanStartWrite.Inkcanvas.slnx` 仅含主项目 [LanStartWrite.Inkcanvas.csproj](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj)：`net10.0-windows` / WinExe，唯一 NuGet 依赖为元包 `Jalium.UI 26.10.2`。
- 另有未纳入解决方案的开发小工具 [tools/IconDump/IconDump.csproj](file:///c:/git/ink/LanStartWrite.inkcanvas/tools/IconDump/IconDump.csproj)：`net10.0`，引用 `FluentIcons.Common/WinUI/Wpf 2.1.325`（[Program.cs](file:///c:/git/ink/LanStartWrite.inkcanvas/tools/IconDump/Program.cs) 仅用 Common 查图标码位）。
- 本机 SDK：`10.0.303`，满足 net10.0 构建；无 `global.json` / `nuget.config` / `Directory.Build.props`。
- 基线实测（26.10.2）：`dotnet build` Debug **0 警告 0 错误**；exe 启动后进程存活、主窗口句柄正常创建。

### 代码使用的 Jalium.UI API 面
- 启动：`RenderContext.GetOrCreateCurrent(RenderBackend.Auto)`、`RenderingEngine.Impeller`、`ResourceDictionary.CurrentThemeKey="Light"`、`Application.Run()`（[Program.cs](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/Program.cs)）。
- 窗口：4 个 `.jalxaml` + code-behind（批注栏、全屏 InkCanvas 覆盖层、笔二级菜单、设置窗），大量透明窗口/置顶/`WindowBackdropType.None`。
- Ink：`InkCanvas`、`DrawingAttributes`（BrushType/StylusTip/FitToCurve/IgnorePressure/IsHighlighter）、`Stroke`、`StylusPointCollection`、`DynamicRenderer`、`InkCanvasEditingMode`、Pointer intermediate points。
- 反射兼容代码：[InkCanvasTuning.cs](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/InkCanvasTuning.cs)（MinPointDistance 字段）、实时喂点方法探测、XTilt/YTilt 探测——均设计为静默降级。
- 4 个 `.jalxaml` 根节点使用旧命名空间 `xmlns="http://schemas.jalium.ui/2024"`。
- csproj 含自定义目标 `InkcanvasAnnotationToolbarSkipUicEmbed`（`AfterTargets="EmbedCompiledJalxaml"`），剥离 4 个 `.uic` 嵌入资源，强制走「嵌入 .jalxaml + XamlReader」路线，规避旧版 .uic 路线丢失 `x:Name` 的问题。

### 26.10.2 → 26.10.9 间与本项目相关的变更（GitHub Releases 核实）
1. **26.10.5 行为变更（关键）**：消费侧 `EnableJalxamlCodeGeneration` 默认值 `true → false`，即默认改用 SourceGenerator 路径，官方正是用它修复「发布后开窗无响应/`x:Name` 不接线导致 NRE」。本项目那个剥离 `.uic` 的变通目标在新默认下大概率变成空操作，需要在升级后核对生成代码再决定保留或删除。
2. **26.10.4**：ScrollViewer 触控 `PanningMode` 默认 `VerticalFirst`（设置窗含 ScrollViewer，属正向变化）；RealTimeStylus 改为独立线程（本项目未写 StylusPlugIn，反射喂点为防御式代码）。
3. **26.10.5**：InkCanvas 新增后台线程实时笔迹预览插件；若框架新出现名为 `AddPoints/AppendPoints/FeedPoints/UpdateDrawing` 且参数为 StylusPointCollection 的公共方法，本项目反射喂点可能由「从不生效」变为生效，需观察是否与内置预览重复出墨。
4. **26.10.6**：`Path` 默认 `Stretch.None`（本项目标记中无 Path，无影响）。
5. **26.10.8**：Grid/StackPanel/ScrollViewer/Popup/Window 布局与多窗口主题行为大修——需要运行时目检窗口布局。
6. **26.10.9**：Popup 边缘定位修正、Slider 分段轨道外观、窗口类注册真实图标；均为外观/行为正向修复。
7. 元包仍存在且推全 14 包，`Jalium.UI`（net10.0 元包）引用方式不变；26.10.3→.4 的 `Jalium.Extensions.*` 命名空间回退与本项目无关（未用 Hosting/DI）。

## Files and Modules（拟改动文件）
- [src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/LanStartWrite.Inkcanvas.csproj)：`Jalium.UI` 版本 26.10.2 → 26.10.9；按生成代码核对结果处理 `InkcanvasAnnotationToolbarSkipUicEmbed` 目标（删除或保留并更新注释）。
- 4 个 `.jalxaml`（[AnnotationToolbarWindow](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/AnnotationToolbarWindow.jalxaml)、[AnnotationOverlayWindow](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/AnnotationOverlayWindow.jalxaml)、[PenSecondaryMenuWindow](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/PenSecondaryMenuWindow.jalxaml)、[SettingsWindow](file:///c:/git/ink/LanStartWrite.inkcanvas/src/LanStartWrite.Inkcanvas/SettingsWindow.jalxaml)）：**仅当** 26.10.9 解析器拒绝旧 xmlns `http://schemas.jalium.ui/2024` 时，迁移为 `https://schemas.jalium.dev/jalxaml/presentation`；否则不动。
- C# 源码（Program.cs / 各窗口 code-behind / InkCanvasTuning.cs 等）：仅当构建报 API 不兼容时做最小适配，不改功能。
- [tools/IconDump/IconDump.csproj](file:///c:/git/ink/LanStartWrite.inkcanvas/tools/IconDump/IconDump.csproj)：3 个 FluentIcons 包 2.1.325 → 2.1.341。

## Implementation Steps（按依赖顺序）

1. **升级主项目包版本**：csproj 中 `Jalium.UI` 改为 `26.10.9`，执行 `dotnet restore`。
2. **首次构建与错误收敛**：`dotnet build -c Debug`；若有 API/标记编译错误，逐个做最小适配（重点观察 Interop 的 RenderContext API、Ink API、旧 xmlns、MSBuild 目标告警）。
3. **核对 JALXAML 生成管线**：检查 `obj` 下生成的 `.g.cs` 与资源清单，确认：
   - 新默认（SourceGenerator 路径）下 `x:Name` 字段由生成代码正确接线；
   - `.jalxaml` 仍作为嵌入资源存在且 InitializeComponent 可加载；
   - `EmbedCompiledJalxaml` 目标是否仍存在/产出 `.uic`。
   据此决定：变通目标已无作用则**删除**（含其上方注释）；仍需要则保留。
4. **xmlns 兼容性判定**：构建+运行均正常则保留旧 xmlns；若解析失败，把 4 个 `.jalxaml` 根 xmlns 统一迁移到 `https://schemas.jalium.dev/jalxaml/presentation` 后重新构建。
5. **Release 构建**：`dotnet build -c Release`（该配置 DebugType=none，需确认同样通过）。
6. **运行时冒烟验证**（见 Validation）。
7. **升级工具项目**：IconDump 三个 FluentIcons 包 → 2.1.341，`dotnet build` 验证；不加入解决方案（维持现状）。
8. 汇总结果；不主动提交 git（除非用户另行要求）。

## Dependencies and Considerations（依赖与注意事项）
- 版本选择：NuGet.org 当前最新稳定版即 **26.10.9**（全 14 包同日发布，版本一致），不使用预发布。
- 不升级目标框架（保持 net10.0-windows）、不引入 `ThemeLoader.Initialize()`（当前 RenderContext 预热启动模式在 26.10.2 已验证可用，仅当新版运行时报主题/解析初始化错误时再按官方启动序修正）。
- 反射式代码（MinPointDistance、喂点探测、倾斜探测）坚持「静默降级」语义，不主动改写字面量或方法名匹配；如新版 InkCanvas 自带实时预览导致双重笔迹，优先让反射路径在检测到内置插件时短路，而不是改框架行为。
- 用户偏好：纯客户端、便携、单文件——本次只动版本，不改变输出/打包形态。

## Validation（实施后验证）
1. `dotnet build` Debug 与 Release 均 0 错误（警告数与基线对比，新增警告需说明）。
2. **启动冒烟**：启动 Debug exe，等待约 6 秒，断言进程未退出且 `MainWindowHandle != 0`，随后关闭进程。
3. **界面目检（截图）**：启动后截屏确认批注栏正常渲染（白底圆角、4 个工具图标、拖动手柄）；如可行再用桌面自动化点开：笔工具→全屏墨迹层出现、笔工具再点→二级菜单、设置按钮→设置窗四页切换与滑块/组合框，确认无空白窗口、无异常弹错。
4. 交互正常后关闭进程；观察 Debug 输出中 `[InkCanvasTuning]` / `[ink]` 诊断有无持续异常信息。
5. IconDump 构建通过即可。

## Risks（风险与应对）
- **风险 1：SourceGenerator 默认路径下 InitializeComponent / x:Name 行为与现有变通目标冲突** → 以 obj 生成代码和实际开窗结果为准；冲突则删除自定义剥离目标（官方新默认本就是为修复 x:Name 问题）。
- **风险 2：旧 xmlns 在新版被移除** → 构建/运行立即暴露；按步骤 4 机械迁移 4 个根节点命名空间。
- **风险 3：反射实时喂点在新版意外命中新 API，与内置实时预览重复出墨** → 冒烟时重点试写笔迹；若重复，调整 `TryFeedRealtimePoints` 增加内置预览能力检测并短路。
- **风险 4：26.10.8 布局大修导致窗口尺寸/置顶/透明层行为细微回归** → 截图目检四个窗口；仅在出现实际回归时做针对性最小修正，不做预防性重构。
- **回退**：全部改动集中在 csproj 版本号、（可能的）MSBuild 目标删除、（可能的）xmlns 迁移；如 26.10.9 出现无法快速解决的阻断，直接还原上述改动即可回到已验证的 26.10.2 基线。
