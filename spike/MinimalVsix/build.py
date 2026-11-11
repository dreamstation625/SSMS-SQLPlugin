"""把构建产物打成 .vsix，并生成可直接拷贝部署的目录。

不依赖 NuGet / VSSDK BuildTools —— 环境里 nuget.org 不通，必须离线自足。
用法：python build.py
"""
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "bin", "Release")
DIST = os.path.join(HERE, "dist")
DEPLOY = os.path.join(DIST, "deploy")

DLL = "SsmsSqlPlugin.Probe.dll"
PKGDEF = "SSMS-SQLPlugin.pkgdef"
MANIFEST = "extension.vsixmanifest"
VSIX = "SSMS-SQLPlugin.Probe.vsix"

CONTENT_TYPES = """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="pkgdef" ContentType="text/plain" />
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
</Types>
"""


def main():
    dll_path = os.path.join(BIN, DLL)
    if not os.path.isfile(dll_path):
        print("[FAIL] 找不到 %s，先跑 dotnet build" % dll_path)
        return 1

    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DEPLOY)

    payload = [
        (os.path.join(HERE, MANIFEST), MANIFEST),
        (os.path.join(HERE, PKGDEF), PKGDEF),
        (dll_path, DLL),
    ]

    for src, name in payload:
        if not os.path.isfile(src):
            print("[FAIL] 缺少 %s" % src)
            return 1
        shutil.copy2(src, os.path.join(DEPLOY, name))

    vsix_path = os.path.join(DIST, VSIX)
    with zipfile.ZipFile(vsix_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", CONTENT_TYPES)
        for src, name in payload:
            z.write(src, name)

    size_kb = os.path.getsize(vsix_path) / 1024.0
    print("[OK] %s  (%.1f KB)" % (vsix_path, size_kb))

    with zipfile.ZipFile(vsix_path) as z:
        print("[OK] 内容清单:")
        for n in z.namelist():
            print("     %-36s %6d B" % (n, z.getinfo(n).file_size))

    # 关键字段自检：漏了 InstallationTarget 会装不上，且报错很含糊
    with open(os.path.join(HERE, MANIFEST), encoding="utf-8") as f:
        manifest = f.read()
    for needle in ("Microsoft.VisualStudio.Ssms", "Microsoft.VisualStudio.VsPackage"):
        if needle not in manifest:
            print("[FAIL] manifest 缺少 %s" % needle)
            return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
