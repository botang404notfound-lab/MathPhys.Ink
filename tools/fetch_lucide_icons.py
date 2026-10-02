#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 Lucide 官方 SVG 转成 WPF 的 PathGeometry 资源（M20 S3）。

用法（在仓库根目录跑）：:

    python tools\\fetch_lucide_icons.py --fetch     # 只抓缺失的 SVG 到 tools/_lucide_src/
    python tools\\fetch_lucide_icons.py             # 从本地 SVG 生成 Design/Icons.xaml
    python tools\\fetch_lucide_icons.py --check     # 生成一份与磁盘上那份比对，不一致则非零退出

为什么要「先落盘 SVG、再离线生成」：构建与打包**绝不联网**（一体机在教室里没网），
所以源 SVG 必须进仓库；联网只发生在人工迭代这一下，而且只抓缺失的那几个。

转换口径（这是本脚本真正有讲究的地方）：

* 只取**几何**，丢弃一切颜色属性 —— 颜色由使用处给（跟着按钮前景色走），
  换肤不需要动图标。这也意味着脚本可以拿上游 SVG 原样用，不必改它的 style。
* 一律落在 **24×24 网格**（`Stretch=None`）。`viewBox` 不是 `0 0 24 24` 时**直接报错**，
  不自动缩放：Lucide 的路径数据里有大量**相对命令**（`a`/`h`/`v`），
  对它做仿射改写要同时处理弧半径与相对坐标，写错了屏幕上只是「有点歪」，
  很难发现。**宁可炸掉，也不要静默错位。**（换版本时若 viewBox 变了，
  这里会当场拦下，人工看一眼再决定怎么处理。）
* 路径数据做**正规化**：分词后把「隐式重复」展开成显式命令（`M3 3 5 5` → `M3 3 L5 5`），
  数字之间统一补空格。WPF 的 Figures 解析器对连写的负数（`0-2-2`）容错，
  但没必要去赌它。
* 键按**字典序**输出 ⇒ 同样输入永远得到逐字节相同的产物，`--check` 才有意义。

输出文件**整本由本脚本生成**（含两个路径样式），头注释写明「勿手改」。
"""

from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

# ---------------------------------------------------------------- 常量

SVG_NS = "http://www.w3.org/2000/svg"

#: 抓取源。★ 实测 unpkg 会在连续抓几十个之后开始 TLS 握手失败（curl 退出码 35），
#: 换镜像即可 —— 两边都是同一个包的同一版本，内容逐字节一致。
CDN_MIRRORS = (
    "https://unpkg.com/lucide-static@{version}/icons/{name}.svg",
    "https://cdn.jsdelivr.net/npm/lucide-static@{version}/icons/{name}.svg",
)

#: 线宽对齐 Lucide 上游（M10 自绘时是 1.8）。
STROKE_THICKNESS = "2.0"

#: 每条 SVG 命令要几个数字。`Z` 不吃数字。
PARAM_COUNT = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}

TOKEN_RE = re.compile(r"([MmLlHhVvCcSsQqTtAaZz])|(-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)")


# ---------------------------------------------------------------- 路径数据


def tokenize(d: str) -> list[str]:
    """把 `d` 拆成「命令字母 / 数字」两种记号。"""
    out: list[str] = []
    pos = 0
    while pos < len(d):
        ch = d[pos]
        if ch in " ,\t\r\n":
            pos += 1
            continue
        m = TOKEN_RE.match(d, pos)
        if m is None:
            raise ValueError(f"路径数据里解析不动的片段：{d[pos:pos + 24]!r}")
        out.append(m.group(1) if m.group(1) else m.group(2))
        pos = m.end()
    return out


def fmt(v: float) -> str:
    """数字转字符串：去掉多余的 0，避免同一几何写出两种文本。"""
    s = f"{v:.4f}".rstrip("0").rstrip(".")
    if s in ("", "-0", "-"):
        s = "0"
    return s


def normalize_path(d: str) -> str:
    """展开隐式重复命令，并统一数字分隔 —— 让 WPF 的解析器没有二义性。"""
    toks = tokenize(d)
    parts: list[str] = []
    i = 0
    while i < len(toks):
        tok = toks[i]
        if not tok.isalpha():
            raise ValueError(f"命令字母之前出现了数字：{tok!r}")
        upper = tok.upper()
        need = PARAM_COUNT[upper]
        i += 1

        if upper == "Z":
            parts.append(tok)
            continue

        first = True
        while i + need <= len(toks) and not any(t.isalpha() for t in toks[i:i + need]):
            nums = [float(x) for x in toks[i:i + need]]
            i += need
            if upper == "M":
                # ★ 跟在同一对坐标后面的多余数字是**隐式 LineTo**（SVG 语义），
                #   必须显式写出来，否则「M 后面是折线」这层意思会丢。
                letter = tok if first else ("L" if tok == "M" else "l")
            else:
                letter = tok
            parts.append(letter + " ".join(fmt(n) for n in nums))
            first = False
            if i >= len(toks) or toks[i].isalpha():
                break
        if first:
            raise ValueError(f"命令 {tok} 后面没有参数")

    return " ".join(parts)


def force_absolute_start(figures: str) -> str:
    """把一串几何开头的相对命令改成绝对命令。

    ★ 这条不是洁癖，是**必须**：一个 Lucide 图标常由多个 `<path>` 元素组成
    （例如 arrow-down = `M12 5v14` + `m19 12-7 7-7-7`），而它们最终会被拼进
    **同一个** `Figures` 串。SVG 里每个元素的当前点都从 (0,0) 起算，
    拼接后却不重置 —— 保留相对写法时，第二个元素的 `m19 12` 会相对第一个元素的
    终点位移，箭头当场飞出 24 网格（实测落到 (31,31)）。
    """
    i = 0
    while i < len(figures) and not figures[i].isalpha():
        i += 1
    if i < len(figures) and figures[i].islower():
        figures = figures[:i] + figures[i].upper() + figures[i + 1:]
    return figures


def circle_figures(cx: float, cy: float, rx: float, ry: float) -> str:
    """圆/椭圆：两段半圆弧。WPF 没有原生的「整圆」命令，这是标准做法。"""
    left = f"{fmt(cx - rx)},{fmt(cy)}"
    right = f"{fmt(cx + rx)},{fmt(cy)}"
    arc = f"A {fmt(rx)},{fmt(ry)} 0 1 0"
    return f"M {left} {arc} {right} {arc} {left} Z"


def rect_figures(x: float, y: float, w: float, h: float, rx: float, ry: float) -> str:
    """矩形：`rx=0` 退化成四条直线，否则每个角一段 90° 圆弧。"""
    if rx <= 0 or ry <= 0:
        return (f"M {fmt(x)},{fmt(y)} H {fmt(x + w)} V {fmt(y + h)} "
                f"H {fmt(x)} Z")
    arc = f"A {fmt(rx)},{fmt(ry)} 0 0 1"
    return (
        f"M {fmt(x + rx)},{fmt(y)} "
        f"H {fmt(x + w - rx)} {arc} {fmt(x + w)},{fmt(y + ry)} "
        f"V {fmt(y + h - ry)} {arc} {fmt(x + w - rx)},{fmt(y + h)} "
        f"H {fmt(x + rx)} {arc} {fmt(x)},{fmt(y + h - ry)} "
        f"V {fmt(y + ry)} {arc} {fmt(x + rx)},{fmt(y)} Z"
    )


def points_figures(points: str, close: bool) -> str:
    nums = [float(x) for x in re.findall(r"-?(?:\d+\.?\d*|\.\d+)", points)]
    if len(nums) < 4 or len(nums) % 2:
        raise ValueError(f"points 属性不是成对的坐标：{points!r}")
    pairs = [f"{fmt(nums[i])},{fmt(nums[i + 1])}" for i in range(0, len(nums), 2)]
    body = " L ".join(pairs)
    return f"M {body}" + (" Z" if close else "")


def tag(element: ET.Element) -> str:
    return element.tag.split("}", 1)[-1]


def number(element: ET.Element, name: str, default: float = 0.0) -> float:
    raw = element.get(name)
    return default if raw is None else float(raw)


def walk(node: ET.Element, out: list[str]) -> None:
    """深度优先收集几何。`<g>` 递归，其余元数据元素直接跳过。"""
    for child in node:
        name = tag(child)
        if name == "g":
            walk(child, out)
        elif name == "path":
            d = child.get("d")
            if not d:
                raise ValueError("<path> 没有 d 属性")
            out.append(force_absolute_start(normalize_path(d)))
        elif name == "line":
            x1, y1 = number(child, "x1"), number(child, "y1")
            x2, y2 = number(child, "x2"), number(child, "y2")
            out.append(f"M {fmt(x1)},{fmt(y1)} L {fmt(x2)},{fmt(y2)}")
        elif name == "polyline":
            out.append(points_figures(child.get("points", ""), close=False))
        elif name == "polygon":
            out.append(points_figures(child.get("points", ""), close=True))
        elif name == "circle":
            cx, cy, r = number(child, "cx"), number(child, "cy"), number(child, "r")
            out.append(circle_figures(cx, cy, r, r))
        elif name == "ellipse":
            out.append(circle_figures(
                number(child, "cx"), number(child, "cy"),
                number(child, "rx"), number(child, "ry")))
        elif name == "rect":
            rx = number(child, "rx")
            ry = number(child, "ry", rx)
            out.append(rect_figures(
                number(child, "x"), number(child, "y"),
                number(child, "width"), number(child, "height"), rx, ry))
        # title / desc / defs / metadata 等不含几何，静默跳过


def svg_to_figures(svg_text: str, name: str) -> str:
    root = ET.fromstring(svg_text)

    view_box = (root.get("viewBox") or "").split()
    if view_box != ["0", "0", "24", "24"]:
        raise SystemExit(
            f"✗ {name}.svg 的 viewBox 是 {root.get('viewBox')!r}，期望 '0 0 24 24'。\n"
            "  本脚本**故意不自动缩放**：路径里有相对命令与弧半径，"
            "静默做仿射改写只会在屏幕上表现为「图标有点歪」，极难发现。\n"
            "  请人工确认上游版本变化后，改这里的校验或在 map 里换一个图标。")

    parts: list[str] = []
    walk(root, parts)
    if not parts:
        raise SystemExit(f"✗ {name}.svg 里没找到任何几何")

    return " ".join(parts)


# ---------------------------------------------------------------- 几何包围盒（预检）


def _arc_samples(p0: tuple[float, float], rx: float, ry: float, rot: float,
                 large: int, sweep: int, p1: tuple[float, float]) -> list[tuple[float, float]]:
    """把一段 SVG 圆弧采样成折线点。

    ★ 为什么不能只取**端点**：Lucide 里半径动辄 8~10 的圆弧（例如手形掌心的
      「8 8 0 0 1 8 8」）向外鼓出，只看端点看不出它鼓到哪儿。
      弧的真实外沿只能按「端点 → 圆心」参数化（SVG 规范 F.6.5）再取极值。
    """
    phi = math.radians(rot)
    cos_p, sin_p = math.cos(phi), math.sin(phi)

    dx2, dy2 = (p0[0] - p1[0]) / 2.0, (p0[1] - p1[1]) / 2.0
    x1p = cos_p * dx2 + sin_p * dy2
    y1p = -sin_p * dx2 + cos_p * dy2

    rx, ry = abs(rx), abs(ry)
    if rx == 0 or ry == 0:
        return [p0, p1]

    lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry)
    if lam > 1.0:                       # 半径不够长：按规范等比放大
        scale = math.sqrt(lam)
        rx, ry = rx * scale, ry * scale

    numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    coef = math.sqrt(max(0.0, numerator / denominator)) if denominator else 0.0
    if large == sweep:
        coef = -coef

    cxp = coef * rx * y1p / ry
    cyp = -coef * ry * x1p / rx
    cx = cos_p * cxp - sin_p * cyp + (p0[0] + p1[0]) / 2.0
    cy = sin_p * cxp + cos_p * cyp + (p0[1] + p1[1]) / 2.0

    def angle(ux: float, uy: float, vx: float, vy: float) -> float:
        dot = (ux * vx + uy * vy) / (math.hypot(ux, uy) * math.hypot(vx, vy))
        value = math.acos(max(-1.0, min(1.0, dot)))
        return -value if (ux * vy - uy * vx) < 0 else value

    start = angle(1.0, 0.0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    delta = angle((x1p - cxp) / rx, (y1p - cyp) / ry,
                  (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    if not sweep and delta > 0:
        delta -= 2 * math.pi
    if sweep and delta < 0:
        delta += 2 * math.pi

    steps = 240
    points: list[tuple[float, float]] = []
    for index in range(steps + 1):
        theta = start + delta * index / steps
        points.append((
            cx + rx * math.cos(theta) * cos_p - ry * math.sin(theta) * sin_p,
            cy + rx * math.cos(theta) * sin_p + ry * math.sin(theta) * cos_p,
        ))
    return points


def _bezier_samples(points: list[tuple[float, float]], steps: int = 64) -> list[tuple[float, float]]:
    """贝塞尔采样（含终点）。

    ★ 为什么不能直接把**控制点**算进包围盒：控制点可以落在曲线之外很远。
      实测 `triangle-right` 的 `c-1.1 0-1.3-.6-.4-1.3` 控制点在 x=1.70，
      而曲线真正最左只到 x≈2.03 —— 拿控制点当外沿会**冤枉**一个本来合规的图标。
    """
    degree = len(points) - 1
    result: list[tuple[float, float]] = []
    for index in range(steps + 1):
        t = index / steps
        x = y = 0.0
        for k, (px, py) in enumerate(points):
            weight = math.comb(degree, k) * (t ** k) * ((1 - t) ** (degree - k))
            x += weight * px
            y += weight * py
        result.append((x, y))
    return result


def geometry_bounds(figures: str) -> tuple[float, float, float, float]:
    """算一串几何的**精确**包围盒（弧与贝塞尔都按参数式采样，不是只取端点/控制点）。

    这是 harness 那条「像素探针」的**离线预检**：不用编译、不用起 WPF，
    换图标之前先在这里筛一遍，省掉「改了 → 编 40 秒 → 才发现在 24 网格外」的循环。
    """
    toks = tokenize(figures)
    points: list[tuple[float, float]] = []
    i = 0
    cx = cy = sx = sy = 0.0
    prev_cubic_control: tuple[float, float] | None = None
    prev_quad_control: tuple[float, float] | None = None

    while i < len(toks):
        command = toks[i]
        upper = command.upper()
        need = PARAM_COUNT[upper]
        i += 1
        if upper == "Z":
            cx, cy = sx, sy
            continue

        while i + need <= len(toks) and not any(t.isalpha() for t in toks[i:i + need]):
            v = [float(x) for x in toks[i:i + need]]
            i += need
            relative = command.islower()

            def at(offset: int) -> tuple[float, float]:
                return ((cx + v[offset], cy + v[offset + 1]) if relative
                        else (v[offset], v[offset + 1]))

            if upper == "M":
                cx, cy = at(0)
                sx, sy = cx, cy          # 新子路径的起点（Z 要回到这里）
                prev_cubic_control = prev_quad_control = None
            elif upper == "L":
                cx, cy = at(0)
                prev_cubic_control = prev_quad_control = None
            elif upper == "H":
                cx = cx + v[0] if relative else v[0]
                prev_cubic_control = prev_quad_control = None
            elif upper == "V":
                cy = cy + v[0] if relative else v[0]
                prev_cubic_control = prev_quad_control = None
            elif upper == "C":
                c1, c2, end = at(0), at(2), at(4)
                points.extend(_bezier_samples([(cx, cy), c1, c2, end]))
                cx, cy = end
                prev_cubic_control = c2
                prev_quad_control = None
            elif upper == "S":
                c1 = ((2 * cx - prev_cubic_control[0], 2 * cy - prev_cubic_control[1])
                      if prev_cubic_control else (cx, cy))
                c2, end = at(0), at(2)
                points.extend(_bezier_samples([(cx, cy), c1, c2, end]))
                cx, cy = end
                prev_cubic_control = c2
                prev_quad_control = None
            elif upper == "Q":
                c1, end = at(0), at(2)
                points.extend(_bezier_samples([(cx, cy), c1, end]))
                cx, cy = end
                prev_quad_control = c1
                prev_cubic_control = None
            elif upper == "T":
                c1 = ((2 * cx - prev_quad_control[0], 2 * cy - prev_quad_control[1])
                      if prev_quad_control else (cx, cy))
                end = at(0)
                points.extend(_bezier_samples([(cx, cy), c1, end]))
                cx, cy = end
                prev_quad_control = c1
                prev_cubic_control = None
            elif upper == "A":
                end = at(5)
                points.extend(_arc_samples((cx, cy), v[0], v[1], v[2],
                                           int(v[3]), int(v[4]), end))
                cx, cy = end
                prev_cubic_control = prev_quad_control = None

            points.append((cx, cy))
            if i >= len(toks) or toks[i].isalpha():
                break
        if i >= len(toks):
            break

    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


# ---------------------------------------------------------------- 抓取


def fetch_missing(root: Path, mapping: dict[str, str], version: str) -> int:
    src_dir = root / "tools" / "_lucide_src"
    src_dir.mkdir(parents=True, exist_ok=True)

    fetched = 0
    for name in sorted(set(mapping.values())):
        dest = src_dir / f"{name}.svg"
        if dest.exists():
            continue
        print(f"  ↓ {name}")

        # ★ 退出码 22 = HTTP 4xx，那是「名字在 Lucide 里不存在」，重试与换镜像都没意义。
        #   其余（尤其 35 TLS 握手失败）隔一下换个镜像重试。
        last_url = ""
        last = None
        for round_index in range(2):
            for template in CDN_MIRRORS:
                last_url = template.format(version=version, name=name)
                last = subprocess.run(
                    ["curl", "-sS", "-f", "-o", str(dest), last_url],
                    capture_output=True, text=True)
                if last.returncode == 0:
                    break
                if last.returncode == 22:
                    break
                time.sleep(1.5)
            if last is not None and last.returncode in (0, 22):
                break

        if last is None or last.returncode != 0:
            raise SystemExit(
                f"✗ 抓取 {last_url} 失败（curl 退出码 {last.returncode if last else '?'}）\n"
                f"  {(last.stderr or '').strip()}\n"
                "  退出码 22 表示该名字在 Lucide 里不存在 —— 换一个候选名再试。")
        fetched += 1

    print(f"[抓取] 新增 {fetched} 个 SVG，现在共 "
          f"{len(list(src_dir.glob('*.svg')))} 个（目录 {src_dir}）")
    return fetched


# ---------------------------------------------------------------- 产物


def geometry_block(mapping: dict[str, str], src_dir: Path) -> list[tuple[str, str]]:
    rows: list[tuple[str, str]] = []
    for key in sorted(mapping, key=lambda k: k.encode("utf-8")):
        name = mapping[key]
        path = src_dir / f"{name}.svg"
        if not path.exists():
            raise SystemExit(
                f"✗ 缺源文件 {path}\n  先跑：python tools\\fetch_lucide_icons.py --fetch")
        rows.append((key, svg_to_figures(path.read_text(encoding="utf-8"), name)))
    return rows


HEADER = '''<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options">

    <!-- ================================================================
         图标（M20 S3：全量换 Lucide 官方 SVG）

         ★ 本文件由 tools/fetch_lucide_icons.py 生成，**请勿手改**。
           改图标 = 改 tools/lucide-map.json，再重跑脚本；
           脚本另有「只比对不写入」的模式：产物被手改过时它非零退出，防止只改一半。
           （★ 头注释里不能出现连续两个减号 —— 那是 XML 注释的终止符，
             MSBuild 会直接报 MC3000 说「XML 不合法」而看不出是注释的问题。）

         来源：lucide-static v{version}（ISC 许可，https://lucide.dev）
         规模：{count} 个几何，全部落在 24×24 网格。

         ★ 为什么全量换掉自绘图标（M10 那 17 个）：自绘图标靠一条家规维持整齐 —
           「内容一律落在 4~20」。而 Lucide 自带「内容落在 2~22」的边距约定，
           两套光学边距混在一起，屏幕上就是「某些图标偏大、某些偏小」。
           换完之后**那条 4~20 的家规随之废弃**，边距统一交给上游网格，
           本文件也不再对坐标做任何微调。

         ★ 只有几何、没有颜色：颜色由使用处给（跟着按钮前景色走），
           所以图标在深色 / 浅色两套主题下都不需要改，也不需要两套资源。

         ★ 用法：<Path Style="{{StaticResource Style.IconPath}}"
                        Data="{{StaticResource Icon.Save}}"
                        Stroke="{{Binding Foreground, RelativeSource=...}}"/>
           Data 用 StaticResource（形状不随主题变），
           Stroke 用绑定（跟着按钮的启用 / 悬停状态变色）。
         ================================================================ -->

    <!-- ================ 路径样式：所有图标都套它 ================
         Stretch=None + 固定 24：图标永远按 24 网格摆，绝不被按钮拉伸变形。
         线宽 {stroke} 对齐 Lucide 上游（自绘时期是 1.8）。 -->
    <Style x:Key="Style.IconPath" TargetType="Path">
        <Setter Property="Width" Value="24" />
        <Setter Property="Height" Value="24" />
        <Setter Property="Stretch" Value="None" />
        <Setter Property="StrokeThickness" Value="{stroke}" />
        <Setter Property="StrokeLineJoin" Value="Round" />
        <Setter Property="StrokeStartLineCap" Value="Round" />
        <Setter Property="StrokeEndLineCap" Value="Round" />
        <Setter Property="Fill" Value="{{x:Null}}" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
    </Style>

    <!-- 按钮里的图标：套上面的路径样式，外加
         ① 右边留 6 的空隙（与文字分开）；
         ② **描边色自动跟着所属按钮的前景色** ——
            这样按钮置灰时里面的图标一起变灰，不用为每个按钮各写一遍绑定。 -->
    <Style x:Key="Style.ButtonIconPath" TargetType="Path" BasedOn="{{StaticResource Style.IconPath}}">
        <Setter Property="Margin" Value="{{StaticResource Gap.Label}}" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Stroke" Value="{{Binding Foreground, RelativeSource={{RelativeSource AncestorType=Control}}}}" />
    </Style>

    <!-- ================================================================
         图标几何（按资源键字典序排列，保证脚本可重跑出逐字节相同的产物）
         ================================================================ -->
'''


def build_xaml(rows: list[tuple[str, str]], version: str) -> str:
    text = HEADER.format(version=version, count=len(rows), stroke=STROKE_THICKNESS)
    for key, figures in rows:
        text += (f'\n    <!-- {key} -->\n'
                 f'    <PathGeometry x:Key="{key}" po:Freeze="True"\n'
                 f'                  Figures="{figures}" />\n')
    text += "\n</ResourceDictionary>\n"

    # ★ 自证：XML 注释里既不能出现连续两个减号，也不能以单个减号收尾。
    #   踩过一次 —— 头注释里写了命令行的「两个减号 + check」，MSBuild 只报
    #   MC3000「XML 不合法」并把行号指到注释中间，完全看不出是注释的问题。
    for m in re.finditer(r"<!--(.*?)-->", text, re.S):
        body = m.group(1)
        if "--" in body or body.endswith("-"):
            raise SystemExit(
                "✗ 生成物里有非法的 XML 注释（含连续减号或以减号结尾）："
                + body.strip()[:60])
    return text


# ---------------------------------------------------------------- 入口


def main() -> int:
    parser = argparse.ArgumentParser(description="Lucide SVG → WPF PathGeometry")
    parser.add_argument("--fetch", action="store_true", help="抓取缺失的 SVG（唯一联网动作）")
    parser.add_argument("--check", action="store_true", help="只比对，不写文件；不一致则非零退出")
    parser.add_argument("--bounds", action="store_true",
                        help="只报每个图标的几何包围盒（离线预检，不写文件）")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    map_path = root / "tools" / "lucide-map.json"
    map_data = json.loads(map_path.read_text(encoding="utf-8"))
    mapping: dict[str, str] = map_data["icons"]
    version: str = map_data["version"]

    if args.fetch:
        fetch_missing(root, mapping, version)
        return 0

    src_dir = root / "tools" / "_lucide_src"

    if args.bounds:
        # ★ 判据：内容必须落在 [2,22] —— 线宽 2 的描边各向外扩 1px，
        #   于是墨迹落在 [1,23]，四边各留 1px，与 harness 的像素探针同一口径。
        # ★ 0.05 的亚像素容差：Lucide 有几个图标（layers / ruler）画到 22.02，
        #   那点覆盖率在光栅上根本不产生像素（harness 实测通过）——
        #   这里跟着放宽一点，免得天天「狼来了」。**真值以 harness 的像素探针为准。**
        tolerance = 0.05
        outside: list[str] = []
        for key in sorted(mapping, key=lambda k: k.encode("utf-8")):
            name = mapping[key]
            path = src_dir / f"{name}.svg"
            if not path.exists():
                print(f"  {key:28s} (缺源文件 {name}.svg)")
                continue
            x0, y0, x1, y1 = geometry_bounds(svg_to_figures(
                path.read_text(encoding="utf-8"), name))
            bad = (x0 < 2.0 - tolerance or y0 < 2.0 - tolerance
                   or x1 > 22.0 + tolerance or y1 > 22.0 + tolerance)
            flag = "◀ 越界" if bad else ""
            print(f"  {key:28s} x {x0:6.2f}..{x1:6.2f}   y {y0:6.2f}..{y1:6.2f}  {flag}")
            if bad:
                outside.append(f"{key}（{name}）")
        print(f"[bounds] {len(mapping)} 个图标，越出 [2,22] 的有 {len(outside)} 个"
              + ("：" + "、".join(outside) if outside else ""))
        return 0
    rows = geometry_block(mapping, src_dir)
    text = build_xaml(rows, version)

    target = root / "src" / "MathPhys.Ink" / "Design" / "Icons.xaml"

    if args.check:
        current = target.read_text(encoding="utf-8") if target.exists() else ""
        if current == text:
            print(f"[check] OK：{len(rows)} 个图标与脚本一致")
            return 0
        print(f"[check] 不一致：{target} 与脚本产物不同（有人手改了，或没重跑脚本）")
        return 1

    unique = sorted(set(mapping.values()))
    print(f"[生成] {len(rows)} 个图标（来自 {len(unique)} 个 SVG，Lucide v{version}）"
          f" → {target.relative_to(root)}")
    target.write_text(text, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
