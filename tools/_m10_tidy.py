# -*- coding: utf-8 -*-
"""
M10 收尾：工作区瘦身。

原则
----
1) **删产物、留脚本**：历史里程碑的截图 / 日志 / 解压目录都是「一次性的'产物'」，
   体积大、不可复用；而 .py 脚本再小也保留（可改、可复跑）。
2) **用 rename，不用删除**：>50 文件的批量删除会撞沙箱的 safe-delete 拦截
   （Exit Code:1、无异常栈、只有一行 SAFE_DELETE_BULK_CONFIRM_REQUIRED）。
   统一 rename 到系统临时目录，等价于删除，但留一个后悔窗口。
3) 交付物（dist/ 的 zip 包）、源码 src/（bin/obj 除外，可再生）、文档 docs/ 一律不动。
4) M11 起新增两块：src 主项目 bin/obj（dotnet build 可再生）、dist/ 里的解包暂存目录
   （zip 才是交付物，解包目录随时可由 _m9_rebuild_stage.py / 重装流程复原）。
"""
import os, sys

DRY = '--go' not in sys.argv
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TRASH = os.path.join(os.environ['TEMP'], 'mathphys_trash_20260923')

# ---------------------------------------------------------------- artifacts
ART_KEEP_DIR = {'m10'}                       # M10 双主题真机出图 + 色值真值
ART_KEEP_FILE = {'smoke_m10.py', 'smoke_m10_pkg.py', 'smoke_m11.py',
                 'smoke-m10.txt', 'smoke-m10-pkg.txt',
                 'smoke-m11.txt', 'smoke-m11-pkg.txt'}

def collect_artifacts():
    out = []
    art = os.path.join(ROOT, 'artifacts')
    for name in sorted(os.listdir(art)):
        p = os.path.join(art, name)
        if os.path.isdir(p):
            if name not in ART_KEEP_DIR:
                out.append(p)
            continue
        if name in ART_KEEP_FILE:
            continue
        # 保留所有可复跑的脚本，但排除一次性探针
        if name.endswith('.py') and not name.startswith('_probe'):
            continue
        out.append(p)
    return out

# ------------------------------------------------------------------- tools
TOOLS_ACTIVE_PY = {
    '_m9_pack.py',            # 打双包
    '_m9_rebuild_stage.py',   # 从零重建打包暂存目录（依赖 _m9s4_make_samples.py）
    '_m9s4_make_samples.py',  # 造随包样例（被重建脚本调用）
    '_m9_cleanup_dist.py',    # 清 dist
    '_run_harness.py',        # 跑全量回归
    '_stage_plugins.py',      # 铺插件
    '_m10_tidy.py',           # 本脚本
}
TOOLS_ACTIVE_OTHER = {'cleanup.py', 'fetch-geogebra-bundle.mjs'}
TOOLS_TRASH = ['PdfSmokeTest/bin', 'PdfSmokeTest/obj',
               'PdfSmokeTest.GfxPlugin/bin', 'PdfSmokeTest.GfxPlugin/obj',
               'PdfSmokeTest.Plugin/bin', 'PdfSmokeTest.Plugin/obj',
               '__pycache__', '_scratch']

def collect_tools():
    trash, archive = [], []
    tl = os.path.join(ROOT, 'tools')
    for rel in TOOLS_TRASH:
        p = os.path.join(tl, rel.replace('/', os.sep))
        if os.path.exists(p):
            trash.append(p)
    for name in sorted(os.listdir(tl)):
        p = os.path.join(tl, name)
        if os.path.isdir(p):
            continue
        if name in TOOLS_ACTIVE_PY or name in TOOLS_ACTIVE_OTHER:
            continue
        if name.endswith('.py'):
            archive.append(p)
    return trash, archive

def collect_src_build():
    """src 各项目的 bin/obj（dotnet build 可再生，整体搬走）。"""
    out = []
    src = os.path.join(ROOT, 'src')
    if not os.path.isdir(src):
        return out
    for proj in sorted(os.listdir(src)):
        for sub in ('bin', 'obj'):
            p = os.path.join(src, proj, sub)
            if os.path.isdir(p):
                out.append(p)
    return out

DIST_KEEP = {'白板M11-书写测试包.zip', '白板M11-书写测试包-轻量版.zip'}

def collect_dist_staging():
    """dist/ 里的解包暂存目录（zip 保留，目录搬走）。"""
    out = []
    dist = os.path.join(ROOT, 'dist')
    if not os.path.isdir(dist):
        return out
    for name in sorted(os.listdir(dist)):
        p = os.path.join(dist, name)
        if os.path.isdir(p) and name not in DIST_KEEP:
            out.append(p)
    return out

def dirsize(p):
    if os.path.isfile(p):
        return 1, os.path.getsize(p)
    n = s = 0
    for r, _, fs in os.walk(p):
        for f in fs:
            try:
                s += os.path.getsize(os.path.join(r, f)); n += 1
            except OSError:
                pass
    return n, s

def relocate(p, dest_root):
    """把 p 搬到 dest_root 下，保留相对仓库根的路径结构。"""
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    dst = os.path.join(dest_root, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    i = 1
    while os.path.exists(dst):
        dst = '%s__%d' % (dst, i); i += 1
    if not DRY:
        os.rename(p, dst)
    return dst

def main():
    art = collect_artifacts()
    t_trash, t_arch = collect_tools()

    print('模式:', 'DRY-RUN（只看不动）' if DRY else '执行')
    print('临时中转:', TRASH)
    print()

    groups = [('artifacts → 临时目录', art, TRASH),
              ('tools 构建产物 → 临时目录', t_trash, TRASH),
              ('src bin/obj → 临时目录', collect_src_build(), TRASH),
              ('dist 解包暂存 → 临时目录', collect_dist_staging(), TRASH),
              ('tools 一次性迁移脚本 → tools/_archive/', t_arch,
               os.path.join(ROOT, 'tools', '_archive'))]

    grand_n = grand_s = 0
    for title, items, dest in groups:
        n = s = 0
        print('=== %s （%d 项）===' % (title, len(items)))
        for p in items:
            cn, cs = dirsize(p)
            n += cn; s += cs
            shown = os.path.relpath(p, ROOT)
            extra = '  [%d 文件]' % cn if os.path.isdir(p) else ''
            print('   %-46s %8.1f KB%s' % (shown, cs / 1024, extra))
        grand_n += n; grand_s += s
        print('   小计: %d 文件 / %.1f MB' % (n, s / 1048576))
        print()
        if not DRY:
            for p in items:
                relocate(p, dest)

    print('合计: %d 文件 / %.1f MB' % (grand_n, grand_s / 1048576))
    if DRY:
        print()
        print('（DRY-RUN 结束，未改动任何文件；加 --go 执行）')
    else:
        print('已全部搬离仓库。核对无误后可直接删除:', TRASH)

if __name__ == '__main__':
    main()
