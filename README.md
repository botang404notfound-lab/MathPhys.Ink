# 数理墨 · MathPhys Ink

> 面向教室一体机的**数理试卷讲评白板**。
> 打开 PDF 试卷就能写、就能画、就能导出；学科工具（直尺、三角板、量角器、圆规、坐标系、
> 函数图像、公式、电路、矢量箭头、物理仿真）全部以插件形式加载。

为希沃（Seewo）一类**红外框 + 被动笔**的教室一体机设计：无真压感、手指与笔混点、
需要"站着用一只手就能操作"的界面。技术栈 C# / WPF / .NET 8，仅 Windows x64。

---

## 它解决什么问题

讲评一份 PDF 试卷，最顺手的做法是在试卷本身上面写。但一体机自带的批注工具通常：

- 只能在同一页上涂，**换页就丢**，或者根本不能导入 PDF；
- 画直线/圆只能靠手抖，**没有尺规**；
- 电路图、函数图像、公式得切到别的软件，画完截图再贴回来；
- 写完的批注**导不出**，学生拿不走。

数理墨把这些做成一件事：**PDF 当纸，墨迹是笔，学科工具是尺规，导出是收作业**。

---

## 功能

### 核心

| 能力 | 说明 |
| --- | --- |
| PDF 试卷画布 | PdfiumViewer 离屏渲染，无限画布，多页，缩放/平移不改墨迹世界坐标 |
| 手写墨迹 | Windows Ink 墨迹栈，采样率约 213 Hz；笔锋（速度—压力映射）、橡皮（笔尾/粗头） |
| 图层 | 批注层 / 草稿层两层，草稿可整体隐藏、一键清空，不进导出 |
| 撤销 / 重做 | 墨迹与图形统一时间线，一次 Ctrl+Z 对得上账 |
| 题号标记 | 一键给题目打「题号气泡」，可索引跳转 |
| 工程文件 `.twb` | 一个 ZIP 把试卷、批注、图形、视图位置一起装走，拷一个文件就齐 |
| 批注侧车 `.tbink` | 不想建工程时，批注就存在试卷旁边的同名文件里 |
| 导出 | 导出为 PDF / PNG，含墨迹与图形；导出走离屏渲染，所见即所得 |

### 学科工具（全部插件化）

- **尺规**：直尺（含自由角）、三角板 45°/60°（可沿边画线）、量角器、圆规（任意夹角圆弧）
- **坐标与函数**：平面坐标系（含无网格变体）、空间坐标系、函数图像（表达式解析、特征点）
- **书写与符号**：LaTeX 公式（WPF-Math 渲染）、数学键盘（表达式键位 / LaTeX 键位共用一套）、
  矢量箭头（单矢量 / 分组 / 求和 / 正交分解）
- **电与力**：电路元件库（自动连线与吸附）、学科工具包（电场线等图片资料库）
- **仿真演示**：GeoGebra 离线演示面板；物理仿真（原生运动学求解器 + 网页仿真
  CircuitJS / PhET，可抓帧导回画布）

工具按 **笔类 / 图形 / 文档 / 系统** 分组排布，同族工具折叠成一个瓦片，点开换本族其它工具。

---

## 快速开始

> 仅支持 **Windows x64**。需要 .NET 8 SDK。

```powershell
# 中文环境下 dotnet 输出会是 GBK 乱码，跑之前先置语言
$env:DOTNET_CLI_UI_LANGUAGE = "en"

# 构建（-m:1 -nodeReuse:false 是必需的，否则 MSBuild 跨进程 node 会被拦）
dotnet build MathPhys.Ink.sln -c Release -m:1 -nodeReuse:false

# 运行
dotnet run --project src\MathPhys.Ink\MathPhys.Ink.csproj
```

回归测试（本仓库没有单元测试框架，唯一测试入口是这个 harness）：

```powershell
python tools\_run_harness.py     # 打印 PASS=n FAIL=m，全文日志在 artifacts\_harness_run.txt
```

**判定标准：`FAIL` 必须为 0。** 改了行为就要相应加断言。

---

## 目录结构

```
src/
  MathPhys.Ink/                        主程序（Views / ViewModels / Ink / Pdf / Gfx / Tools /
                                       Viewport / Export / WebPanel / GeoGebra / Design /
                                       Workflows / Input / Infrastructure）
  MathPhys.Ink.Plugin.Abstractions/    插件契约（插件必须共用同一份，见下）
  MathPhys.Ink.Plugin.*/               13 个学科工具插件，按目录名加载
tools/
  PdfSmokeTest/                        回归 harness（ProjectReference 引用生产工程，可无窗口实例化真实控件树）
  _*.py                                打包 / 整理 / 取回第三方资源的运维脚本
docs/                                  26 份设计方案与踩坑清单（01~26），活文档是 06
artifacts/                             回归日志与脚本产物（已 gitignore）
dist/                                  测试包暂存（已 gitignore）
```

---

## 插件架构（两条不能破的规则）

1. **契约只加新接口，`ITool` 成员零新增。**
   给 `ITool` 加成员会让旧插件 `TypeLoadException`。需要新能力时加**新接口**，
   用 `is` 判断且由实例实现。

2. **所有插件必须共用同一份 `MathPhys.Ink.Plugin.Abstractions`。**
   插件经独立 `AssemblyLoadContext` 加载，宿主把契约 dll「往回走」共享给插件。
   一旦让插件从自己目录里加载契约副本，运行时就会出现**两个同名不同类型的
   `IWhiteBoardPlugin`**，`as` 判断静默返回 `null` ——
   表现是「dll 明明在、一个工具都没注册、还不报错」，最难查的那类故障。

装载是**失败隔离**的：任何一个插件出错都只损失那一个，记日志、写报告、继续下一个，
绝不抛异常、绝不弹窗 —— 老师站在讲台上，程序崩了没有第二次机会。

契约版本规则：破坏性改动（改名、改语义、给 `ITool` 加成员）→ 主版本 +1，旧插件被明确拒绝；
纯新增接口 → 次版本 +1，旧插件照常加载。当前契约版本见
`src/MathPhys.Ink.Plugin.Abstractions/*.csproj` 的 `<Version>`。

### 停用 / 添加插件

插件放在程序目录的 `plugins\<插件名>\<插件名>.dll`。

- **停用**：把子目录改名成 `_` 开头（如 `plugins\_protractor`）。
- **添加**：把新插件目录拷进 `plugins\`，重启程序。

两者都不需要改任何配置文件 —— 老师拖一下文件夹就完成，也就没有「配置漂移」。

---

## 打包

```powershell
tools\_stage_plugins.py        # 把各插件产物铺进暂存目录
tools\_m9_rebuild_stage.py     # 重建自包含发布目录（WebView2 运行时从上一版完整包取回）
tools\_m9_pack.py --variant both
tools\_m9_cleanup_dist.py --go
```

产出**全量版 / 轻量版**双包：轻量版剔除 `websim\`（网页仿真）与部分重型资源，
体积约为全量版的 1/7，课堂日常用轻量版。

---

## 第三方资源

仓库内 `src/MathPhys.Ink/Assets/` 下携带了两份**原样未改**的第三方离线包：

| 目录 | 内容 | 来源 |
| --- | --- | --- |
| `geogebra/` | GeoGebra Math Apps Bundle 离线包 | 官方分发，`tools/fetch-geogebra-bundle.mjs` 取回 |
| `websim/` | CircuitJS1（`circuitjs/`）、PhET 仿真（`phet/`） | 官方分发，`tools/fetch_circuitjs.py`、`tools/fetch_phet.py` 取回 |

均为**原样携带、一个字节未改**，作者与许可见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

> ⚠️ **注意**：GeoGebra 官方离线包只允许**非商业**使用。因此**本仓库整体不得用于商业用途**，
> 无论 `LICENSE` 怎么授权。详见 `THIRD-PARTY-NOTICES.md`。需要商业化时把 `GeoGebra` 插件整体拔除即可
> —— 该功能是可插拔设计，拔除后其余功能不受影响。

---

## 许可

本项目以 **GNU General Public License v3.0** 发布，全文见 [`LICENSE`](LICENSE)。

之所以是 GPL-3.0 而不是 MIT：本项目**移植了 [Ink-Canvas](https://github.com/WXRIW/Ink-Canvas)（GPL-3.0）
中已在希沃设备上验证过的模块**（全局热键、多点触控原始输入、希沃笔尾橡皮识别、主题字典）。
GPL 的传染性决定了衍生作品必须以同样条款开源。若你需要 MIT/Apache 许可的版本，
必须先把上述移植模块重写掉。

**另有第三方附加约束**（GeoGebra 非商业条款等），见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

## 致谢

- [Ink-Canvas](https://github.com/WXRIW/Ink-Canvas) —— 希沃设备适配经验的来源
- [PdfiumViewer (Bluegrams fork)](https://github.com/Bluegrams/PdfiumViewer) —— PDF 离屏渲染
- [PdfSharpCore](https://github.com/ststeiger/PdfSharpCore) —— PDF 导出
- [WPF-Math (XAML-Math)](https://github.com/ForNeVeR/xaml-math) —— LaTeX 公式排版
- [GeoGebra](https://www.geogebra.org) · [CircuitJS1](https://github.com/pfalstad/circuitjs1) ·
  [PhET](https://phet.colorado.edu) —— 离线演示与仿真
- Microsoft Edge WebView2 —— 网页面板宿主

---

*数理墨（MathPhys Ink）—— 数理，墨，一板之间。*
