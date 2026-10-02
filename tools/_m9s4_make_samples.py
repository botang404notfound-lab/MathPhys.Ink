# -*- coding: utf-8 -*-
"""M9 S4 打包第 4 步：造包内测试样例（小试卷 PDF + 外挂工程 .twb）。

与 M8 的 `_s4_make_samples.py` 是同一套做法（那版目标写死在 M8 包目录），
这里把目标改成 `dist/tablet`，并把说明文字更新到 M9（导出）语境。

为什么样例必须由脚本现造、而不是把上一版复制过来：
  外挂工程里存着**小试卷的指纹**（SHA256(长度 ‖ 前1MiB ‖ 后1MiB) 前 32 位）。
  指纹与 PDF 必须成对；复制两份文件虽然也能凑成一对，但一旦有人单独替换了 PDF，
  两边就对不上了。现造 = 指纹与文件天然一致。

样例刻意做小：
- PDF 是现画的（595x842 pt 一页、纯 Helvetica、不嵌字体）⇒ 几 KB，不占包体；
- 工程用**相对路径**引用它 ⇒ 两个文件一起搬到任何地方都还能打开。

产物：dist/tablet/测试样例/  （随包交付）
"""
import hashlib
import io
import json
import os
import struct
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PKG = os.path.join(ROOT, 'dist', 'tablet')
SAMPLE_DIR = os.path.join(PKG, '测试样例')

PDF_NAME = '测试用小试卷.pdf'


def make_pdf(path):
    """手写一份一页的极简 PDF（不嵌字体，只用到 Helvetica 与矩形/直线）。"""
    content = '\n'.join([
        '1 w',
        '50 760 495 58 re S',
        'BT /F1 16 Tf 64 798 Td (Sample Paper) Tj ET',
        'BT /F1 10 Tf 64 776 Td (for testing export  -  page 1 of 1) Tj ET',
        '',
        'BT /F1 12 Tf 56 706 Td (1.) Tj ET',
        '56 692 m 545 692 l S',
        'BT /F1 12 Tf 56 646 Td (2.) Tj ET',
        '56 632 m 545 632 l S',
        'BT /F1 12 Tf 56 586 Td (3.) Tj ET',
        '56 572 m 545 572 l S',
        'BT /F1 12 Tf 56 526 Td (4.) Tj ET',
        '56 512 m 545 512 l S',
        '',
        'BT /F1 10 Tf 56 470 Td (Answer each question below.) Tj ET',
        '56 456 m 545 456 l S',
        '56 396 m 545 396 l S',
        '56 336 m 545 336 l S',
        '56 276 m 545 276 l S',
    ]).encode('latin-1')

    objects = [
        b'<< /Type /Catalog /Pages 2 0 R >>',
        b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] '
        b'/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
        b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
        b'<< /Length ' + str(len(content)).encode() + b' >>\nstream\n' + content + b'\nendstream',
    ]

    out = io.BytesIO()
    out.write(b'%PDF-1.4\n')
    offsets = []
    for index, body in enumerate(objects, start=1):
        offsets.append(out.tell())
        out.write(('%d 0 obj\n' % index).encode())
        out.write(body)
        out.write(b'\nendobj\n')

    xref_at = out.tell()
    out.write(b'xref\n0 %d\n' % (len(objects) + 1))
    out.write(b'0000000000 65535 f \n')
    for offset in offsets:
        out.write(('%010d 00000 n \n' % offset).encode())
    out.write(b'trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n'
              % (len(objects) + 1, xref_at))

    data = out.getvalue()
    io.open(path, 'wb').write(data)
    return len(data)


def fingerprint(path):
    """复现宿主 DocumentFingerprint.Compute：SHA256(长度 ‖ 前 1MiB ‖ 后 1MiB) 取前 32 个十六进制字符。"""
    window = 1 << 20
    length = os.path.getsize(path)
    whole = length <= 2 * window

    with open(path, 'rb') as handle:
        head = handle.read(min(window, length))
        tail = b''
        if not whole:
            handle.seek(length - window)
            tail = handle.read(window)

    payload = struct.pack('<q', length) + head + tail
    return hashlib.sha256(payload).hexdigest()[:32]


def make_twb(path, pdf_path, title):
    """造一份「外挂」工程：相对路径 + 指纹；absolutePath 留空（换个机器照样打得开）。"""
    now = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    manifest = {
        'schemaVersion': 1,
        'title': title,
        'createdUtc': now,
        'updatedUtc': now,
        'pdf': {
            'embedding': 'referenced',
            # 相对路径优先 ⇒ 两个文件一起搬走也还能打开（这就是它存在的理由）
            'relativePath': os.path.basename(pdf_path),
            'absolutePath': None,
            'fingerprint': fingerprint(pdf_path),
        },
        # ★ 这里**故意**存一份"看不到纸"的视口（100%@原点）：
        #   宿主认出这是"看不到任何页面"的档，会兜底改成「适应宽度」⇒
        #   无论一体机是 1080p 还是 4K，打开样例都是**铺满屏幕**的。
        'view': {'pageIndex': 0, 'scale': 1.0, 'offsetX': 0.0, 'offsetY': 0.0,
                 'isFullScreen': False},
        'stats': {'strokeCount': 0, 'objectCount': 0},
    }

    with zipfile.ZipFile(path, 'w') as archive:
        archive.writestr('manifest.json',
                         json.dumps(manifest, ensure_ascii=False, indent=2),
                         compress_type=zipfile.ZIP_DEFLATED)
    return manifest


README = '''数理墨 · M9 测试样例
=================================

这个文件夹是给「工程文件 / 导出」预备的测试材料，包很小，删掉也不影响主程序。

  测试用小试卷.pdf      一份现画的空白小试卷（一页，几 KB，正好拿来试导出）
  外挂工程样例.twb      一份"外挂工程"——它不装试卷，只记住试卷在哪


它能帮你测什么
--------------

【测法一 · 拿它试导出（M9 的主角）】
  1. 双击「外挂工程样例.twb」
     期望：白板打开，画布上是那份空白小试卷（铺满屏幕）；
           工具栏靠右的工程文字写着「外挂」。
  2. 随便写两笔，然后点「导出 PNG」或按 Ctrl+E，选一个文件夹
     期望：文件夹里出现一张 PNG，是这个"卷面 + 你写的字"；
           文件名是「外挂工程样例-第01页.png」（用的是工程标题，不是临时文件名）。
  3. 点「导出 PDF」或按 Ctrl+Shift+E，选个文件名保存
     期望：得到一份一页的 PDF，内容和上面那张 PNG 一样。

【测法二 · 试卷被挪走（工程文件的回归项）】
  1. 先把「测试用小试卷.pdf」改个名（比如改成 测试用小试卷-旧.pdf）；
  2. 再双击「外挂工程样例.twb」
     期望：弹出一个"请指出这份 PDF 在哪里"的文件选择框，标题写着
           「找不到工程引用的试卷…」；点「取消」⇒ 什么都不打开，白板保持原样。
  3. 把它改回原名，再双击
     期望：又能正常打开。

  追问（可选）：把整个「测试样例」文件夹复制到 U 盘再双击工程
  期望：照样打开 —— 这就是"相对路径"的好处（工程搬了家也认得路）。

测完可以把整个「测试样例」文件夹删掉。
'''


def main():
    if not os.path.isdir(PKG):
        print('[ERR] 包目录不存在：{0}'.format(PKG))
        return 1

    os.makedirs(SAMPLE_DIR, exist_ok=True)

    pdf_path = os.path.join(SAMPLE_DIR, PDF_NAME)
    size = make_pdf(pdf_path)
    print('  小试卷 PDF：{0}  {1} 字节'.format(pdf_path, size))

    twb_path = os.path.join(SAMPLE_DIR, '外挂工程样例.twb')
    manifest = make_twb(twb_path, pdf_path, '外挂工程样例')
    print('  外挂工程：{0}  {1} 字节'.format(twb_path, os.path.getsize(twb_path)))
    print('     相对路径 = {0}'.format(manifest['pdf']['relativePath']))
    print('     指纹     = {0}'.format(manifest['pdf']['fingerprint']))

    readme_path = os.path.join(SAMPLE_DIR, '说明.txt')
    io.open(readme_path, 'w', encoding='utf-8', newline='').write(README)
    print('  说明：{0}'.format(readme_path))

    print()
    print('  校验 指纹一致：{0}'.format(fingerprint(pdf_path) == manifest['pdf']['fingerprint']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
