# -*- coding: utf-8 -*-

"""把 dist/tablet（已验证的发布产物）按变体打成测试包 zip。



为什么要有两个包（M9 起）

----------------------------------------------------------------

「完整版」带 GeoGebra 演示面板，代价是随包再分发 WebView2 Fixed Runtime

（860 MB）+ GeoGebra 离线包（22 MB），解压后整包 1 GB 出头。

日常上课/导出验收根本用不到 GeoGebra，所以另出一个「轻量版」：

把这三样大件去掉，其余完全一致。



为什么用「过滤」而不是「重新 publish 两次」

----------------------------------------------------------------

两个包的 exe / 程序集 / 插件必须**逐字节相同**，否则"轻量版没问题、

完整版有问题"这类差异无从排查。所以这里只做一件事：

拿同一份 dist/tablet，按变体过滤后写 zip。



★ 不落任何中转目录：边遍历边写 zip 条目。避免为了打个包再占一份

  ~200 MB（轻量）/ ~1 GB（完整）的磁盘副本；也避免"删旧目录"这个动作

  （沙箱会拦批量删除，见 docs/06 §13）。



用法

----------------------------------------------------------------

    python tools/_m9_pack.py --variant lite      # 出轻量版

    python tools/_m9_pack.py --variant full      # 出完整版

    python tools/_m9_pack.py --variant both      # 两个都出



前置：dist/tablet 必须已经是最新且完整的发布产物

      （publish → 铺插件 → 补 webview2/测试样例 之后）。

"""

import argparse

import io

import os

import re

import sys

import time

import zipfile



ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))



# 变体名 -> (包文件名, 是否轻量版)

VARIANTS = {

    "full": ("白板M26-数学键盘测试包.zip", False),

    "lite": ("白板M26-数学键盘测试包-轻量版.zip", True),

}



# 轻量版去掉的顶层目录（都是"只有 GeoGebra 演示面板才用得上"的大件）

DROP_TOP = ("webview2", "geogebra", "websim")

# 轻量版去掉的其它路径（相对包根的 POSIX 路径）
# ★ M23：plugins/nativesim（物理仿真，含原生模型与网页仿真入口）也只保留在完整版
#   —— 轻量版工具栏因此少「物理仿真」一颗瓦片，smoke 锚点与操作卡同步改过。
DROP_REL = ("plugins/geogebra", "plugins/nativesim")



TESTCARD = "怎么测试.txt"

CREDIT = "第三方许可与署名.txt"



# 轻量版操作卡里要替换的锚点：(原片段, 新片段, 便于报错的名字)

# 锚点刻意取短、取唯一，命中数必须恰好 1（否则报错退出，不写 zip）。

TESTCARD_EDITS = [
    # ---- 标题 ----
    (" 数理墨 · M26 测试包（数学键盘：函数输入 / 公式 LaTeX 共用一套键盘）",
     " 数理墨 · M26 测试包 · 轻量版（数学键盘：函数输入 / 公式 LaTeX 共用一套键盘）",
     "标题"),
    # ---- 自带资源句 ----
    ("  · 本包自带 WebView2 运行时与 GeoGebra / 物理仿真离线资源，目标机【不需联网、\n"
     "    不需安装任何东西】—— 解压、双击、就能用。",
     "  · 本包不含 GeoGebra 演示面板与物理仿真（含网页仿真），所以体积小得多；\n"
     "    坐标系族、学科工具（电场线）等本版新功能**照常可用**。目标机【不需联网、\n"
     "    不需安装任何东西】—— 解压、双击、就能用。",
     "自带资源句"),
    # ---- 网页仿真修复节 → 轻量版整体没有物理仿真 ----
    ("  ★ 网页仿真（本版重点修复）：\n"
     "    · 仿真窗右下「电路」组进电路模拟器；「力学」等五个板块下是 PhET 仿真\n"
     "      （共 45 个，按板块分组，第一次打开某个仿真要等两三秒）；\n"
     "    · ★ 加载失败不再装死：资源缺失或加载超时（30 秒），屏幕上会明确写原因，\n"
     "      状态栏与日志里也有 —— 再点一次就是重试；\n"
     "    · 电路模拟器仍是**英文界面**（官方包原样）；PhET 里 44 个是简体中文、\n"
     "      1 个是繁体中文，没有重复。",
     "  · ★ 本轻量版没有物理仿真：原生单摆 / 弹簧 / 斜面与网页仿真\n"
     "    （电路模拟器 / PhET 45 个）都只在完整版里。需要时换用完整版测试包。",
     "网页仿真修复节"),
    # ---- 包很大节 ----
    ("【包很大是正常的】\n"
     "  GeoGebra 离线资源 + PhET / 电路离线包 + WebView2 运行时加起来约 1.1 GB\n"
     "  —— 这是「免安装、断网可用」的代价。",
     "【这是轻量版】\n"
     "  完整版之所以大，是 GeoGebra 离线资源 + 物理仿真离线包 + WebView2 运行时\n"
     "  加起来约 1.1 GB。本轻量版把这些大件整个去掉了，解压后小得多、启动也更快。",
     "包很大节"),
    # ---- 实测 8~11（物理仿真 / 网页仿真）→ 轻量版整节跳过 ----
    ("  [ ] 8. 点「物理仿真」瓦片（或按键盘 **P**）\n"
     "      期望：仿真窗打开，单摆**自己动起来**（不是钉在最低点）；\n"
     "      顶栏切「弹簧 / 斜面」照常；「导出到白板」把画面落到卷面\n"
     "  [ ] 9. 仿真窗右下：板块标题「电路」下有「电路模拟器（CircuitJS）」按钮\n"
     "      期望：点它 → 全屏面板出现电路模拟器（**英文界面**，官方包如此）；\n"
     "      搭一个 电池 → 开关 → 灯泡 回路，合上开关灯泡亮；按 Esc 返回白板\n"
     "  [ ] 10. 仿真窗右下：往下滚动，能看到「力学」「振动与波·声与光」「电磁学」\n"
     "      「热学与流体」「天体与近代」五个板块标题，各自下面是 PhET 仿真按钮\n"
     "      期望：共 45 个；点「力学」里的「单摆实验」→ 中文界面的钟摆实验；\n"
     "      点「电磁学」里的「电荷与电场」→ 能拖电荷看电场线\n"
     "  [ ] 11. 顺手在网页仿真里多开两个不同板块的仿真\n"
     "      期望：都能打开；★ 如果某个打开失败，屏幕上会**写明原因**（不再白屏装死）\n"
     "      —— 把提示的话抄下来",
     "  [ ] 8. （本轻量版没有物理仿真，本节跳过）\n"
     "      期望：「图形」组里**没有**「物理仿真」瓦片 —— 这不是坏了，是本版轻量版\n"
     "      刻意瘦身的（需要时换用完整版）；其余实测条目照测",
     "实测8至11"),
    # ---- 瓦片颗数（完整 17 → 轻量 15）----
    ("数一下带图标的小方块（瓦片）一共 17 颗，",
     "数一下带图标的小方块（瓦片）一共 15 颗\n"
     "      （轻量版比完整版少两颗 ——「物理仿真」与 GeoGebra，属正常），",
     "瓦片颗数"),
    # ---- 实测 21 的按 Y 说明 ----
    ("      期望：分别是「三角板·沿边画线」「坐标系」「圆弧」「GeoGebra 演示面板」",
     "      期望：分别是「三角板·沿边画线」「坐标系」「圆弧」「GeoGebra 演示面板」\n"
     "      （本轻量版不含 GeoGebra 与物理仿真：按 Y **没有反应**属正常，不是坏了）",
     "实测21按Y"),
    # ---- 插件个数与列举 ----
    ("  plugins\\                    12 个插件（直尺/量角器/三角板/坐标系/函数图像/\n"
     "                              矢量箭头/GeoGebra/公式/电路元件/圆规/物理仿真/\n"
     "                              学科工具）",
     "  plugins\\                    10 个插件（直尺/量角器/三角板/坐标系/函数图像/\n"
     "                              矢量箭头/公式/电路元件/圆规/学科工具）",
     "插件个数"),
    # ---- 署名行 ----
    ("  第三方许可与署名.txt          许可署名（GeoGebra / CircuitJS / PhET / WPF-Math，\n"
     "                              必须随包保留）",
     "  第三方许可与署名.txt          许可说明（本轻量版不含 GeoGebra 与物理仿真）",
     "署名行"),
    # ---- 内容一览收尾 ----
    ("  怎么测试.txt                 本文件",
     "  怎么测试.txt                 本文件\n"
     "\n"
     "  （本包**没有** geogebra\\、websim\\ 与 webview2\\ 三个文件夹 —— 那是完整版才带的。）",
     "内容一览收尾"),
]




# 「本包内容一览」里要整行删掉的两行（以这两个名字开头的目录行）

DROP_LINE_PATTERNS = (

    re.compile(r"^  geogebra\\"),

    re.compile(r"^  webview2\\"),

    re.compile(r"^  websim\\"),

)



LITE_CREDIT = """\

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

数理墨 —— 第三方许可与署名（轻量版）

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━



本包是「轻量版」：**不含 GeoGebra 演示面板，也不含物理仿真插件**
（原生单摆 / 弹簧 / 斜面与网页仿真都只在完整版）。

因此包内既没有 GeoGebra 离线资源，也没有 Microsoft Edge WebView2 Runtime

—— 这两样只服务于那个面板。



■ 关于 GeoGebra

----------------------------------------------------------------

  完整版（文件名里不带「轻量版」的那个）内含 GeoGebra 官方 Math Apps

  Bundle 离线包；其许可要求「仅限非商业用途 + 必须保留署名」。

  完整版内附有对应的署名文件（本文件在完整版里是另一份）。



  若将来需要商业化（出售、嵌入收费产品、由硬件厂商预装），

  必须先取得 GeoGebra 商业许可，或把该面板插件整体移除。



■ 本包实际携带的第三方组件

----------------------------------------------------------------

  · PDFium / PdfiumViewer       渲染 PDF 试卷页面

  · PdfSharpCore                导出多页 PDF

  · SixLabors.ImageSharp / Fonts  导出 PNG 时的图像与字体处理

  · ICSharpCode.SharpZipLib     工程文件（.twb）的读写

  · WPF-Math 2.1.0 (MIT)        LaTeX 公式排版（公式插件）

  · .NET 8 运行时（微软）        随包自包含分发，目标机无需另装



■ 本软件自身的批注数据

----------------------------------------------------------------

  批注（.tbink）与试卷均为用户自有数据，只保存在本机，不经过任何网络。



━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

"""





def human_mb(nbytes):

    return "%.1f" % (nbytes / 1048576.0)





def mb_int(nbytes):

    return "%.0f" % (nbytes / 1048576.0)





def collect(source):

    """遍历源目录，返回按相对路径排序的 [(绝对路径, 相对POSIX路径, 字节数)]。"""

    items = []

    for root, dirs, files in os.walk(source):

        dirs.sort()

        for f in files:

            ap = os.path.join(root, f)

            rel = os.path.relpath(ap, source).replace(os.sep, "/")

            items.append((ap, rel, os.path.getsize(ap)))

    items.sort(key=lambda x: x[1])

    return items





def is_dropped(rel):

    """该相对路径是否属于轻量版要剔除的大件。"""

    top = rel.split("/", 1)[0]

    if top in DROP_TOP:

        return True

    for d in DROP_REL:

        if rel == d or rel.startswith(d + "/"):

            return True

    return False





def _apply(text, old, new, tag):

    n = text.count(old)

    if n != 1:

        raise RuntimeError("锚点「{0}」命中 {1} 次（应为 1），无法安全替换".format(tag, n))

    return text.replace(old, new, 1)





def build_lite_testcard(full_text, unpack_mb, zip_mb):

    """由完整版操作卡派生轻量版操作卡。任何锚点对不上就抛错（不静默放过）。"""

    s = full_text

    for old, new, tag in TESTCARD_EDITS:

        new = new.replace("__UNPACK_MB__", unpack_mb).replace("__ZIP_MB__", zip_mb)

        s = _apply(s, old, new, tag)



    # 整行删掉「本包内容一览」里的 geogebra\ / webview2\ 两行

    kept = []

    for ln in s.split("\n"):

        if any(p.match(ln) for p in DROP_LINE_PATTERNS):

            continue

        kept.append(ln)

    s = "\n".join(kept)



    # 反向自检：内容一览里的两条"目录行"必须已被删掉，插件个数/列举必须已改

    leftover = [ln for ln in s.split("\n") if any(p.match(ln) for p in DROP_LINE_PATTERNS)]

    if leftover:

        raise RuntimeError("轻量版操作卡里仍残留目录行：{0}".format(leftover))

    for probe in ["  12 个插件（", "【包很大是正常的】", "点「物理仿真」瓦片（或按键盘 **P**）"]:

        if probe in s:

            raise RuntimeError("轻量版操作卡里仍残留「{0}」，替换不完整".format(probe))

    return s





def build_zip(source, zip_path, lite):

    items = collect(source)

    if not items:

        raise RuntimeError("源目录里没有文件：{0}".format(source))

    if not os.path.isfile(os.path.join(source, "MathPhys.Ink.exe")):

        raise RuntimeError("源目录里没有 MathPhys.Ink.exe：{0}".format(source))



    # 两份"随 publish 自动带出的文本"必须先在源目录里现身，缺了立刻中止。

    # ★ 为什么要在写 zip 之前查：`--variant full` 不走操作卡派生逻辑，

    #   源目录少了 怎么测试.txt 时它会安安静静打出一个**没有操作卡**的包，

    #   直到 verify() 才报 FAIL —— 那时 zip 已经落盘、并且看起来"打成功了"。

    #   （M9 收尾清理真的触发过这个风险，见 docs/06 §15.2）

    for need in (TESTCARD, CREDIT):

        if not os.path.isfile(os.path.join(source, need)):

            raise RuntimeError(

                "源目录里没有 {0}：{1}\n"

                "  这两份文本由 publish 自动带出（见 MathPhys.Ink.csproj 中\n"

                "  Assets\\{0} 的 None 条目）。缺失说明源目录不是最新的发布产物。".format(need, source))



    kept = []

    dropped_bytes = 0

    dropped_files = 0

    for ap, rel, size in items:

        if lite and is_dropped(rel):

            dropped_bytes += size

            dropped_files += 1

            continue

        kept.append((ap, rel, size))



    keep_bytes = sum(x[2] for x in kept)



    # 轻量版的替代文本（写 zip 时按名字替换掉源文件内容）

    replacement = {}

    t0 = time.time()



    def _write():

        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:

            for ap, rel, size in kept:

                if rel in replacement:

                    z.writestr(rel, replacement[rel])

                else:

                    z.write(ap, rel)



    if lite:

        full_text = io.open(os.path.join(source, TESTCARD), encoding="utf-8").read()

        replacement[CREDIT] = LITE_CREDIT.encode("utf-8")



        # 两遍写：操作卡里要写「压缩包多大」，而那个数字只有压完才知道。

        # 压缩只要几秒，所以先写一遍量出真实大小，再用它重写一遍。

        replacement[TESTCARD] = build_lite_testcard(

            full_text, mb_int(keep_bytes), "?待定").encode("utf-8")

        _write()

        zip_mb = mb_int(os.path.getsize(zip_path))

        replacement[TESTCARD] = build_lite_testcard(

            full_text, mb_int(keep_bytes), zip_mb).encode("utf-8")

        _write()

    else:

        _write()



    elapsed = time.time() - t0



    return {

        "items": kept,

        "dropped_files": dropped_files,

        "dropped_bytes": dropped_bytes,

        "keep_bytes": keep_bytes,

        "elapsed": elapsed,

        "replacement": replacement,

    }





def verify(source, zip_path, lite, info):

    print("---- 校验 ----")

    fails = []



    def check(ok, label, extra=""):

        print("  [{0}] {1}{2}".format("PASS" if ok else "FAIL", label,

                                      ("  " + extra) if extra else ""))

        if not ok:

            fails.append(label)



    if not os.path.isfile(zip_path):

        check(False, "zip 已生成")

        return fails

    zsize = os.path.getsize(zip_path)

    check(True, "zip 已生成", "%.1f MB" % (zsize / 1048576.0))



    with zipfile.ZipFile(zip_path) as z:

        infos = [i for i in z.infolist() if not i.is_dir()]

        names = {i.filename for i in infos}

        top = [n.split("/")[0] for n in names]



        check("MathPhys.Ink.exe" in names, "顶层直接见 exe")

        check(not any(n.startswith("tablet/") for n in names), "无 tablet/ 套层")

        check(not any("gfxdemo" in n.lower() or "PdfSmokeTest" in n for n in names),

              "无验收用插件混入")



        if lite:

            bad = [n for n in names if is_dropped(n)]

            check(not bad, "轻量版已剔除 geogebra/ webview2/ websim/ plugins/geogebra",

                  "残留 %d" % len(bad))

        else:

            check("webview2" in top and "geogebra" in top and "websim" in top,

                  "完整版含 webview2/ geogebra/ websim/")



        for need in ["plugins/ruler/MathPhys.Ink.Plugin.Ruler.dll",

                     "plugins/vectorarrow/vectorarrow.dll",

                     "plugins/formula/formula.dll",

                     # M15 教训：插件依赖的 NuGet dll 必须随包（缺了就"一输公式就解析失败"）

                     "plugins/formula/WpfMath.dll",

                     "plugins/formula/XamlMath.Shared.dll",

                     "plugins/circuitkit/circuitkit.dll",

                     "plugins/compass/compass.dll",

                     "plugins/说明.txt",

                     "测试样例/外挂工程样例.twb",

                     "测试样例/测试用小试卷.pdf",

                     "第三方许可与署名.txt",

                     "怎么测试.txt",

                     ]:

            check(need in names, "含 " + need)



        check("plugins/geogebra" not in top, "顶层无 plugins/geogebra 目录") if lite else None



        # 逐条对账：zip 文件集 == (源文件集 - 剔除项)

        expected = {rel for _, rel, _ in info["items"]}

        check(expected == names, "包内文件集 == 过滤后的源文件集",

              "缺 %d / 多 %d" % (len(expected - names), len(names - expected)))



        # 体积对账：除被替换的两份文本，其余条目原始字节数必须一致

        src_size = {rel: size for _, rel, size in info["items"]}

        mism = []

        for i in infos:

            if i.filename in info["replacement"]:

                continue

            if src_size.get(i.filename) != i.file_size:

                mism.append(i.filename)

        check(not mism, "非替换条目字节数逐条一致", "不符 %d" % len(mism))



        # 抽样哈希

        samples = ["MathPhys.Ink.exe",

                   "plugins/ruler/MathPhys.Ink.Plugin.Ruler.dll",

                   "plugins/vectorarrow/vectorarrow.dll",

                   "测试样例/外挂工程样例.twb"]

        import hashlib

        for s in samples:

            if s not in names:

                continue

            a = hashlib.sha256(z.read(s)).hexdigest()[:16]

            b = hashlib.sha256(open(os.path.join(source, s), "rb").read()).hexdigest()[:16]

            check(a == b, "哈希一致 " + s, a)



        if lite:

            tc = z.read(TESTCARD).decode("utf-8")

            dir_lines = [ln for ln in tc.split("\n")

                         if ln.startswith(("  geogebra\\", "  websim\\", "  webview2\\"))]

            check(" 数理墨 · M26 测试包 · 轻量版（数学键盘：函数输入 / 公式 LaTeX 共用一套键盘）" in tc

                  and "【这是轻量版】" in tc and not dir_lines,

                  "操作卡已是轻量版措辞", "残留目录行 %d" % len(dir_lines))

            cr = z.read(CREDIT).decode("utf-8")

            check("轻量版" in cr, "署名文件已是轻量版")



    return fails





def main():

    parser = argparse.ArgumentParser()

    parser.add_argument("--source", default="dist/tablet", help="发布产物目录")

    parser.add_argument("--variant", default="lite", choices=["lite", "full", "both"])

    parser.add_argument("--stage", default="dist", help="zip 输出目录")

    args = parser.parse_args()



    source = os.path.join(ROOT, args.source) if not os.path.isabs(args.source) else args.source

    stage = os.path.join(ROOT, args.stage) if not os.path.isabs(args.stage) else args.stage



    variants = ["lite", "full"] if args.variant == "both" else [args.variant]

    all_fails = []



    for v in variants:

        name, lite = VARIANTS[v]

        zip_path = os.path.join(stage, name)

        print("=" * 64)

        print("变体 {0}（{1}） -> {2}".format(v, "轻量版" if lite else "完整版", name))

        print("=" * 64)

        try:

            info = build_zip(source, zip_path, lite)

        except Exception as ex:

            print("  [FAIL] 打包失败：{0}".format(ex))

            all_fails.append("{0} 打包失败".format(v))

            continue

        print("  过滤后文件数 = {0}（原 {1}，剔除 {2} 个 / {3} MB）".format(

            len(info["items"]),

            len(info["items"]) + info["dropped_files"],

            info["dropped_files"], human_mb(info["dropped_bytes"])))

        print("  未压缩体积 = {0} MB   压缩用时 {1:.1f}s".format(

            human_mb(info["keep_bytes"]), info["elapsed"]))

        fails = verify(source, zip_path, lite, info)

        all_fails.extend("{0}:{1}".format(v, f) for f in fails)

        print()



    if all_fails:

        print("结果：FAIL（{0} 项）".format(len(all_fails)))

        for f in all_fails:

            print("   -", f)

        return 1

    print("结果：全部 PASS")

    return 0





if __name__ == "__main__":

    sys.exit(main())

