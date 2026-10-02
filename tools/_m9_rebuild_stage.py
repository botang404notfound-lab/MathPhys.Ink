# -*- coding: utf-8 -*-
"""一键重建打包暂存目录 dist/tablet（M9 收尾清理后补）。

为什么需要它
----------------------------------------------------------------
M9 收尾按用户要求把 dist/ 清到只剩两个交付 zip，于是打包暂存目录
dist/tablet 也一并删了。以后要再出包，必须能**从零重建**它。

重建共四步，其中第 3 步是真正的坑：
  webview2\\ —— 微软 WebView2 **Fixed Runtime**（860 MB，917 个文件），
  随完整版包分发用。它在仓库里**没有副本**（不在 publish 输出里、不在
  Assets\\ 里），唯一留存处就是已交付的完整版 zip 内。漏了这一步，
  打出来的完整版包会缺运行时，要等到一体机上才暴露。

四步
----------------------------------------------------------------
  1. publish     自包含发布主工程                    -> dist/tablet
  2. 铺插件      tools/_stage_plugins.py（★ publish **不刷** plugins\\，
                 插件不在宿主引用图里 —— 见 docs/06 §8.15）
  3. webview2    从已交付的完整版 zip 取回 webview2\\ 放到暂存目录
  4. 测试样例    tools/_m9s4_make_samples.py（.twb 里存着小试卷指纹，
                 必须与 PDF 现造配对，不能从旧包里抄）

跑完即可打两个包：
    python tools/_m9_pack.py --variant both

用法
----------------------------------------------------------------
    python tools/_m9_rebuild_stage.py                  # 全跑四步
    python tools/_m9_rebuild_stage.py --skip-publish   # 跳过第 1 步（复用现有输出）
    python tools/_m9_rebuild_stage.py --probe 6        # 只试取 6 个 webview2 成员到临时目录
                                                       # （验证筛选与解包路径，不落 860 MB；
                                                       #  只跑第 3 步，不碰 publish/铺插件/样例）
    python tools/_m9_rebuild_stage.py --webview2-from <目录或 zip>
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STAGE_REL = os.path.join("dist", "tablet")
# ★ 跟着"当前交付的完整版包"走。M10 起是 白板M10-界面测试包.zip；
#   M9 包已归档到 dist\_历史版本\，不要再指回去（那会让这次重建找不到源）。
#   ★ 每次收尾把上一版包挪进 %TEMP% 之后，这里必须跟着换成**新的**完整版包，
#     否则下一次重建会报"找不到 webview2 来源"（webview2 不可再生，只有包里有）。
DEFAULT_WEBVIEW2_FROM = os.path.join("dist", "白板M26-数学键盘测试包.zip")

WV_TOP = "webview2/"

# 重建完成后必须存在的条目（相对暂存目录）
MUST_HAVE_FILES = [
    "MathPhys.Ink.exe",
    "MathPhys.Ink.dll",
    "怎么测试.txt",
    "第三方许可与署名.txt",
    "plugins/说明.txt",
    "plugins/ruler/MathPhys.Ink.Plugin.Ruler.dll",
    "plugins/vectorarrow/vectorarrow.dll",
    "plugins/formula/formula.dll",
    "plugins/circuitkit/circuitkit.dll",
    "plugins/compass/compass.dll",
    "测试样例/外挂工程样例.twb",
    "测试样例/测试用小试卷.pdf",
    "测试样例/说明.txt",
]
MUST_HAVE_DIRS = ["plugins", "geogebra", "webview2"]


def log(msg=""):
    print(msg, flush=True)


def count_files(path):
    if not os.path.isdir(path):
        return 0
    return sum(len(fs) for _, _, fs in os.walk(path))


def run(cmd):
    """跑子进程并把输出透传；失败抛错（不吞返回值）。"""
    log("    $ " + " ".join(cmd))
    env = dict(os.environ)
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"   # 中文环境会把 warning/error 字样本地化，日志不便比对
    env["PYTHONIOENCODING"] = "utf-8"
    proc = subprocess.run(cmd, cwd=ROOT, env=env)
    if proc.returncode != 0:
        raise RuntimeError("命令失败（exit={0}）：{1}".format(proc.returncode, " ".join(cmd)))


def step_publish(stage):
    log("[1/4] publish 自包含发布 -> {0}".format(STAGE_REL))
    if os.path.isdir(stage) and os.listdir(stage):
        log("    ⚠ 目标目录已存在且非空（{0} 项）：publish **只增不删**，陈旧文件会留在暂存目录里，"
            .format(len(os.listdir(stage))))
        log("      进而混进包内（包内文件集就不再等于源文件集）。要干净重建请先把它改名挪走。")
    run(["dotnet", "publish",
         os.path.join("src", "MathPhys.Ink", "MathPhys.Ink.csproj"),
         "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "--no-restore", "-o", STAGE_REL])
    log("    publish 完成，文件数 = {0}".format(count_files(stage)))


def step_plugins(stage):
    log("[2/4] 铺插件（铁律 §8.15：publish 不刷 plugins\\）")
    run([sys.executable, os.path.join("tools", "_stage_plugins.py"),
         "--out", STAGE_REL, "--config", "Release"])
    subs = sorted(os.listdir(os.path.join(stage, "plugins")))
    log("    plugins\\ 子项 = {0}".format(subs))


def resolve_webview2_source(arg):
    p = arg if os.path.isabs(arg) else os.path.join(ROOT, arg)
    if not os.path.exists(p):
        raise RuntimeError(
            "找不到 webview2 来源：{0}\n"
            "  webview2\\ 不可再生，只能从已交付的完整版包目录或 zip 里取。".format(p))
    return p


def step_webview2(stage, src, probe):
    log("[3/4] 取回 webview2 Fixed Runtime（不可再生资产）")
    log("    来源 = {0}".format(os.path.relpath(src, ROOT) if src.startswith(ROOT) else src))

    dst_root = os.path.join(stage)
    if probe:
        # 试跑：只取前 N 个成员到临时目录，验证筛选逻辑，不落 860 MB
        dst_root = tempfile.mkdtemp(prefix="twb_wv_probe_")
        log("    试跑模式：只取前 {0} 个成员 -> {1}".format(probe, dst_root))

    if os.path.isdir(src):
        # 来源是解压好的包目录：直接把 webview2\ 整棵拷过去
        s = os.path.join(src, "webview2")
        if not os.path.isdir(s):
            raise RuntimeError("来源目录里没有 webview2\\：{0}".format(s))
        d = os.path.join(dst_root, "webview2")
        if os.path.isdir(d) and not probe:
            log("    目标已存在，跳过（文件数 = {0}）".format(count_files(d)))
            return
        if probe:
            n = 0
            for root, dirs, files in os.walk(s):
                rel = os.path.relpath(root, s)
                for f in sorted(files):
                    t = os.path.join(d, rel, f) if rel != "." else os.path.join(d, f)
                    os.makedirs(os.path.dirname(t), exist_ok=True)
                    shutil.copy2(os.path.join(root, f), t)
                    log("      取 {0}".format(os.path.join("webview2", rel, f).replace("\\", "/")))
                    n += 1
                    if n >= probe:
                        break
                if n >= probe:
                    break
            log("    试跑完成：{0} 个成员，验证筛选与路径拼接正确".format(n))
            return
        shutil.copytree(s, d)
        log("    拷贝完成，文件数 = {0}".format(count_files(d)))
        return

    # 来源是 zip
    with zipfile.ZipFile(src) as z:
        members = [i for i in z.infolist() if not i.is_dir() and i.filename.startswith(WV_TOP)]
        if not members:
            raise RuntimeError("这个 zip 里没有 {0} 条目：{1}".format(WV_TOP, src))
        log("    来源内 webview2 条目数 = {0}".format(len(members)))
        if probe:
            for i in members[:probe]:
                z.extract(i, dst_root)
                log("      取 {0}".format(i.filename))
            log("    试跑完成：{0} 个成员，验证筛选与解包路径正确".format(min(probe, len(members))))
            return
        d = os.path.join(dst_root, "webview2")
        if os.path.isdir(d) and os.listdir(d):
            log("    目标已存在，跳过（文件数 = {0}）".format(count_files(d)))
            return
        for i in members:
            z.extract(i, dst_root)
    got = count_files(os.path.join(dst_root, "webview2"))
    if got != len(members):
        raise RuntimeError("解包数不符：期望 {0}，实际 {1}".format(len(members), got))
    log("    解包完成，文件数 = {0}".format(got))


def step_samples(stage):
    log("[4/4] 造包内测试样例")
    run([sys.executable, os.path.join("tools", "_m9s4_make_samples.py")])
    d = os.path.join(stage, "测试样例")
    log("    测试样例 = {0}".format(sorted(os.listdir(d)) if os.path.isdir(d) else "(缺失!)"))


def final_check(stage, probe):
    log()
    log("=== 暂存目录自检 ===")
    if not os.path.isdir(stage):
        raise RuntimeError("暂存目录不存在：{0}".format(stage))
    fails = []
    for rel in MUST_HAVE_FILES:
        ok = os.path.isfile(os.path.join(stage, rel))
        log("  [{0}] {1}".format("OK  " if ok else "MISS", rel))
        if not ok and not probe:
            fails.append(rel)
    for rel in MUST_HAVE_DIRS:
        ok = os.path.isdir(os.path.join(stage, rel))
        log("  [{0}] {1}\\".format("OK  " if ok else "MISS", rel))
        if not ok and not probe:
            fails.append(rel + "\\")
    log("  文件总数 = {0}".format(count_files(stage)))
    if probe:
        log("  （试跑模式，缺件属预期，不做判定）")
        return
    if fails:
        raise RuntimeError("暂存目录不完整，缺：{0}".format("、".join(fails)))
    log("  结论：暂存目录完整，可以打包")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-publish", action="store_true", help="跳过第 1 步 publish")
    ap.add_argument("--webview2-from", default=DEFAULT_WEBVIEW2_FROM,
                    help="webview2 来源（包目录或 zip）")
    ap.add_argument("--probe", type=int, default=0,
                    help="试跑：只取 N 个 webview2 成员到临时目录，不落 860 MB")
    args = ap.parse_args()

    stage = os.path.join(ROOT, STAGE_REL)
    log("重建打包暂存目录 {0}".format(STAGE_REL))
    log()

    # ★ 试跑要放在最前面：它只验证第 3 步的筛选/解包逻辑，不该顺带 publish
    #   （否则会凭空落一个 180 MB 的暂存目录，与"只留交付包"的清理目的相悖）。
    if args.probe:
        log("[试跑] 只验证第 3 步（webview2 取回）的筛选与解包逻辑；不跑 publish / 铺插件 / 样例")
        step_webview2(stage, resolve_webview2_source(args.webview2_from), args.probe)
        log()
        log("试跑结束（未触碰 {0}）".format(STAGE_REL))
        return 0

    if not args.skip_publish:
        step_publish(stage)
    else:
        log("[1/4] 跳过 publish（--skip-publish）")
    log()

    step_plugins(stage)
    log()
    step_webview2(stage, resolve_webview2_source(args.webview2_from), 0)
    log()
    step_samples(stage)
    final_check(stage, False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
