# -*- coding: utf-8 -*-
"""跑 PdfSmokeTest 全量回归，输出 PASS/FAIL 计数与 M7.4-S1 段摘要。"""
import os, re, subprocess, sys, io

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "artifacts", "smoke")
os.makedirs(OUT, exist_ok=True)

env = dict(os.environ)
env["DOTNET_CLI_UI_LANGUAGE"] = "en"
env["DOTNET_NOLOGO"] = "1"

log_path = os.path.join(ROOT, "artifacts", "_harness_run.txt")

with io.open(log_path, "w", encoding="utf-8", errors="replace") as log:
    b = subprocess.run(
        ["dotnet", "build", r"tools\PdfSmokeTest\PdfSmokeTest.csproj", "-c", "Release", "--nologo"],
        cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log.write("BUILD rc=%d\n%s\n%s\n" % (b.returncode, b.stdout or "", b.stderr or ""))
    log.flush()
    if b.returncode != 0:
        print("BUILD FAILED rc=%d" % b.returncode)
        print((b.stdout or "")[-3000:])
        print((b.stderr or "")[-3000:])
        sys.exit(1)

    pdf = os.path.join(ROOT, "samples", "26西附全真模拟1物理试卷.pdf")
    r = subprocess.run(
        ["dotnet", "run", "--project", r"tools\PdfSmokeTest\PdfSmokeTest.csproj",
         "-c", "Release", "--no-build", "--", pdf, OUT],
        cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log.write("\nRUN rc=%d\n%s\n%s\n" % (r.returncode, r.stdout or "", r.stderr or ""))

out = io.open(log_path, encoding="utf-8", errors="replace").read()
p = len(re.findall(r"\[PASS\]", out))
f = len(re.findall(r"\[FAIL\]", out))
bline = ""
for line in out.splitlines():
    if "Warning(s)" in line and "Error(s)" in line:
        bline = line.strip()

print("BUILD:", bline or "(未解析)")
print("PASS=%d  FAIL=%d" % (p, f))

# M7.4-S1 段落
i = out.find("M7.4 Step 1")
if i >= 0:
    seg = out[i:]
    for line in seg.splitlines():
        if line.startswith("[FAIL]"):
            print("FAIL>>", line)

tail = out.strip().splitlines()[-3:]
print("---- 尾部 ----")
for line in tail:
    print(line)
