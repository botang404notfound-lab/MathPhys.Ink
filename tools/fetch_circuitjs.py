# -*- coding: utf-8 -*-
"""取回 CircuitJS 官方离线包，剥出纯 Web 部分，落到 Assets/websim/circuitjs/（M22 S5）。

为什么是 Python 而不是 docs/25 原计划的 .mjs
----------------------------------------------------------------
docs/25 里这步原本写成 tools/fetch-circuitjs.mjs。落地时改成 Python：
本仓库的运维脚本清一色 Python，且解 zip 用 stdlib zipfile 即可 ——
Node 侧解 zip 反而要引入依赖。意图不变：**唯一联网动作**就是下载官方离线 zip。

为什么用 falstad.com 的 Windows 离线包
----------------------------------------------------------------
https://www.falstad.com/circuit/offline/ 的 circuitjs1-win.zip 是 NW.js 封装
（约 109 MB，Chromium 运行时占大头），但里面的
circuitjs1/resources/app/war/ 就是**完整的 GWT Web 构建**：
circuitjs.html + circuitjs1/circuitjs1.nocache.js + *.cache.js + 字体 + 示例电路。
把 war/ 剥出来就能用 WebView2 + 虚拟域名直接跑，不需要 NW.js 那一百 MB。

剔除什么（保留能跑的最小集）
----------------------------------------------------------------
- WEB-INF/       —— GWT 的 Java 编译产物（symbolMaps 等），浏览器用不上，约 1.6 MB；
- pong/、avr8js/、avr8js-build/ —— 彩蛋与 Arduino 变体，物理课用不上；
- doc/           —— 英文帮助文档；
- 顶层 index.html —— falstad 的落地页（带 Google 广告脚本）。index.html 这个名字
  留给自建导航页（Assets/websim/index.html，宿主 profile 的 EntryHtml）。

用法
----------------------------------------------------------------
    python tools/fetch_circuitjs.py            # 下载（若缓存没有）+ 解包 + 校验
    python tools/fetch_circuitjs.py --check    # 只校验已落地的资源，不联网

产物自证
----------------------------------------------------------------
解完后逐项核对入口与 GWT 输出是否齐全，缺一样就非零退出 ——
「解包成功但少文件」的表现是面板白屏，现场无从排查。
"""
import argparse
import os
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET = os.path.join(ROOT, "src", "MathPhys.Ink", "Assets", "websim")
APP = os.path.join(TARGET, "circuitjs")

# 下载缓存位置（重复跑不重复下 109 MB）。
CACHE = os.path.join(ROOT, "artifacts", "_m22s5_dl", "circuitjs1-win.zip")
SOURCE_URL = "https://www.falstad.com/circuit/offline/circuitjs1-win.zip"

# zip 里 war/ 的前缀（NW.js 封装把 Web 应用放在 resources/app/war/）。
WAR_PREFIX = "circuitjs1/resources/app/war/"

# 剔除清单（相对 war/ 的路径前缀或文件名）。见文件头说明。
PRUNE_PREFIXES = ("WEB-INF/", "pong/", "avr8js/", "avr8js-build/", "doc/")
PRUNE_FILES = ("index.html", "mexle.html")

# 解完之后必须存在的文件（相对 circuitjs/）。缺一样 = 白屏风险，直接 FAIL。
REQUIRED = (
    "circuitjs.html",
    "circuitjs1/circuitjs1.nocache.js",
    "font/fontello.css",
    "lz-string.min.js",
)

CREDIT = """CircuitJS1（电路模拟器）离线资源
====================================

来源：{url}
取回日期：{date}
原包大小：{zip_mb:.1f} MB（NW.js 封装）；本目录只保留了其中的 Web 构建部分（war/）。

CircuitJS1 原作者 Paul Falstad（Java Applet），Iain Sharp 移植到浏览器（GWT）。
许可证：GNU General Public License v2（GPL-2.0）—— 本目录按 GPL 要求原样保留
分发（未改动任何官方文件；剔除清单见 tools/fetch_circuitjs.py 文件头）。

项目主页：https://github.com/pfalstad/circuitjs1
在线版：https://www.falstad.com/circuit/

重新取回：python tools/fetch_circuitjs.py
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="取回 CircuitJS 离线资源（M22 S5）")
    parser.add_argument("--check", action="store_true", help="只校验已落地资源，不联网")
    args = parser.parse_args()

    if args.check:
        return verify()

    # ---- 1) 下载（带缓存）----
    if os.path.exists(CACHE) and os.path.getsize(CACHE) > 50 * 1024 * 1024:
        print("缓存命中：%s（%d MB）" % (CACHE, os.path.getsize(CACHE) // (1024 * 1024)))
    else:
        os.makedirs(os.path.dirname(CACHE), exist_ok=True)
        print("下载 %s ..." % SOURCE_URL)
        urllib.request.urlretrieve(SOURCE_URL, CACHE)
        print("完成：%d MB" % (os.path.getsize(CACHE) // (1024 * 1024)))

    # ---- 2) 解包（先清掉旧目录再铺新的，保证可重复执行）----
    if os.path.exists(APP):
        import shutil
        # 只删本脚本自己管理的这个目录；目录内容全部来自官方 zip，可再生。
        shutil.rmtree(APP)
    os.makedirs(APP, exist_ok=True)

    z = zipfile.ZipFile(CACHE)
    names = z.namelist()
    war_names = [n for n in names if n.startswith(WAR_PREFIX) and not n.endswith("/")]
    if not war_names:
        print("FAIL：zip 里找不到 %s（官方包结构变了？）" % WAR_PREFIX)
        return 1

    kept = 0
    for n in war_names:
        rel = n[len(WAR_PREFIX):]
        if any(rel.startswith(p) for p in PRUNE_PREFIXES):
            continue
        if rel in PRUNE_FILES:
            continue
        out = os.path.join(APP, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "wb") as f:
            f.write(z.read(n))
        kept += 1

    # ---- 3) 版本与许可说明 ----
    version = ""
    if "circuitjs1/version" in names:
        version = z.read("circuitjs1/version").decode("utf-8", errors="replace").strip()
    import datetime
    credit = CREDIT.format(
        url=SOURCE_URL,
        date=datetime.date.today().isoformat(),
        zip_mb=os.path.getsize(CACHE) / (1024 * 1024))
    if version:
        credit = "版本标记（包内 version 文件）：%s\n\n" % version + credit
    with open(os.path.join(APP, "来源与版本.txt"), "w", encoding="utf-8") as f:
        f.write(credit)

    print("解包完成：%d 个文件（剔除 %d 个）"
          % (kept, len(war_names) - kept))
    return verify()


def verify() -> int:
    """产物自证：入口 + GWT 输出 + 字体 + 压缩库，缺一样就 FAIL。"""
    ok = True
    for rel in REQUIRED:
        p = os.path.join(APP, rel.replace("/", os.sep))
        hit = os.path.exists(p) and os.path.getsize(p) > 0
        print("  %-4s %s" % ("OK" if hit else "MISS", rel))
        ok = ok and hit

    # GWT 按浏览器排列产物出多个 *.cache.js，至少要有一个。
    gwt_dir = os.path.join(APP, "circuitjs1")
    caches = [f for f in os.listdir(gwt_dir)
              if f.endswith("cache.js")] if os.path.isdir(gwt_dir) else []
    print("  %-4s GWT *.cache.js × %d" % ("OK" if caches else "MISS", len(caches)))
    ok = ok and bool(caches)

    # 顶层 index.html 必须不存在 —— 那个名字属于自建导航页，
    # 官方落地页（带广告脚本）被剔除，别让它改回来。
    bad = os.path.join(APP, "index.html")
    if os.path.exists(bad):
        print("  MISS index.html 不应存在（官方落地页必须被剔除）")
        ok = False

    total = sum(os.path.getsize(os.path.join(r, f))
                for r, _, fs in os.walk(APP) for f in fs)
    print("  合计 %.1f MB，目录 %s" % (total / (1024 * 1024), APP))
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
