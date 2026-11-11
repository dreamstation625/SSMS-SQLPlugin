"""构建 + 打包 + 部署 SSMS-SQLPlugin。

用法：
    python build.py            构建、打包 vsix、部署到用户扩展目录
    python build.py --no-deploy  只构建打包，不部署

为什么不走官方 VSIX 打包链：
    本机 `dotnet build` 走到 VsixUtil 时 Process.Start 抛异常，
    MSBuild.exe（桌面版）被工具策略拦截。所以手搓 OPC 包
    （zip + [Content_Types].xml），VSIXInstaller 认得这种结构。

部署铁律（踩过坑）：
    装完必须 touch extensions.configurationchanged，
    否则 Shell 继续用旧缓存，包静默不加载。
"""

import os
import shutil
import subprocess
import sys
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
VSIX_PROJ = os.path.join(HERE, "SsmsSqlPlugin.Vsix.csproj")
BIN = os.path.join(HERE, "bin", "Debug")
DIST = os.path.join(HERE, "dist")

DLL = "SsmsSqlPlugin.dll"
DLL_FORMATTING = "SsmsSqlPlugin.Formatting.dll"
PKGDEF = "SsmsSqlPlugin.pkgdef"
MANIFEST = "extension.vsixmanifest"
VSIX = "SSMS-SQLPlugin.vsix"

# SSMS 22 用户级扩展目录（GUID 是这台机器实测的实例 ID）
SSMS_EXT_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", ""),
    "Microsoft", "SSMS", "22.0_a832b12e", "Extensions")

CONTENT_TYPES = """<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="pkgdef" ContentType="text/plain" />
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
</Types>
"""


def step(msg):
    print("[*] " + msg)


def fail(msg):
    print("[FAIL] " + msg)
    return 1


def build():
    step("构建 " + os.path.basename(VSIX_PROJ))
    r = subprocess.run(
        ["dotnet", "build", VSIX_PROJ, "-v", "q", "--nologo"],
        cwd=HERE, capture_output=True, text=True, encoding="utf-8", errors="replace")

    out = (r.stdout or "") + (r.stderr or "")
    for line in out.splitlines():
        if " error " in line or "错误" in line:
            print("    " + line.strip())

    if r.returncode != 0:
        return fail("构建失败")

    if "已成功生成" in out or "Build succeeded" in out:
        step("构建成功")
    return 0


def package():
    for name, path in ((DLL, os.path.join(BIN, DLL)),
                       (DLL_FORMATTING, os.path.join(BIN, DLL_FORMATTING))):
        if not os.path.isfile(path):
            return fail("找不到 %s" % path)

    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DIST)

    payload = [
        (os.path.join(HERE, MANIFEST), MANIFEST),
        (os.path.join(HERE, PKGDEF), PKGDEF),
        (os.path.join(BIN, DLL), DLL),
        (os.path.join(BIN, DLL_FORMATTING), DLL_FORMATTING),
    ]

    vsix_path = os.path.join(DIST, VSIX)
    with zipfile.ZipFile(vsix_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", CONTENT_TYPES)
        for src, name in payload:
            z.write(src, name)

    step("打包 %s (%.1f KB)" % (vsix_path, os.path.getsize(vsix_path) / 1024.0))
    with zipfile.ZipFile(vsix_path) as z:
        for n in z.namelist():
            print("       %-38s %8d B" % (n, z.getinfo(n).file_size))
    return 0


def deploy():
    if not os.path.isdir(SSMS_EXT_DIR):
        return fail("SSMS 扩展目录不存在：%s" % SSMS_EXT_DIR)

    # ⚠ 目录名不能随便起！
    # SSMS 的扩展目录名必须是它自己生成的随机 ID 形式（8 字符 + "." + 3 字符），
    # 例如 1ze54kt1.tye。自己起个 "ssmssqlplugin.dev" 这样的名字，
    # Shell 不会扫描到 —— 包不加载、日志零输出、菜单不出现，且不报错。
    #
    # 所以：优先复用已存在的那个目录；一个都没有时用固定 ID。
    target = None
    for name in sorted(os.listdir(SSMS_EXT_DIR)):
        d = os.path.join(SSMS_EXT_DIR, name)
        if os.path.isdir(d) and os.path.isfile(os.path.join(d, PKGDEF)):
            target = d
            break

    if target is None:
        # 沿用探测阶段 Shell 生成过的那个 ID，避免出现"目录名不合法"的问题
        target = os.path.join(SSMS_EXT_DIR, "1ze54kt1.tye")
        os.makedirs(target, exist_ok=True)
        step("新建扩展目录 " + target)
    else:
        step("复用扩展目录 " + target)

    for fname in (DLL, DLL_FORMATTING, PKGDEF):
        shutil.copy2(os.path.join(BIN if fname != PKGDEF else HERE, fname),
                     os.path.join(target, fname))
        print("       copied %s" % fname)

    shutil.copy2(os.path.join(HERE, MANIFEST), os.path.join(target, MANIFEST))
    print("       copied %s" % MANIFEST)

    # 铁律：刷新配置标记，否则 Shell 用旧缓存、包静默不加载
    marker = os.path.join(SSMS_EXT_DIR, "extensions.configurationchanged")
    with open(marker, "a"):
        os.utime(marker, None)
    step("已刷新 extensions.configurationchanged")

    return 0


def main():
    no_deploy = "--no-deploy" in sys.argv

    rc = build()
    if rc != 0:
        return rc

    rc = package()
    if rc != 0:
        return rc

    if no_deploy:
        step("跳过部署（--no-deploy）")
        return 0

    return deploy()


if __name__ == "__main__":
    sys.exit(main())
