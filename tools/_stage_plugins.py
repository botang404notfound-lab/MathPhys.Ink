# -*- coding: utf-8 -*-
"""把各插件工程的构建产物铺到目标程序的 plugins\\ 目录下。

为什么必须有这一步：`dotnet build` / `publish` **不会**刷新 `plugins\\` ——
插件不在宿主的引用图里（宿主是运行时按目录加载的）。漏了这一步的表现是
"插件明明编译过了、程序里就是没有那个工具"，而且日志里一片正常。

用法：
    python tools/_stage_plugins.py --out <程序目录> --config Release [--with-gfxdemo]

行为：
  1. 清空 <程序目录>\\plugins 下的**插件子目录**（保留 说明.txt 与 _ 开头的停用目录）；
  2. 按映射表把每个插件 dll 复制到 plugins\\<名>\\；
  3. 回读校验（每个目录里确实有那个 dll，且字节数与源一致）。
"""
import argparse
import io
import os
import shutil
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# （插件目录名, 工程相对路径, 主 dll 文件名）
PLUGINS = [
    ("ruler", "src/MathPhys.Ink.Plugin.Ruler", "MathPhys.Ink.Plugin.Ruler.dll"),
    ("protractor", "src/MathPhys.Ink.Plugin.Protractor", "protractor.dll"),
    ("triangle", "src/MathPhys.Ink.Plugin.Triangle", "triangle.dll"),
    ("coordsystem", "src/MathPhys.Ink.Plugin.CoordSystem", "coordsystem.dll"),
    ("functionplot", "src/MathPhys.Ink.Plugin.FunctionPlot", "functionplot.dll"),
    ("vectorarrow", "src/MathPhys.Ink.Plugin.VectorArrow", "vectorarrow.dll"),
    ("geogebra", "src/MathPhys.Ink.Plugin.GeoGebra", "MathPhys.Ink.Plugin.GeoGebra.dll"),
    ("formula", "src/MathPhys.Ink.Plugin.Formula", "formula.dll"),
    ("circuitkit", "src/MathPhys.Ink.Plugin.CircuitKit", "circuitkit.dll"),
    ("compass", "src/MathPhys.Ink.Plugin.Compass", "compass.dll"),
    # M22 S3：物理仿真（原生运动学）—— 单摆 / 弹簧振子 / 斜面的独立 WPF 仿真窗。
    # ★ 两个变体都带它：它零 NuGet 依赖、也不引宿主，轻量版里照样能跑
    # （轻量版只是把窗里的「网页仿真」入口置灰）。
    ("nativesim", "src/MathPhys.Ink.Plugin.NativeSim", "nativesim.dll"),
    # M23 S4：学科工具（图片资料库）—— 电场线等插图，点图落到卷面（宿主 AddImage 通道）。
    ("subjectkit", "src/MathPhys.Ink.Plugin.SubjectKit", "subjectkit.dll"),
]

# 验收用插件（只在需要时铺；正式交付包里**必须没有**它）
GFXDEMO = ("gfxdemo", "tools/PdfSmokeTest.GfxPlugin", "PdfSmokeTest.GfxPlugin.dll")

# 插件目录里**不**该有契约 dll：它由宿主共享给所有插件，
# 每个插件目录再放一份会造成"两个同名的 IWhiteBoardPlugin"这类身份问题。
SHARED_EXCLUDE = {"MathPhys.Ink.Plugin.Abstractions.dll"}


def find_output_dir(project_rel, config):
    """插件工程的输出目录（有的带 win-x64 子目录，有的不带，都要能找）。"""
    base = os.path.join(ROOT, project_rel, "bin", config, "net8.0-windows")
    for candidate in (os.path.join(base, "win-x64"), base):
        if os.path.isdir(candidate):
            return candidate
    return None


def newest_source_mtime(project_rel):
    """插件工程里最新的源文件修改时间（排除 bin/obj）。

    ★ 为什么需要它：`dotnet build MathPhys.Ink.sln -c Release` **不能保证**
    每个插件都产到 bin\\Release\\ —— 有两个工程（VectorArrow、
    PdfSmokeTest.Plugin）压根**不在 sln 里**，只是被 harness 用 ProjectReference
    带出来的；实测在这种情况下 Configuration 没传下去，SDK 回落到默认
    **Debug**，产物落进 bin\\Debug\\，而 bin\\Release\\ 里躺的是**上一次**的旧 dll。
    `dotnet build` 的输出行照样打印 `... -> ...Release...dll`（增量判定为最新时
    也会打印目标路径），所以"看日志"完全看不出来 —— 只有比时间戳才看得见。
    （2026-09-26 M20 收尾时实测到：10 个插件里只有 VectorArrow 与
    PdfSmokeTest.Plugin 被编进 Debug，其余 9 个因为源未变、增量判定最新而正常。）
    """
    newest = 0.0
    for root, dirs, files in os.walk(os.path.join(ROOT, project_rel)):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for f in files:
            if f.endswith((".cs", ".xaml", ".csproj", ".resx")):
                newest = max(newest, os.path.getmtime(os.path.join(root, f)))
    return newest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="目标程序目录（其下有 plugins\\）")
    parser.add_argument("--config", default="Release", choices=["Debug", "Release"])
    parser.add_argument("--with-gfxdemo", action="store_true", help="额外铺验收用 gfxdemo 插件")
    args = parser.parse_args()

    target_root = os.path.join(ROOT, args.out) if not os.path.isabs(args.out) else args.out
    plugins_root = os.path.join(target_root, "plugins")

    if not os.path.isdir(target_root):
        print("目标程序目录不存在：{0}".format(target_root))
        return 1

    os.makedirs(plugins_root, exist_ok=True)

    print("目标 plugins 目录：{0}".format(plugins_root))

    # ---- 1) 清空插件子目录（保留 说明.txt 与 _ 开头的停用目录）----
    removed = 0
    for name in os.listdir(plugins_root):
        full = os.path.join(plugins_root, name)
        if not os.path.isdir(full):
            continue
        if name.startswith("_"):
            print("  保留停用目录：{0}".format(name))
            continue
        shutil.rmtree(full)
        removed += 1

    print("  清空旧插件子目录 {0} 个".format(removed))

    # ---- 2) 复制 ----
    wanted = list(PLUGINS) + ([GFXDEMO] if args.with_gfxdemo else [])
    staged = []
    failed = []

    for name, project_rel, dll_name in wanted:
        source_dir = find_output_dir(project_rel, args.config)
        if source_dir is None:
            failed.append("{0}（找不到 {1} 的构建输出）".format(name, project_rel))
            continue

        source_dll = os.path.join(source_dir, dll_name)
        if not os.path.isfile(source_dll):
            failed.append("{0}（缺少 {1}）".format(name, dll_name))
            continue

        # 只带插件自己的 dll；契约 dll 由宿主提供
        target_dir = os.path.join(plugins_root, name)
        os.makedirs(target_dir, exist_ok=True)

        copied = 0
        for entry in os.listdir(source_dir):
            if not entry.endswith(".dll"):
                continue
            if entry in SHARED_EXCLUDE:
                continue
            shutil.copy2(os.path.join(source_dir, entry), os.path.join(target_dir, entry))
            copied += 1

        staged.append((name, project_rel, dll_name, copied,
                       os.path.getsize(source_dll), os.path.getmtime(source_dll)))

    # ---- 3) 回读校验 + 新鲜度校验 ----
    print("  --- 回读校验 ---")
    ok = True
    stale = []
    for name, project_rel, dll_name, copied, size, dll_mtime in staged:
        target = os.path.join(plugins_root, name, dll_name)
        exists = os.path.isfile(target)
        same = exists and os.path.getsize(target) == size
        src_mtime = newest_source_mtime(project_rel)
        fresh = dll_mtime >= src_mtime
        if not (exists and same and fresh):
            ok = False
        print("  [{0}] {1}\\{2}  dll={3}  依赖={4}  {5} bytes  新鲜度={6}".format(
            "PASS" if exists and same and fresh else "FAIL",
            name, dll_name, "有" if exists else "缺", copied, size,
            "OK" if fresh else "落后于源码"))
        if not fresh:
            stale.append((name, project_rel, dll_name,
                          time.strftime("%m-%d %H:%M:%S", time.localtime(dll_mtime)),
                          time.strftime("%m-%d %H:%M:%S", time.localtime(src_mtime))))

    if stale:
        print()
        print("  ★ 以下插件的 bin\\{0}\\ 产物**落后于源码**，铺出去就是旧行为：".format(args.config))
        for name, project_rel, dll_name, dm, sm in stale:
            print("      {0:<14} dll={1}  源码={2}".format(name, dm, sm))
        print("    修法（对每个落后的工程单独跑，绕过 sln 的配置映射）：")
        for name, project_rel, _dll, _dm, _sm in stale:
            print("      dotnet build {0}.csproj -c {1}".format(
                project_rel.replace("/", "\\"), args.config))
        print("    为什么会发生：不在 sln 里的工程（VectorArrow / PdfSmokeTest.Plugin）")
        print("    经 ProjectReference 构建时 Configuration 可能没传下去，SDK 回落到 Debug。")

    for item in failed:
        ok = False
        print("  [FAIL] {0}".format(item))

    print("铺插件完成：{0} 个插件，校验 {1}".format(len(staged), "通过" if ok else "失败"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
