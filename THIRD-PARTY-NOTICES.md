# 第三方组件 · 许可与署名

**数理墨（MathPhys Ink）** 使用了下列第三方组件。本文件是仓库层面的权威声明；
随程序分发的两份同名说明（`assets/第三方许可与署名.txt`、
`src/MathPhys.Ink/Assets/第三方许可与署名.txt`）是其面向终端用户的口语化版本。

---

## ⚠️ 最重要的一条：本仓库整体**不得商用**

本仓库内携带 **GeoGebra Math Apps Bundle 官方离线包**（`src/MathPhys.Ink/Assets/geogebra/`），
该分发包适用 GeoGebra **自有条款：仅限非商业用途，且必须署名**。

因此：

> **无论根目录 `LICENSE`（GPL-3.0）怎么授权，本仓库及其构建产物都不得用于商业用途。**
> GPL-3.0 授予的开源自由，在此处被 GeoGebra 的非商业条款所限制。

需要商业化时，把 `GeoGebra` 插件整体移除即可 —— 它是可插拔设计，拔除后其余功能不受影响。

---

## 1. GeoGebra（几何动态演示面板）

- **用途**：以「独立演示面板」形态提供动态几何、函数联动、圆锥曲线等重型演示能力。
  白板本体的学科工具仍为自绘实现，不是 GeoGebra。
- **分发内容**：`Assets/geogebra/` —— GeoGebra 官方 Math Apps Bundle 离线包，**原样携带、一个字节未改**。
- **取回方式**：`tools/fetch-geogebra-bundle.mjs`
- **署名**：

  > 本产品包含由 GeoGebra 开发的 GeoGebra 数学应用。
  > GeoGebra 版权归 International GeoGebra Institute 所有。
  > 官方网站：<https://www.geogebra.org>

  该署名同时显示在「GeoGebra 演示面板」界面右下角。
- **许可分层**（2026-09-22 核实 <https://www.geogebra.org/license>）：

  | 部分 | 许可 |
  | --- | --- |
  | GeoGebra 源代码 | EUPL 1.2（旧版许可页曾标注 GPL v3）—— 本项目**未使用、未修改、未编译**其源代码 |
  | 安装包 / Math Apps Bundle（**实际分发的是这个**） | GeoGebra 自有条款：**仅限非商业** + **必须署名** |
  | 语言文件 / 图标 / 样式表 | CC BY-NC-SA 3.0/4.0（含 NC 非商业条款） |

- **商业授权**：<office@geogebra.org>

## 2. CircuitJS1（电路模拟器，物理仿真面板）

- **用途**：「物理仿真 → 网页仿真：电路模拟器」。
- **分发内容**：`Assets/websim/circuitjs/` —— Paul Falstad / Iain Sharp 的 CircuitJS1
  官方 Web 构建（GWT 编译版），**原样携带**（仅剔除浏览器用不到的服务端目录）。取回：`tools/fetch_circuitjs.py`
- **许可**：GNU General Public License v2（GPL-2.0）
- **来源**：
  - 原作者 Paul Falstad（Java Applet）/ Iain Sharp（GWT 移植）
  - 官网 <https://www.falstad.com/circuit/>
  - 源码 <https://github.com/pfalstad/circuitjs1>

## 3. PhET Interactive Simulations（物理仿真面板）

- **用途**：「物理仿真 → 网页仿真：PhET 单摆 / 颜色视觉」等。
- **分发内容**：`Assets/websim/phet/` —— University of Colorado Boulder 的官方
  self-contained 离线文件，**原样携带、未改任何字节**。取回：`tools/fetch_phet.py`
- **许可**：CC-BY-4.0 / GPL-3.0 双许可
- **来源**：<https://phet.colorado.edu/>

## 4. Ink-Canvas（WXRIW/Ink-Canvas）★ 与本项目许可直接相关

- **用途**：本项目的**多点触控原始输入**、**全局热键**、**希沃笔尾橡皮识别**、**主题色板**
  参考/移植自该项目。
- **来源**：<https://github.com/WXRIW/Ink-Canvas>
- **许可**：GPL-3.0

> **这就是本项目必须以 GPL-3.0 开源的原因。** 移植 GPL-3.0 代码构成衍生作品，
> 一旦分发就必须以同样条款开源。若希望改用 MIT/Apache，必须先重写上述四个模块。

## 5. PdfiumViewer.Updated 2.14.5

- **用途**：PDF 文档加载与页面渲染的 .NET 封装。白板走**离屏渲染**，不用 WindowsFormsHost。
- **来源**：pvginkel/PdfiumViewer（上游 2019-08 已归档），本项目用社区更新的
  `PdfiumViewer.Updated`。
- **许可**：Apache License 2.0（保留版权与许可声明；本项目未修改其源代码）

## 6. PdfiumViewer.Native.x86_64.v8-xfa

- **用途**：上条所依赖的**原生 PDFium 运行库**（x86_64，含 V8）。
- **来源**：Google PDFium <https://pdfium.googlesource.com/pdfium/>
- **许可**：BSD-3-Clause

## 7. PdfSharpCore 1.3.67

- **用途**：M9 起导出 PDF。
- **许可**：MIT。其依赖的 `SixLabors.ImageSharp` / `SixLabors.Fonts` 在
  「作为 PdfSharpCore 的一部分分发」时按 Apache-2.0 处理，且 1.3.x 依赖的是
  ImageSharp 1.0.4（自身即 Apache-2.0）—— **不触及 Six Labors 2.x 起的分成许可**，无额外义务。

## 8. WPF-Math / XAML-Math 2.1.0

- **用途**：「公式」工具的 LaTeX 解析与渲染。渲染在本地完成，不联网、不上传数据。
- **来源**：<https://github.com/ForNeVeR/xaml-math>
- **许可**：MIT

## 9. Microsoft WebView2

- **用途**：GeoGebra / 网页面板的宿主控件。发布包内以 **Fixed Version** 模式随应用再分发。
- **许可**：Microsoft 软件许可条款。仅用于本地渲染离线页面，不上传任何用户数据。

## 10. .NET 8 / WPF

- **用途**：应用框架与 UI。
- **许可**：MIT（.NET Foundation）。自包含发布时随包附带 .NET 运行时。

---

## 用户数据

批注（`.tbink`）、工程（`.twb`）与导入的试卷**均为用户自有数据，只保存在本机，不经过任何网络**。
