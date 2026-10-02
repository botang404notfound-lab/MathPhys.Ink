# 数理墨 · 设计方案 01
## PDF 导入 + 无限画布 + 笔迹随画布变换

> 状态：**待确认**（确认后再进入编码）
> 基线：Ink-Canvas（WXRIW/Ink-Canvas，GPL-3.0）
> 目标机：希沃一体机（Windows x64，Windows Ink 手写笔）

---

## 0. 需求复述

在希沃一体机上做一个**面向 PDF 试卷讲评的批注白板**，具体为：

1. **不重写墨迹引擎** —— 复用 WPF / Windows Ink 既有墨迹栈（`InkCanvas` + `Stroke` + `StylusDevice`），以及 Ink-Canvas 中已验证的希沃适配代码。
2. **PDF 导入并渲染** —— 打开试卷 PDF，按页显示在画布上，作为批注底图。
3. **无限画布** —— 可缩放、可平移，画面不受屏幕/单页边界限制，可把某一题放大到整屏讲。
4. **笔迹随画布变换** —— 缩放、平移画布时，已写的板书必须与 PDF 内容**严格同步位移与缩放**，不漂移、不脱层。
5. **后续插件式扩展** —— 为角度尺、量角器、函数图像、受力分析等学科工具预留插件契约。
6. **工程约束** —— 每次只交付一个可验证的小功能；先方案后编码；代码可编译；关键处中文注释。

---

## 1. 本机环境实测结果（重要：先看这条）

我在本机（`C:\Users\9800X3D`）实测了构建环境：

| 项目 | 实测结果 | 影响 |
|---|---|---|
| .NET 运行时 | **6.0.36**（`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App` 均在） | 只能**运行**，不能构建 |
| .NET SDK | `C:\Program Files\dotnet\sdk` **为空** | `dotnet build` 不可用 ❌ |
| Visual Studio / Build Tools | **未安装**（`C:\Program Files\Microsoft Visual Studio`、`(x86)` 均不存在） | 无 IDE、无 MSBuild |
| .NET Framework 4.8 引用程序集 | **不存在**（`Reference Assemblies\...\.NETFramework` 为空） | 无法编译 .NET Framework 目标 ❌ |
| NuGet 全局缓存 | `~/.nuget/packages` **不存在** | 首次还原需联网下载全部包 |
| dotnet.exe | 存在于 `C:\Program Files\dotnet\dotnet.exe` | 只是宿主，不带 SDK |

**结论：目前这台机器一行 C# 也编不出来。**
所以在写第一个功能之前，必须先补工具链（见 §7 的 M0）。这一步需要你点头，因为要装东西。

---

## 2. 不确定点（需要你拍板）

| 编号 | 问题 | 选项 | 我的建议 |
|---|---|---|---|
| **D1** | **是否真的 fork Ink-Canvas？** | A. 整仓 fork，在它上面改<br>B. 只**移植**它的可复用模块，主工程新建 | **B**。原因：Ink-Canvas 是"**屏幕批注悬浮层**"（在桌面/PPT 上盖一层透明窗口），不是文档画布应用；它的 `MainWindow.xaml.cs` 有 **329 KB**，是单体泥球；且带 UWP `wapproj` 打包链路，与我们要的"PDF 文档画布"目标形态差异很大。移植它的**经验证模块**（多点触控、全局热键、希沃笔尾橡皮识别、主题字典）性价比最高。 |
| **D2** | **目标框架** | A. .NET Framework 4.8（与 Ink-Canvas 一致）<br>B. .NET 8 / WPF | 取决于 D3。若要**直接复用** Ink-Canvas 的笔尾橡皮识别（依赖 `Microsoft.Ink.dll` + `IACore.dll` / `IAWinFX.dll`），**只能选 A**，因为 IA 系列程序集是 Framework-only，.NET 8 下加载不了。若选 B，笔尾橡皮要用退化方案（见 §5.4）。**本机目前两种都编不了**，都要先装 SDK。 |
| **D3** | **PDF 组件形态** | A. 只用 `PdfDocument` **离屏渲染**成位图<br>B. 用 `PdfRenderer` WinForms 控件 + `WindowsFormsHost` | **A，无悬念**。WinForms 层是独立 HWND 空中层，**不参与 WPF 的 Matrix 变换**，一旦缩放就会和笔迹层错位撕裂。只有 A 才能让 PDF 与笔迹走同一条变换管线、永远对齐。见 §5.2。 |
| **D4** | **缩放时笔迹粗细** | A. 跟随缩放（放大板书也变粗，同 OneNote）<br>B. 视觉粗细恒定 | **A**。符合教师"放大局部讲"的直觉，且零额外成本。B 需要自绘渲染层，首版不做。 |
| **D5** | **试卷页面布局** | A. 单页模式<br>B. 连续纵向滚动<br>C. 讲义式拼贴（2 页对照） | **B 为主 + 可切 A**。讲评试卷常要"上一题回看"，连续滚动最顺；C 留到后面。 |
| **D6** | **触摸/笔分工** | 单指 = 写字还是平移？ | 建议：**笔 = 写，单指 = 平移/翻页，双指 = 缩放旋转，手掌/粗触点 = 擦除**。希沃红外屏误触多，这条必须定死，否则上课会乱画。 |
| **D7** | **首个可验证功能的范围** | 见 §7 的 M1 | 建议 M1 = "打开 PDF → 按 world 坐标摆页 → fit-width 显示"，**先不接笔迹**，把底图和坐标系跑通。 |
| **D8** | **保存/导出** | 是否需要保存批注、导出带批注的 PDF？ | 建议首版只做保存/加载工程文件，导出 PDF 放 M6。 |

---

## 3. 核心设计决策（附理由）

### 3.1 坐标系：世界坐标 / 视口坐标分离

- **世界坐标（World）**：一张"无限大的纸"。单位取 **PDF point（1/72 inch）**，与 PDF 原生单位一致，避免换算误差。
- **视口坐标（Viewport）**：屏幕像素（DIP）。
- 视口矩阵：`WorldToViewport = Scale(z) × Translate(offsetX, offsetY)`，反向即 `Inverse`。
- **所有持久化数据（页面位置、笔迹）一律存世界坐标**，与缩放平移无关。这样打开文件时的显示缩放可以随意变，内容永不错位。

### 3.2 图层栈：把变换放在"世界层"这一层做

```
CanvasViewport (Grid, ClipToBounds=True)      ← 视口，固定为窗体大小
└─ WorldHost (FrameworkElement)
     RenderTransform = 视口矩阵                ← 唯一的缩放/平移入口
   ├─ PdfPageLayer   (Canvas)                  ← 每页一个 Image，位置=世界坐标
   ├─ AnnotationLayer(Canvas)                  ← 预留：文字/图形批注
   └─ InkLayer       (InkCanvas)               ← 笔迹层
```

**为什么这样摆就天然满足"笔迹随画布变换"**：
WPF 的输入系统会把触控/笔的坐标**自动经过 RenderTransform 的逆矩阵**再交给 `InkCanvas`。因此：
- 采集到的笔迹点 = 世界坐标；
- 渲染时再经过同一个正向矩阵 ⇒ 笔迹与 PDF 永远同层同变换，**零自定义矩阵数学**，不会漂移。

这正是"不重写墨迹引擎"的最省力落点。

### 3.3 该方案的三个已知代价（必须有对策）

| 代价 | 说明 | 对策 |
|---|---|---|
| 元素尺寸不能是无穷 | `InkCanvas` / `Canvas` 需要具体尺寸，WPF 布局尺寸有上限 | 设定**软边界**（world 坐标约 ±50,000 DIP），随内容增长动态扩尺寸；越界做钳制 |
| 离屏页仍参与布局 | 页面多时会拖慢 | 首版试卷 4~8 页可忽略；**把非视口页的 `Image.Source` 置空**做页面级虚拟化；上百页再升级渲染层 |
| 笔迹粗细随缩放 | 即 D4 的 A 方案 | 教师端可接受；如需恒定粗细，后续换自绘层 |

### 3.4 对比：为什么不用 WinForms 承载 PDF（D3 的展开）

`WindowsFormsHost` 里的 `PdfRenderer` 是一个**独立原生子窗口**，它悬浮在 WPF 渲染树之外：
- 不参与 WPF 矩阵变换 → 缩放时 PDF 与笔迹错层；
- 触控事件要跨层转发，希沃上延迟明显；
- DPI 缩放与空中层 z-order 问题多。

所以 PdfiumViewer 我们**只取 `PdfDocument` 当渲染器用**（它本身是纯计算类，不依赖 UI）：
`PdfDocument.Load()` → `PageCount` / `PageSizes` → `Render(page, w, h, dpiX, dpiY, false)` 得到位图 → 转成 `BitmapSource` 交给 WPF `Image`。
这样 PDF 与墨迹在同一变换管线，永远对齐。

### 3.5 PDF 清晰度与内存策略（关键工程点）

PDF 是矢量的，放大到 400% 时若还用 100% 的位图就会糊。策略：

- **缩放分档**：按 zoom 档位（0.25 / 0.5 / 1 / 2 / 4 / 8）预渲染位图；
- **交互中**用最接近档位的已有位图**拉伸过渡**，保证跟手不卡；
- **停止操作 200 ms 后**再按当前 zoom 精确重渲目标页（防抖）；
- **单渲染线程 + 队列**：`PdfDocument` **非线程安全**，绝不能并发渲染；
- **LRU 缓存 + 只渲染视口页**。

内存估算（务必重视）：A4 页面 150 dpi ≈ 1240×1754 ≈ **8.7 MB/页**（BGRA32）；300 dpi 是 4 倍 ≈ 35 MB/页。8 页 @300dpi 已近 280 MB，一体机内存吃紧。
⇒ 建议缓存上限 **512 MB**，并优先保证"当前视口页 + 相邻页"的分辨率。

### 3.6 笔迹序列化：复用 WPF 内置格式

`StrokeCollection.Save(Stream)` / `Load()` 支持微软 **ISF** 格式，开箱即用，含压感与 `DrawingAttributes`。
因为我们把笔迹存在世界坐标，**存盘时不需要任何坐标补偿**。
（ISF 是 WPF 内置的二进制墨迹格式；如需跨端/可读，后续再加一层 JSON 导出。）

### 3.7 撤销/重做

基于命令栈，而不是快照整个 `StrokeCollection`：
- 命令类型：`AddStroke` / `EraseStroke` / `EraseRange` / `Clear` / `AddPage`；
- 每个命令自带 `Undo()` / `Redo()`；
- 栈深上限（如 200）防止长时间上课内存膨胀。

---

## 4. 类结构与职责

命名空间根：`MathPhys.Ink`

### 4.1 Canvas（无限画布核心）

| 类 | 职责 |
|---|---|
| `CanvasViewport` | 视口唯一真值源：`Scale`、`Offset`、`WorldToViewport` 矩阵；提供 `ToWorld(pt)` / `ToViewport(pt)`；`ZoomAt(viewPt, factor)`；`FitWidth(pageIndex)`；变更时发 `ViewportChanged` |
| `WorldLayout` | 页面 → 世界坐标的排版计算：页间距、单页/连续模式、整体包围盒 `WorldBounds` |
| `PageVisualHost` | 单页宿主：持有 `Image`、管理该页渲染生命周期、虚拟化（离屏置空） |
| `InputRouter` | 输入分类与路由：笔/单指/双指/滚轮 → 决定走"书写 / 平移 / 缩放 / 擦除"；对接 `MultiTouchInput` |
| `CanvasBoundsGuard` | 软边界钳制与 `InkCanvas` 尺寸自适应增长 |

### 4.2 Pdf（PDF 子系统）

| 类 | 职责 |
|---|---|
| `IPdfDocumentService` | 抽象：`Open`、`PageCount`、`GetPageSize`、`RenderPageAsync`、`Dispose`。为将来换解析器留口子 |
| `PdfiumDocumentService` | PdfiumViewer 实现：`PdfDocument.Load` + `Render` |
| `PageRenderCache` | LRU 缓存 + 缩放分档 + 防抖重渲 + 单线程队列 |
| `BitmapInterop` | `System.Drawing.Bitmap` → `BitmapSource`（BGRA32，`Freeze()` 后可跨线程） |

### 4.3 Ink（墨迹）

| 类 | 职责 |
|---|---|
| `InkSurface` | 封装 `InkCanvas`：图层挂载、`DrawingAttributes`（颜色/粗细/压感开关）、`EditingMode` 切换、范围擦除 |
| `StrokeHistory` | 撤销/重做命令栈 |
| `InkSerializer` | ISF 存取；世界坐标保证无补偿 |
| `PenProfile` | 笔型配置（钢笔/荧光笔/红笔/橡皮），供 `Tools` 复用 |

### 4.4 Tools（插件式，后续学科工具的落点）

| 类 | 职责 |
|---|---|
| `ITool` | 工具契约：`Activate(ToolContext)` / `Deactivate()` / `OnPointerEvent(...)` / `GetVisual()` |
| `ToolContext` | 工具拿到的能力包：视口、墨迹层、当前页、资源释放钩子 |
| `ToolHost` | 工具注册表 + 当前工具切换 + 工具浮层挂载点 |
| `ITeachingWidget` | **学科工具插件契约**（角度尺、量角器、函数图、受力分析等的统一入口），首版只定义接口不实现 |

### 4.5 ViewModels / Views

| 类 | 职责 |
|---|---|
| `MainViewModel` | 组合根：文档、视口、工具、命令（打开/保存/翻页/缩放） |
| `DocumentViewModel` | 文档状态、页列表、当前页 |
| `ToolViewModel` | 当前工具、笔型、粗细、颜色 |
| `MainWindow.xaml` | 主窗口：顶部工具栏 + 中央画布 + 左侧页面缩略图 |
| `CanvasViewportHost.xaml` | 画布宿主控件（图层栈 XAML 在这里） |
| `PageThumbnailPanel.xaml` | 页面缩略图导航 |

### 4.6 Storage / Infrastructure

| 类 | 职责 |
|---|---|
| `BoardPackage` | `.wbx` 工程文件读写（zip：`doc.pdf` + `ink.isf` + `meta.json` + `pages.json`） |
| `RecentFiles` | 最近打开 |
| `HotkeyManager` | 全局热键（**移植自 Ink-Canvas `Helpers/Hotkey.cs`**） |
| `MultiTouchInput` | 多点触控原始输入（**移植自 Ink-Canvas `Helpers/MultiTouchInput.cs`**） |
| `SeewoPenAdapter` | 希沃笔尾橡皮/压感适配（**移植自 Ink-Canvas 的 `Microsoft.Ink` 用法**） |
| `AppSettings` | 设置持久化（参考 Ink-Canvas `Settings.cs`，但重写为轻量 JSON） |
| `Logging` | 简易日志（便于一体机上排障） |

---

## 5. 关键技术点补充

### 5.1 视口操作习惯（拟）

| 操作 | 行为 |
|---|---|
| `Ctrl` + 滚轮 | 以光标为锚点缩放（锚点不跑，这是顺手的核心） |
| `空格` + 左键拖 / 中键拖 | 平移 |
| 触摸双指 | 捏合缩放 + 平移（旋转可选） |
| 触摸单指 | 平移（可配置为擦除） |
| 笔 | 书写 |
| 笔尾 / 手掌 | 擦除 |

### 5.2 缩放锚点为什么要用光标

若以窗口中心缩放，教师"想看清某道题"时要缩放后再手动拖回来，很别扭。以光标为锚点时，公式上表现为对 `Offset` 做一次补偿（`newOffset = p - (p - oldOffset) * k`），交互体感立刻变"跟手"。这是一体机上最容易感知的体验差异点，首版就做对。

### 5.3 为什么"先不接笔迹"（M1 的取舍）

底图坐标系是后面所有功能的地基。如果 M1 就把 InkCanvas 接上，一旦换算有偏差，你会看到"笔迹和题干错位"这种症状，但它可能来自渲染、布局、变换三层中任意一层，排查成本高。
先把"PDF 页 → world → 视口"这条链单独验证到**缩放平移都精准**，再接墨迹，问题空间就小得多。

### 5.4 希沃笔尾橡皮的现实风险（D2 的后果）

部分数位屏上，**笔尾（eraser tip）不会触发 WPF 的 `StylusDown`** —— 这正是 Ink-Canvas 要引入 `Microsoft.Ink.dll`（WISP 栈）读取 `PacketStatus.EraserMode` / 压感的原因。
- 若 D2 选 **.NET Framework 4.8**：可原样移植 Ink-Canvas 的做法，笔尾橡皮体验最接近原生。
- 若 D2 选 **.NET 8**：`IAWinFX` / `IACore` 无法加载，只能退化 —— 用设置项"笔尾当橡皮/用粗头当橡皮"手动开关（Ink-Canvas 在 `SettingsPage` 里也有类似兜底开关）。

这条直接决定一体机上的手感，建议以"能不能拿到希沃板子实测"为准来定 D2。

---

## 6. 文件改动清单

### 6.1 新增（新工程，主交付物）

```
数理墨.sln
src/MathPhys.Ink/
├─ MathPhys.Ink.csproj
├─ App.xaml / App.xaml.cs                        程序入口、异常兜底、渲染模式
├─ Views/
│   ├─ MainWindow.xaml / .xaml.cs                主窗口骨架
│   ├─ Controls/CanvasViewportHost.xaml / .cs    图层栈宿主（§3.2 的结构）
│   └─ Controls/PageThumbnailPanel.xaml / .cs    页面缩略图
├─ ViewModels/
│   ├─ MainViewModel.cs
│   ├─ DocumentViewModel.cs
│   └─ ToolViewModel.cs
├─ Canvas/
│   ├─ CanvasViewport.cs                         视口矩阵与坐标互转
│   ├─ WorldLayout.cs                            页面世界坐标排版
│   ├─ PageVisualHost.cs                         单页宿主 + 虚拟化
│   ├─ InputRouter.cs                            输入分类路由
│   └─ CanvasBoundsGuard.cs                      软边界与尺寸自适应
├─ Pdf/
│   ├─ IPdfDocumentService.cs
│   ├─ PdfiumDocumentService.cs                  ★ 唯一引用 PdfiumViewer 的地方
│   ├─ PageRenderCache.cs                        LRU + 分档 + 防抖 + 单线程
│   └─ BitmapInterop.cs                          Bitmap → BitmapSource
├─ Ink/
│   ├─ InkSurface.cs
│   ├─ StrokeHistory.cs
│   ├─ InkSerializer.cs                          ISF 存取
│   └─ PenProfile.cs
├─ Tools/
│   ├─ ITool.cs / ToolContext.cs / ToolHost.cs
│   └─ ITeachingWidget.cs                        学科工具插件契约（仅接口）
├─ Storage/
│   ├─ BoardPackage.cs                           .wbx 读写
│   └─ RecentFiles.cs
├─ Infrastructure/
│   ├─ HotkeyManager.cs                          ← 移植
│   ├─ MultiTouchInput.cs                        ← 移植
│   ├─ SeewoPenAdapter.cs                        ← 移植
│   ├─ AppSettings.cs
│   └─ Logging.cs
└─ Resources/
    ├─ Styles/Light.xaml / Dark.xaml             ← 移植并精简
    └─ Icons/                                    笔/橡皮/缩放等图标
docs/
├─ 01-设计方案-PDF导入与无限画布.md               本文件
└─ 02-开发环境搭建.md                             M0 产出
```

### 6.2 从 Ink-Canvas 移植（只取模块，不搬主窗口）

| 来源文件（Ink-Canvas） | 目标 | 处理方式 | 说明 |
|---|---|---|---|
| `Helpers/MultiTouchInput.cs` | `Infrastructure/MultiTouchInput.cs` | 基本照搬 | 多点触控原始输入，已验证 |
| `Helpers/Hotkey.cs` | `Infrastructure/HotkeyManager.cs` | 基本照搬 | 全局热键注册 |
| `Helpers/TimeMachine.cs` | 由 `Ink/StrokeHistory.cs` 重写 | **参考思路** | 它的撤销机制绑定自身场景，重写更干净 |
| `Settings.cs` + `SettingsPage.xaml` | `Infrastructure/AppSettings.cs` | **重写** | 原实现基于 `.settings` 生成类，耦合重 |
| `Resources/Styles/Light.xaml`、`Dark.xaml` | `Resources/Styles/` | 精简移植 | 主题色板可复用 |
| `Microsoft.Ink` 相关调用（分散在 `MainWindow.xaml.cs`） | `Infrastructure/SeewoPenAdapter.cs` | **提取重写** | 从 329KB 主窗口里把那几段孤立出来封装 |
| `Helpers/InkRecognizeHelper.cs` | 首版**不用** | 暂缓 | 手写识别非首版需求 |
| `MainWindow.xaml.cs`（329KB） | **不移植** | 放弃 | 与原形态（屏幕批注悬浮层）强绑定，搬过来是负债 |
| `IACore.dll` / `IALoader.dll` / `IAWinFX.dll` / `Microsoft.Ink.dll` | 视 D2 决定 | 按需引入 | 仅 .NET Framework 路线可用 |
| `Ink Canvas Packaging/*.wapproj` | **不用** | 放弃 | 我们不需要 UWP/MSIX 打包 |

### 6.3 NuGet 依赖（首版）

| 包 | 用途 | 备注 |
|---|---|---|
| `PdfiumViewer.Updated`（Bluegrams fork） | PDF 解析与渲染 | 原版 `pvginkel/PdfiumViewer` **已归档停更**；此 fork 支持 .NET Core 3.1 / .NET 6，Apache-2.0，推荐 2.13.4+ |
| `PdfiumViewer.Native.x86_64.v8-xfa` | pdfium 原生 x64 DLL | **必须单独装**，主包不带原生库，缺了会运行时崩 |
| `System.Drawing.Common` | `PdfDocument.Render` 返回 `System.Drawing.Image` | .NET 6+ 仅 Windows 可用，本场景没问题 |
| `CommunityToolkit.Mvvm`（可选） | MVVM 样板代码 | 若你不想引第三方，可手写 `INotifyPropertyChanged` 基类 |
| `Newtonsoft.Json` 或 `System.Text.Json` | `meta.json` / `pages.json` | 内置的够用，倾向不引额外包 |

**注意**：`PdfiumViewer.Updated` 的 `PdfDocument` **非线程安全**，且 `Render` 返回的非托管图像需要显式 `Dispose`，否则长时间上课会漏 GDI 句柄 —— 这条在 §3.5 的实现里必须落实。

### 6.4 首版明确不做

- 手写识别 / 公式识别
- 导出带批注 PDF
- 学科工具实现（只留 `ITeachingWidget` 接口）
- 多文档标签页
- 云同步 / 协作

---

## 7. 里程碑（每个里程碑都可独立验证）

| 里程碑 | 内容 | 验证标准 |
|---|---|---|
| **M0** | 装工具链 + 建空工程 | 一条 build 命令编译通过，弹出空窗口 |
| **M1** | 打开 PDF → 按 world 坐标摆页 → fit-width | 打开 4 页试卷，比例正确、无变形、无错页 |
| **M2** | 视口：Ctrl+滚轮（光标锚点）/ 空格平移 / 双指 | 缩放到 400% 后反复平移，PDF 不模糊不抖、锚点不跑 |
| **M3** | 接入 `InkCanvas` 到世界层 | **关键验证**：写几笔 → 缩放到 400% → 平移 → 笔迹与题干**始终贴合**，无漂移 |
| **M4** | 笔 / 橡皮 / 压感 / 希沃笔尾 | 一体机上笔尾反转即擦，压感线条粗细自然 |
| **M5** | 撤销 / 重做 | 连写 20 笔再全撤销，状态与绘制顺序一致 |
| **M6** | `.wbx` 保存 / 加载 | 存盘退出重开，缩放平移随意改，内容仍在原位 |
| **M7** | 工具插件宿主 | 能挂一个示例工具（如角度尺占位）并正确随画布变换 |

**M3 是整个方案成败的关键验证点** —— 它验证 §3.2 那个"零自定义矩阵数学"的假设是否真的成立。

---

## 8. 风险清单

| 风险 | 等级 | 说明与对策 |
|---|---|---|
| 本机无 SDK，无法验证"可编译" | **高** | 必须 M0 先装 SDK（D2 定了才知装哪个） |
| GPL-3.0 传染性 | **中** | Ink-Canvas 是 GPL-3.0。**仅自用**无影响；**若要分发给同行或商用，衍生作品需同样开源**。若不能接受 GPL，就只能"只读参考、不复制代码"，那么 `Hotkey`/`MultiTouchInput` 也要重写。这条请务必确认 |
| 笔尾橡皮在 .NET 8 下不可用 | 中 | 取决于 D2；选 .NET Framework 4.8 可规避 |
| 高倍缩放内存爆 | 中 | §3.5 的 LRU + 分档 + 视口优先；需实测一体机内存 |
| `InkCanvas` 尺寸上限 | 低 | 软边界钳制（`CanvasBoundsGuard`） |
| 索引 PDF 页面错页 | 低 | PdfiumViewer 页索引从 0 开始，注意与 UI 的 1-based 显示换算 |
| 一体机触摸误触 | 中 | D6 的输入分工策略 + `SettingsPage` 式手动兜底开关 |

---

## 9. 待你确认的事项汇总

1. **D1** 是否接受"只移植模块、不 fork 主窗口"？
2. **D2** 目标框架选 **.NET Framework 4.8** 还是 **.NET 8**？（影响笔尾橡皮方案；我能拿到实机验证吗？）
3. **D3** 确认 PDF 走离屏渲染（不用 `WindowsFormsHost`）？
4. **D4** 笔迹粗细随缩放（A）可以吗？
5. **D5** 页面布局默认连续纵向 + 可切单页？
6. **D6** 输入分工按 §5.1 的表定？
7. **D7** 同意 M1 先不接笔迹、只验证底图与坐标系？
8. **GPL** 仅自用，还是将来要分发？这决定能否直接复用 Ink-Canvas 代码。

确认后，我从 **M0（装工具链 + 空工程编译通过）** 开始，一次只做一个里程碑。
