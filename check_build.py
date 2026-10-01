# -*- coding: utf-8 -*-
"""
构建产物校验：确认 dist\\CampusNetHelper.exe 里
  ① 版本资源嵌进去了（文件属性 → 详细信息 能看到）
  ② 图标还在（换 /win32res 之后最容易丢的东西）
  ③ 界面显示的版本号（MainWindow.VersionText）和资源里的对得上

用法：
    python check_build.py

为什么需要它：
    版本资源是靠 build_check.bat 调 Windows SDK 的 rc.exe 编译 .rc 得来的。
    如果机器上没装 SDK，或者 .rc 写挂了，构建会**静默降级**成"只有图标、没有版本号"。
    光看构建输出"BUILD SUCCESS"是发现不了的 —— 必须真的把产物拆开读一遍。
    这是"能编译 ≠ 产物正确"的又一处现场（同类教训见开发规范里的那几条）。
"""
import ctypes
import os
import re
import sys
from ctypes import wintypes

ROOT = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(ROOT, 'dist', 'CampusNetHelper.exe')

version = ctypes.WinDLL('version', use_last_error=True)
shell32 = ctypes.WinDLL('shell32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)

version.GetFileVersionInfoSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
version.GetFileVersionInfoSizeW.restype = wintypes.DWORD
version.GetFileVersionInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD,
                                        wintypes.DWORD, ctypes.c_void_p]
version.GetFileVersionInfoW.restype = wintypes.BOOL
version.VerQueryValueW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR,
                                   ctypes.POINTER(ctypes.c_void_p),
                                   ctypes.POINTER(wintypes.UINT)]
version.VerQueryValueW.restype = wintypes.BOOL
shell32.ExtractIconExW.argtypes = [wintypes.LPCWSTR, ctypes.c_int,
                                   ctypes.POINTER(wintypes.HICON),
                                   ctypes.POINTER(wintypes.HICON), wintypes.UINT]


def _q(buf, sub):
    p, n = ctypes.c_void_p(), wintypes.UINT()
    if version.VerQueryValueW(buf, sub, ctypes.byref(p), ctypes.byref(n)):
        return p, n.value
    return None


def read_exe_version(path):
    size = version.GetFileVersionInfoSizeW(path, None)
    if size == 0:
        return None
    buf = ctypes.create_string_buffer(size)
    if not version.GetFileVersionInfoW(path, 0, size, buf):
        return None
    out = {}
    r = _q(buf, '\\')
    if r:
        ptr, n = r
        v = ctypes.cast(ptr, ctypes.POINTER(wintypes.DWORD * (n // 4))).contents

        def f(ms, ls):
            return '%d.%d.%d.%d' % (ms >> 16, ms & 0xffff, ls >> 16, ls & 0xffff)
        out['FileVersion'] = f(v[2], v[3])
        out['ProductVersion'] = f(v[4], v[5])
    langs = []
    r = _q(buf, '\\VarFileInfo\\Translation')
    if r:
        ptr, n = r
        a = ctypes.cast(ptr, ctypes.POINTER(wintypes.WORD * (n // 2))).contents
        for i in range(0, n // 2, 2):
            langs.append((a[i], a[i + 1]))
    for a, b in (langs or [(0x0804, 0x04b0)]):
        for fld in ['ProductName', 'FileDescription', 'OriginalFilename', 'ProductVersion']:
            r = _q(buf, '\\StringFileInfo\\%04x%04x\\%s' % (a, b, fld))
            if r:
                out[fld] = ctypes.wstring_at(r[0])
    return out


def read_code_version():
    """从 MainWindow.cs 里把 VersionText 抠出来 —— 和资源里的对比。"""
    p = os.path.join(ROOT, 'MainWindow.cs')
    if not os.path.exists(p):
        return None
    txt = open(p, encoding='utf-8-sig', errors='replace').read()
    m = re.search(r'VersionText\s*=\s*"([^"]+)"', txt)
    return m.group(1) if m else None


def main():
    print('=' * 62)
    if not os.path.exists(EXE):
        print('[FAIL] 找不到构建产物:', EXE)
        print('       先跑 build_check.bat')
        return 1
    print('产物:', EXE, os.path.getsize(EXE), 'bytes')

    ok = True

    # ---- ① 版本资源 ----
    info = read_exe_version(EXE)
    if not info or 'FileVersion' not in info:
        print('[FAIL] 没有版本资源 —— 文件属性里会是空的')
        print('       检查 build_check.bat 有没有找到 rc.exe，或 assets/version.rc 是否有错')
        ok = False
    else:
        print('[OK] 版本资源已嵌入')
        for k in ['FileVersion', 'ProductVersion', 'ProductName',
                  'FileDescription', 'OriginalFilename']:
            v = info.get(k, '(缺失)')
            print('     %-18s = %s' % (k, v))
        # 中文没乱码？（乱码的话解码后会出现替换符或问号）
        pn = info.get('ProductName', '')
        if '校园网助手' not in pn:
            print('[WARN] ProductName 不是预期的中文 —— version.rc 的编码可能不对')
            print('       检查 assets/version.rc 是否 UTF-8，且 #pragma code_page(65001) 还在')
            ok = False

    # ---- ② 和代码里的版本号对齐 ----
    code_ver = read_code_version()
    if code_ver:
        res_ver = (info or {}).get('ProductVersion', '')
        res_short = '.'.join(res_ver.split('.')[:len(code_ver.split('.'))]) if res_ver else ''
        if res_short and res_short != code_ver:
            print('[FAIL] 版本号不一致！')
            print('       界面里显示（MainWindow.VersionText） =', code_ver)
            print('       文件属性里（assets/version.rc）      =', res_ver)
            print('       → 改版本号时这两处必须一起改')
            ok = False
        elif res_short:
            print('[OK] 版本号对齐: 代码 %s ／ 资源 %s' % (code_ver, res_ver))

    # ---- ③ 图标 ----
    n = shell32.ExtractIconExW(EXE, -1, None, None, 0)
    if n == 0:
        print('[FAIL] 这个 exe 没有图标！(/win32res 之后最容易丢的东西)')
        print('       检查 assets/version.rc 里的 `1 ICON "logo.ico"` 那行')
        ok = False
    else:
        print('[OK] 图标还在（共 %d 个）' % n)

    print('=' * 62)
    print('结论:', '全部通过' if ok else '有问题，见上面')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
