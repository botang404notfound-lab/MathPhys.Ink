# AGENTS.md — 数理墨（MathPhys Ink）

希沃一体机上的 PDF 试卷讲评白板（教师用）。C# / WPF / .NET 8，**仅 Windows x64**。
墨迹用 Windows Ink，PDF 用 PdfiumViewer 离屏渲染，学科工具（直尺、三角板、量角器、圆规、
坐标系、函数图像、公式、电路、GeoGebra、矢量箭头、物理仿真）全部走插件加载。

> **改名沿革**：本项目原名「唐波的白板 / TangBo WhiteBoard」，2026-10-02 全量改名为
> **数理墨 / MathPhys Ink**（命名空间、程序集、工程目录、EXE、打包脚本一并改）。
> ★ 只有"格式标识"保持不变：`.twb` / `.tbink` 扩展名、`TBINK` 魔数、`TwbFile`/`TbinkFile`/
> `TDialog` 等类型名 —— 改了会砸掉已有工程与批注的兼容性。契约程序集同名一改属破坏性改动，
> 契约版本随之升到 **2.0.0**，全部插件必须随宿主一起重建。

本目录是 git 仓库（已 `git init`）。改动靠 `docs/` 记录、git 历史 + `dist/` 打包留存。
交付物的"源"必须在仓库里有源文件，**不能只活在打包暂存目录里**。

## 命令

全部在仓库根目录、用 PowerShell 跑。跑任何 dotnet 命令前先置语言环境：

```powershell
$env:DOTNET_CLI_UI_LANGUAGE = "en"   # 否则中文环境输出 GBK，日志读出来是乱码
```

- 构建全解决方案：`dotnet build MathPhys.Ink.sln -c Release -m:1 -nodeReuse:false`
  （`-m:1 -nodeReuse:false` 是必需的：否则 MSBuild 跨进程 node 会被拦，表现为
  `Build FAILED. 0 Warning(s) 0 Error(s)` 并泄漏上千个 dotnet.exe）
- 运行主程序：`dotnet run --project src\MathPhys.Ink\MathPhys.Ink.csproj`
- 回归测试（唯一测试入口，无单元测试框架）：`python tools\_run_harness.py`
  → 打印 `PASS=n FAIL=m` 与尾部摘要，全文日志在 `artifacts\_harness_run.txt`
- 打包 / 整理：`tools\_m9_pack.py --variant both`、`tools\_m10_tidy.py`（默认 DRY-RUN）、
  `tools\_m9_rebuild_stage.py`、`tools\_m9_cleanup_dist.py --go`

判定标准：**FAIL 必须为 0**。最近一次基线是 2026-10-02 改名后的 1030/0。改了行为就相应加断言。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/MathPhys.Ink/` | 主程序：`Views/` `ViewModels/` `Ink/` `Pdf/` `Gfx/` `Tools/` `Viewport/` `Export/` `GeoGebra/` `Design/` `Workflows/` `Input/` `Infrastructure/` |
| `src/MathPhys.Ink.Plugin.Abstractions/` | 插件契约。插件经独立 ALC 加载，**必须共用同一份**，否则出现两个同名不同类型 |
| `src/MathPhys.Ink.Plugin.*/` | 13 个学科工具插件，按目录名加载 |
| `tools/PdfSmokeTest/` | 回归 harness，`ProjectReference` 引用生产工程，能无窗口实例化真实控件树 |
| `tools/_*.py` | 打包 / 清理 / 取回第三方资源的运维脚本 |
| `tools/_archive/` | 历史一次性脚本（**已排除出公开仓库**，含本机绝对路径） |
| `artifacts/` | 冒烟脚本（`smoke_*.py`）与产物（**不进仓库**） |
| `docs/` | 设计方案与踩坑清单 01~26 |
| `README.md` / `THIRD-PARTY-NOTICES.md` / `LICENSE` | 项目说明 / 第三方许可与署名 / GPL-3.0 |
| `.workbuddy/memory/MEMORY.md` | 长期记忆索引：里程碑状态、当前基线、待补项（**不进仓库**） |

## 工作方式（五条铁律）

① 不重写墨迹引擎；② 一次只做一个小功能；③ 先给方案 + 文件清单，确认后再写代码；
④ 中文注释；⑤ 要测试就打免安装测试包。

## 不能破坏的不变量

- 世界坐标 = **PDF point**，持久化只存世界坐标。`RootGrid` > `WorldHost`（**全程序唯一
  RenderTransform**）> `PdfPageLayer` + `GfxLayer` + `InkLayer`；`InkLayer.Left/Top = 0`
  ⇒ 其本地坐标 ≡ 世界坐标。
- 插件契约只加**新接口**，`ITool` 成员零新增（否则旧插件 `TypeLoadException`）；
  可选接口用 `is` 判断且必须实例实现。
- 渲染器不碰 `RenderTransform` / `Canvas.Left/Top`，本地坐标以 `(0,0)` 为中心。
- 被 `Scale` 放大的树里，固定尺寸图元（线宽、字号、箭头）都要**除 Scale**；
  「世界长常量」进按数学单位画的本地几何要**除 `unitWorld`（≈28.35）**。
- `Geometry.Freeze()` 之后写 `Transform` 会抛异常 ⇒ 先设 `Transform` 再 `Freeze`。
- 导出 = 借画布离屏渲染：**必须 `BeginRenderFreeze`**（否则导出白纸，偶发）；
  视口对齐用「负的页左上角」而不是 `SetView(1,0,0)`；进 PDF 页尺寸必须是 PDF point。
- 中文断言串 / 中文字符串里**禁止 ASCII 双引号** `"`（会截断 C# 字符串或让源级断言锚点失效）
  ⇒ 用 `「」` 或 `“”`。
- **持久化格式标识不得更改**：`.twb`/`.tbink` 扩展名、`TBINK` 魔数、图层属性 Guid
  （`InkLayers.PropertyKey`）。改一个字节就让老师的旧工程/旧批注打不开。

## 环境坑（都付出过代价，别重复）

- **本机没有 bash 工具**（无 `ls`/`grep`/`head`），用 PowerShell；stdout 常不回传 ⇒
  重定向到工作区内文件再读：`$text = & cmd args 2>&1 | Out-String` 再 `Out-File -Encoding utf8`。
  `*>` 重定向写出的文件会被当成二进制（UTF-16）。
- **同一文件多条 Edit 会静默丢改动**（每条都返回成功，实际后写覆盖先写，踩过四次）
  ⇒ 同一文件必须逐个串行改；改完**用 Python 读字节确认关键符号在不在**，返回"成功"不算证据。
  排查顺序：**先怀疑"改动没落地"，再怀疑"逻辑写错了"**。
- **改名前必须区分"品牌名"与"格式标识"**：2026-10-02 改名时，`tools/_run_harness.py` 里
  硬编码的仓库绝对路径 `...\WorkBuddy\唐波的白板` 被文本替换顺手改成 `...\数理墨`，
  路径当场断掉。★ 教训：**任何"全量文本替换"都要先把"作为路径/标识符出现"的同一字符串
  单独挑出来审计**，别把显示名和路径名当一回事。同理，正则里的分段写法
  （`TangBo[.]WhiteBoard`）不会被 `TangBo.WhiteBoard` 替换命中，会被次级规则误伤成
  `MathPhys[.]WhiteBoard`，把一条**否定断言变成恒真探针**。
- 每次 build 后先看 `$LASTEXITCODE` 再决定要不要 run，否则 `--no-build` 会拿旧 dll 跑出**假通过**。
- **结论性回归必须在沙箱外跑**：沙箱禁 `File.Replace` ⇒ `TwbFile` 断言假 FAIL。
- 光栅结论**必须像素采样确证**，不靠肉眼；「图上没看见」不等于「图上没有」。
- 断言要比被测格式宽松：ISF 往返（非位精确）位置容差 **0.05**、笔宽容差 **0.01**，
  像素数用 **1% 相对容差**。断言比被测格式还严时，是断言错了。
- 断言工具本身要先自证：曾因 `check()` 参数顺序写反，13 条断言**恒真**，
  `FAIL=0` 并不能证明查过了。
- 批量删除（>50 文件）会撞沙箱拦截 ⇒ 别删，`os.rename` 改名挪到 `%TEMP%`。

## 细节在哪儿

- `docs/06-工程约定与踩坑清单.md`（137 KB）—— **最重要的活文档**，动手前先搜这里；
  新增踩坑追加到对应小节。
- `docs/01`~`26` —— 各里程碑设计方案；`docs/14` 导出、`docs/16` 笔锋、`docs/26` 最新一版。
- `README.md` —— 面向使用者的功能说明、构建方式、插件架构与许可。
- `.workbuddy/memory/MEMORY.md` —— 里程碑状态、当前交付基线与待补项索引。
