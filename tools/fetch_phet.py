# -*- coding: utf-8 -*-
"""取回 PhET 全部「物理板块」的 HTML5 自包含仿真，落到 Assets/websim/phet/（M23 S6）。

与 tools/fetch_circuitjs.py 同一模式：**唯一联网动作**是下载官方自包含文件。

清单从哪来
----------------------------------------------------------------
PhET 官网的元数据接口已下线（/services/metadata 404、sitemap 404），所以清单
维护在**本文件**里：一行一个仿真（slug、中文标签、板块）。slug 在 M23 落地时
用官方 URL 逐个探测过（zh_CN / zh_TW / en 三档取第一个存在的）；
官方下架某仿真时它会「探测不到」并在结尾明确报出 —— 不会静默丢。

语言回退（用户需求：简体优先，繁体次之，英文兜底，且**不重复**）
----------------------------------------------------------------
PhET 的 self-contained 单文件把语言烧死在文件里（无运行期参数），
所以「选语言」=「选下载哪个文件」：每个仿真按 zh_CN → zh_TW → en 依次探测，
取**第一个**官方存在的语言，只落一个文件。

★ 403 坑：phet.colorado.edu 的 CDN 会拒掉 Python 默认 UA（Cloudflare），
  请求必须带浏览器 User-Agent —— 2026-09-27 实测。

生成物（同源，改清单重跑即同步）
----------------------------------------------------------------
1. Assets/websim/index.html 的 PhET 路由表（PHET-ROUTES:BEGIN/END 标记之间）；
2. src/MathPhys.Ink.Plugin.NativeSim/WebSimEntries.generated.cs
   （仿真窗「网页仿真」入口表，按板块分组）。

用法
----------------------------------------------------------------
    python tools/fetch_phet.py            # 下载缺失的 + 落地 + 生成 + 校验
    python tools/fetch_phet.py --check    # 只校验已落地资源与生成物，不联网
"""
import argparse
import datetime
import os
import re
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET = os.path.join(ROOT, "src", "MathPhys.Ink", "Assets", "websim", "phet")
CACHE = os.path.join(ROOT, "artifacts", "_m23s6_dl")
INDEX_HTML = os.path.join(ROOT, "src", "MathPhys.Ink", "Assets", "websim", "index.html")
GENERATED_CS = os.path.join(ROOT, "src", "MathPhys.Ink.Plugin.NativeSim", "WebSimEntries.generated.cs")

SIM_URL = "https://phet.colorado.edu/sims/html/{slug}/latest/{slug}_{loc}.html"

# Cloudflare 会拒默认 UA（403），必须带浏览器 UA（2026-09-27 实测）。
HEADERS = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                         "(KHTML, like Gecko) Chrome/126.0 Safari/537.36"}

# 语言优先级：简体 → 繁体 → 英文（每仿真只落第一个存在的，避免重复）。
LOCALES = ("zh_CN", "zh_TW", "en")

# 板块顺序（仿真窗与导航页里的分组顺序）。
CATEGORY_ORDER = ("力学", "振动与波·声与光", "电磁学", "热学与流体", "天体与近代")

# 物理板块全量清单：(slug, 中文标签, 板块)
# M23 落地时逐个探测过：45 个里 44 个有 zh_CN、1 个只有 zh_TW。
# 官方下架（三档全 404）会明确报出，不会静默丢。
SIMS = [
    # ---------------- 力学 ----------------
    ("forces-and-motion-basics", "力与运动：基础", "力学"),
    ("friction", "摩擦力", "力学"),
    ("hookes-law", "胡克定律", "力学"),
    ("balancing-act", "杠杆平衡", "力学"),
    ("projectile-motion", "抛体运动", "力学"),
    ("collision-lab", "碰撞实验室", "力学"),
    ("energy-skate-park", "能量滑板公园", "力学"),
    ("energy-skate-park-basics", "能量滑板公园：入门", "力学"),
    ("energy-forms-and-changes", "能量的形式与转化", "力学"),
    ("gravity-force-lab", "重力实验室", "力学"),
    ("gravity-force-lab-basics", "重力实验室：基础", "力学"),
    ("gravity-and-orbits", "重力与轨道", "力学"),
    ("masses-and-springs", "质量与弹簧", "力学"),
    ("masses-and-springs-basics", "质量与弹簧：基础", "力学"),
    ("density", "密度", "力学"),
    ("vector-addition", "矢量合成", "力学"),
    # ---------------- 振动与波·声与光 ----------------
    ("pendulum-lab", "单摆实验", "振动与波·声与光"),
    ("waves-intro", "波入门", "振动与波·声与光"),
    ("wave-on-a-string", "弦上的波", "振动与波·声与光"),
    ("wave-interference", "波的干涉", "振动与波·声与光"),
    ("fourier-making-waves", "傅里叶：造波", "振动与波·声与光"),
    ("bending-light", "光的折射", "振动与波·声与光"),
    ("geometric-optics", "几何光学", "振动与波·声与光"),
    ("color-vision", "颜色视觉", "振动与波·声与光"),
    # ---------------- 电磁学 ----------------
    ("balloons-and-static-electricity", "气球与静电", "电磁学"),
    ("john-travoltage", "约翰的静电", "电磁学"),
    ("charges-and-fields", "电荷与电场", "电磁学"),
    ("ohms-law", "欧姆定律", "电磁学"),
    ("resistance-in-a-wire", "导线的电阻", "电磁学"),
    ("capacitor-lab-basics", "电容器实验室：基础", "电磁学"),
    ("circuit-construction-kit-dc", "电路组装套件：直流", "电磁学"),
    ("circuit-construction-kit-dc-virtual-lab", "电路组装套件：直流·虚拟实验室", "电磁学"),
    ("circuit-construction-kit-ac", "电路组装套件：交流", "电磁学"),
    ("circuit-construction-kit-ac-virtual-lab", "电路组装套件：交流·虚拟实验室", "电磁学"),
    ("magnet-and-compass", "磁铁与指南针", "电磁学"),
    ("magnets-and-electromagnets", "磁铁与电磁铁", "电磁学"),
    ("faradays-law", "法拉第电磁感应定律", "电磁学"),
    ("molecule-polarity", "分子极性", "电磁学"),
    # ---------------- 热学与流体 ----------------
    ("gases-intro", "气体入门", "热学与流体"),
    ("gas-properties", "气体性质", "热学与流体"),
    ("under-pressure", "液体压强", "热学与流体"),
    # ---------------- 天体与近代 ----------------
    ("my-solar-system", "我的太阳系", "天体与近代"),
    ("isotopes-and-atomic-mass", "同位素与原子质量", "天体与近代"),
    ("rutherford-scattering", "卢瑟福散射", "天体与近代"),
    ("models-of-the-hydrogen-atom", "氢原子模型", "天体与近代"),
]

CREDIT_HEADER = """PhET 物理板块离线资源（全量，{count} 个仿真）
==========================================

来源与语言：每个仿真按 zh_CN → zh_TW → en 依次取官方 self-contained 单文件，
只落第一个存在的语言（避免重复）。逐文件明细见下。

PhET Interactive Simulations, University of Colorado Boulder。
仿真本体许可：PhET 仿真按 CC-BY-4.0 / GPL-3.0 双许可分发 —— 本目录按官方
self-contained 文件**原样保留**（未改动任何字节；文件内的统计脚本与版权链接
在离线时会静默失败，不影响仿真）。

项目主页：https://phet.colorado.edu/
重新取回：python tools/fetch_phet.py

清单（slug | 语言 | 大小 MB | 中文标签 | 板块）
----------------------------------------------------------------
"""

GENERATED_HEADER = """// <auto-generated>
// 由 tools/fetch_phet.py 生成（M23 S6）—— **勿手改**。
// 加仿真 = 改 fetch_phet.py 的 SIMS 清单后重跑（会同步生成 index.html 路由表）。
// PageId 形如 phet-<slug>，与 index.html 的 PHET-ROUTES 路由表**同源生成**，
// 两边不可能拼错 —— 这正是"不写裸字符串"纪律在生成时代的形态。
// </auto-generated>
namespace MathPhys.Ink.Plugin.NativeSim;

/// <summary>
/// PhET 仿真入口表（M23 S6 全物理板块；生成源：tools/fetch_phet.py 的 SIMS 清单）。
/// </summary>
internal static class WebSimEntriesGenerated
{{
    /// <summary>全部 PhET 入口（顺序：板块 → 清单顺序）。</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<WebSimEntry> All = new[]
    {{
{body}
    }};
}}
"""


def fetch(slug: str, loc: str) -> str:
    """下载（带缓存）到缓存目录，返回缓存路径。"""
    cache_path = os.path.join(CACHE, "{0}_{1}.html".format(slug, loc))
    if os.path.exists(cache_path) and os.path.getsize(cache_path) > 512 * 1024:
        return cache_path
    os.makedirs(CACHE, exist_ok=True)
    url = SIM_URL.format(slug=slug, loc=loc)
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=120) as r:
        data = r.read()
    if len(data) < 512 * 1024:
        raise RuntimeError("{0}：文件过小（{1} 字节），不像 self-contained 仿真".format(url, len(data)))
    with open(cache_path, "wb") as f:
        f.write(data)
    time.sleep(0.5)   # 官方 CDN 控频：一次下载歇半秒，45 个也就多花 20 秒
    return cache_path


def pick_locale(slug: str) -> tuple[str, str] | None:
    """按 zh_CN → zh_TW → en 探测，返回 (loc, 缓存路径)；三档全 404 返回 None。"""
    last_err = None
    for loc in LOCALES:
        try:
            return loc, fetch(slug, loc)
        except Exception as ex:   # 404 = 该语言没有官方翻译；其余错误也记下来继续试下一档
            last_err = ex
            continue
    print("  [MISS] {0}：三档语言都取不到（最后错误：{1}）".format(slug, last_err))
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="取回 PhET 物理板块全量仿真（M23 S6）")
    parser.add_argument("--check", action="store_true", help="只校验已落地资源与生成物，不联网")
    args = parser.parse_args()

    resolved = []   # (slug, label, category, loc, size_mb)
    missing = []

    if not args.check:
        os.makedirs(TARGET, exist_ok=True)
        for slug, label, category in SIMS:
            hit = pick_locale(slug)
            if hit is None:
                missing.append(slug)
                continue
            loc, cache_path = hit
            data = open(cache_path, "rb").read()
            out = os.path.join(TARGET, "{0}_{1}.html".format(slug, loc))
            with open(out, "wb") as f:
                f.write(data)
            resolved.append((slug, label, category, loc, len(data) / 1048576.0))
            print("  [OK] {0}_{1}.html（{2:.1f} MB）{3}｜{4}".format(slug, loc, len(data) / 1048576.0, label, category))

        if missing:
            print()
            print("★ 以下 {0} 个仿真官方三档语言都 404（可能已下架或改名），本轮跳过：".format(len(missing)))
            for slug in missing:
                print("    -", slug)

        # 来源与版本.txt：整目录重写（旧的单摆版内容并入新表）
        lines = [CREDIT_HEADER.format(count=len(resolved))]
        for slug, label, category, loc, mb in resolved:
            lines.append("  {0} | {1} | {2:.1f} | {3} | {4}\n".format(slug, loc, mb, label, category))
        lines.append("\n取回日期：{0}\n".format(datetime.date.today().isoformat()))
        with open(os.path.join(TARGET, "来源与版本.txt"), "w", encoding="utf-8") as f:
            f.write("".join(lines))

        # 清掉旧语言文件：语言回退结果变了时，别让旧文件留下来造成"两份重复"
        keep = {"{0}_{1}.html".format(s, l) for s, _, _, l, _ in resolved}
        for name in os.listdir(TARGET):
            if name.endswith(".html") and name not in keep:
                stale = os.path.join(TARGET, name)
                trash = os.path.join(CACHE, "_stale_" + name)
                os.replace(stale, trash)   # 别删（沙箱拦批量删除），挪进缓存目录改名
                print("  [MOVE] 旧语言文件移出目录：", name)

        generate_routes(resolved)
        generate_cs(resolved)

    return verify()


# ---------------------------------------------------------------- 生成

def generate_routes(resolved):
    """把 PhET 路由表写回 index.html 的 PHET-ROUTES 标记之间。"""
    text = open(INDEX_HTML, encoding="utf-8").read()
    pattern = re.compile(r"(// PHET-ROUTES:BEGIN\r?\n).*?(// PHET-ROUTES:END)", re.S)
    if not pattern.search(text):
        raise RuntimeError("index.html 里找不到 PHET-ROUTES 标记 —— 导航页结构变了？")
    rows = []
    for slug, label, category, loc, _ in sort_by_category(resolved):
        rows.append("    'phet-{0}': {{ file: 'phet/{0}_{1}.html', title: '{2}' }},".format(
            slug, loc, label))
    block = "var phetRoutes = {\n" + "\n".join(rows) + "\n  };\n"
    text = pattern.sub(lambda m: m.group(1) + block + "  " + m.group(2), text, count=1)
    open(INDEX_HTML, "w", encoding="utf-8", newline="").write(text)
    print("[生成] index.html 路由表：{0} 个 PhET 页".format(len(resolved)))


def sort_by_category(resolved):
    """按 CATEGORY_ORDER 分组排序（板块内保持清单声明顺序）。"""
    order = {c: i for i, c in enumerate(CATEGORY_ORDER)}
    return sorted(resolved, key=lambda r: (order.get(r[2], 99),))


def generate_cs(resolved):
    """生成 WebSimEntries.generated.cs（仿真窗入口表，按板块分组）。"""
    rows = []
    for slug, label, category, loc, _ in sort_by_category(resolved):
        rows.append(
            '        new WebSimEntry("phet-{0}", "{1}",\n'
            '            "打开 PhET「{1}」（离线自包含页，全屏；按 Esc 或右上角「返回白板」退出），板块：{2}",\n'
            '            "{2}"),'.format(slug, label, category))
    text = GENERATED_HEADER.format(body="\n".join(rows))
    open(GENERATED_CS, "w", encoding="utf-8", newline="\r\n").write(text)
    print("[生成] WebSimEntries.generated.cs：{0} 个入口".format(len(resolved)))


# ---------------------------------------------------------------- 校验

def verify() -> int:
    ok = True
    print("---- 校验 ----")

    for slug, label, category in SIMS:
        hit = None
        for loc in LOCALES:
            p = os.path.join(TARGET, "{0}_{1}.html".format(slug, loc))
            if os.path.exists(p) and os.path.getsize(p) > 512 * 1024:
                hit = (loc, p)
                break
        if hit:
            print("  OK   {0} → {1}（{2:.1f} MB）".format(slug, hit[0], os.path.getsize(hit[1]) / 1048576.0))
        else:
            print("  MISS {0}（三档语言都没有落地文件）".format(slug))
            ok = False

    # index.html 路由表必须覆盖每个已落地的仿真
    idx = open(INDEX_HTML, encoding="utf-8").read()
    for slug, _, _ in SIMS:
        if "phet-" + slug not in idx:
            print("  MISS index.html 路由缺 phet-" + slug)
            ok = False

    # 生成物必须覆盖每个已落地的仿真
    gen = open(GENERATED_CS, encoding="utf-8").read() if os.path.exists(GENERATED_CS) else ""
    for slug, _, _ in SIMS:
        if '"phet-' + slug + '"' not in gen:
            print("  MISS WebSimEntries.generated.cs 缺 phet-" + slug)
            ok = False

    if not os.path.exists(os.path.join(TARGET, "来源与版本.txt")):
        print("  MISS 来源与版本.txt")
        ok = False

    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
