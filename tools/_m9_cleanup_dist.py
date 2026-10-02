# -*- coding: utf-8 -*-
"""收尾：清理 dist/ —— 只保留最新里程碑（M19）的双包。

安全护栏（任一不满足就整脚本中止，绝不静默放过）：
  1. 删除项必须在 <项目根>/dist 之下（按绝对路径校验，防目录穿越）；
  2. 删除项不得在 KEEP 保留名单里；
  3. 删除项不得是 dist 自身；
  4. 每删一项都按 KEEP 名单实时复检一次。

用法：
  python tools/_m9_cleanup_dist.py --dry-run    # 只列清单与体积，不删
  python tools/_m9_cleanup_dist.py --go         # 真删
"""
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(HERE)
DIST = os.path.join(PROJ, "dist")

# 保留名单：只有两个 M20 测试包（只保留最新里程碑的双包是既定惯例）。
# ★ _历史版本/ 是「归档容器」而不是交付物 —— 用户 2026-09-23 点名清空后，
#   它不再进 KEEP（否则 KEEP 复检会因目录不存在而中止），
#   改为「真有归档项要搬时才按需创建」，不再每次运行都把它建出来。
KEEP = {
    "数理墨-v0.1.0-完整版.zip",
    "数理墨-v0.1.0-轻量版.zip",
}

# 归档项：被新版本取代、但还想留一份可回退的整包 —— **移进 _历史版本/**（不删）
# 归档项：空 —— _历史版本/ 已按用户 2026-09-23 的决定清空；被取代的包
# 由当轮收尾脚本挪进 %TEMP%\mathphys_trash_*（可反悔），不再重建归档区。
ARCHIVE = []

# 待删清单（相对 dist/ 的名字）
TARGETS = [
    "tablet",                              # publish 暂存
    "白板M9-导出测试包",                      # 解压出来的整份副本
    "白板M9-导出测试包-轻量版",                 # 解压出来的轻量版副本（M9 验收时落的）
    "白板M8-工程文件测试包",                   # 上一里程碑包目录
    "白板M8-工程文件测试包.zip",
    "白板M7.5-GeoGebra测试包",                # 更早一版包目录
    "白板M7.5-GeoGebra测试包.zip",
    "白板M7.4-Final测试包",
    "白板M7.4-Final测试包.zip",
    "白板M7.4-Final测试包_template_howto.txt",
    "白板M7.4-S5测试包",
    "白板M7.4-S5测试包.zip",
    "_stale_tablet_m74",                   # 上次改名留下的旧暂存
    "白板M12-工作流测试包",                  # M12 打包留下的暂存目录
    "白板M12-工作流测试包-轻量版",             # M12 打包留下的轻量版暂存目录
    "白板M17-三角板沿边画线测试包",            # M17 打包留下的暂存目录（zip 已挪走）
    "白板M17-三角板沿边画线测试包-轻量版",       # M17 打包留下的轻量版暂存目录
    "白板M18-悬浮球流畅与笔迹优化测试包-轻量版",  # M18 验收解压出来的整份副本
    "_stale_tablet_m181",                  # M18.1 重建暂存前挪开的旧暂存（§13.1 改名法）
    "白板M24-书写修复测试包-轻量版",           # M24 包本体冒烟解压出来的轻量版副本
    # v0.1.0（改名后首版）：上一版双包的旧名字与解压残留
    "白板M26-数学键盘测试包.zip",              # 上一版完整包（webview2 已移植进新版完整包）
    "白板M26-数学键盘测试包-轻量版.zip",        # 上一版轻量包
    "白板M26-数学键盘测试包-轻量版",            # 上一版轻量包解压出来的副本
]


def mb(n):
    return "%.1f MB" % (n / 1048576.0)


def tree_size(path):
    total = 0
    files = 0
    for root, _dirs, names in os.walk(path):
        for name in names:
            try:
                total += os.path.getsize(os.path.join(root, name))
                files += 1
            except OSError:
                pass
    return total, files


def guard(name):
    """返回绝对路径；任何护栏不通过就抛错。"""
    if name in KEEP:
        raise RuntimeError("「{0}」在保留名单里，拒绝删除".format(name))
    abspath = os.path.abspath(os.path.join(DIST, name))
    if abspath != DIST and not abspath.startswith(DIST + os.sep):
        raise RuntimeError("「{0}」不在 dist/ 之下，拒绝删除".format(name))
    if abspath == DIST:
        raise RuntimeError("不得删除 dist 目录自身")
    return abspath


def main():
    go = "--go" in sys.argv
    print("dist/ 清理 —— {0}".format("实删" if go else "空跑（--dry-run）"))
    print("保留：{0}".format("、".join(sorted(KEEP))))
    print()

    archive_root = os.path.join(DIST, "_历史版本")

    archived = []
    for name in ARCHIVE:
        src = os.path.join(DIST, name)
        if not os.path.exists(src):
            print("  [归档跳过] {0}（不存在）".format(name))
            continue
        if os.path.exists(os.path.join(archive_root, name)):
            print("  [归档跳过] {0}（归档里已有同名）".format(name))
            continue
        size = os.path.getsize(src) if os.path.isfile(src) else tree_size(src)[0]
        print("  [归档] {0:<40} {1:>10} -> _历史版本\\".format(name, mb(size)))
        if go:
            os.makedirs(archive_root, exist_ok=True)   # 只在真要搬时才建
            shutil.move(src, os.path.join(archive_root, name))
            archived.append(name)

    freed = 0
    removed = []
    missing = []

    for name in TARGETS:
        abspath = guard(name)
        if not os.path.exists(abspath):
            missing.append(name)
            print("  [跳过] {0}（不存在）".format(name))
            continue

        if os.path.isdir(abspath):
            size, files = tree_size(abspath)
            kind = "目录/{0} 文件".format(files)
        else:
            size, files = os.path.getsize(abspath), 1
            kind = "文件"

        print("  [待删] {0:<40} {1:>10}  {2}".format(name, mb(size), kind))
        if not go:
            freed += size
            continue

        try:
            if os.path.isdir(abspath):
                shutil.rmtree(abspath)
            else:
                os.remove(abspath)
        except Exception as exc:
            print("  [失败] {0} —— {1}".format(name, exc))
            raise
        # 实时复检：保留名单一项都不能少
        for k in KEEP:
            if not os.path.exists(os.path.join(DIST, k)):
                raise RuntimeError("保留项「{0}」在删除过程中消失，立即中止".format(k))
        freed += size
        removed.append(name)
        print("  [已删] {0}".format(name))

    print()
    if go:
        print("归档 {0} 项 -> _历史版本/；删除 {1} 项，释放 {2}".format(
            len(archived), len(removed), mb(freed)))
    else:
        print("空跑合计将释放 {0}（未执行删除）".format(mb(freed)))
    if missing:
        print("不存在而跳过：{0}".format("、".join(missing)))

    print()
    print("=== dist/ 现状 ===")
    left = sorted(os.listdir(DIST))
    total = 0
    for name in left:
        p = os.path.join(DIST, name)
        if os.path.isdir(p):
            size, files = tree_size(p)
            total += size
            print("  [D] {0:<40} {1:>10}  files={2}".format(name, mb(size), files))
        else:
            size = os.path.getsize(p)
            total += size
            print("  [F] {0:<40} {1:>10}  {2}".format(
                name, mb(size), time.strftime("%m-%d %H:%M", time.localtime(os.path.getmtime(p)))))
    print("  dist/ 合计 {0}".format(mb(total)))

    if go:
        leftover = [n for n in left if n not in KEEP]
        print()
        print("=== 收尾判据 ===")
        print("  保留项齐全 = {0}".format(sorted(KEEP) == set(left) or set(KEEP) <= set(left)))
        print("  白名单外残留 = {0}".format(leftover if leftover else "无"))
        if leftover:
            raise RuntimeError("dist/ 里仍有白名单外的残留：{0}".format(leftover))


if __name__ == "__main__":
    main()
