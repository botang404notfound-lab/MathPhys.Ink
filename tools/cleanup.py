import os, shutil
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
d = os.path.join(ROOT, 'tools')
s = os.path.join(d, '_scratch')
src = os.path.join(d, '_cleanup_s3.py')
if os.path.isfile(src):
    shutil.move(src, os.path.join(s, '_cleanup_s3.py'))
    print('moved')
print('OK')
