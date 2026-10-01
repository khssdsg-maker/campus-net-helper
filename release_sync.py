# -*- coding: utf-8 -*-
"""发布流程：测试版自测 -> 同步生产版 -> 生产件复验。"""
import os
import shutil
import subprocess
import time

SRC = r'D:\Projects\campus-net-helper\dist\CampusNetHelper.exe'
TEST = r'D:\Tools\校园网助手-测试版\校园网助手-测试版.exe'
PROD_DIR = r'D:\Tools\校园网助手'
PROD = os.path.join(PROD_DIR, '校园网助手.exe')
RES = os.path.join(os.environ['TEMP'], 'campusnet-selftest.txt')


def selftest(exe, label):
    if os.path.exists(RES):
        os.remove(RES)
    r = subprocess.run([exe, '--selftest'], capture_output=True, timeout=200)
    time.sleep(0.4)
    if not os.path.exists(RES):
        return '%s: 没出结果文件 (exit=%d)' % (label, r.returncode)
    t = open(RES, encoding='utf-8-sig', errors='replace').read()
    cases = [l.strip() for l in t.split('\n') if l.strip().startswith('---- 用例')]
    res = [l.strip() for l in t.split('\n') if l.strip().startswith('结果：')]
    passed = sum(1 for x in res if '通过' in x)
    bad = [c.replace('---- 用例：', '').replace(' ----', '')
           for c, x in zip(cases, res) if '通过' not in x]
    s = '%s: %d/%d 通过' % (label, passed, len(res))
    if bad:
        s += '  ← 失败: ' + '; '.join(bad)
    return s


# ① 部署到测试版并自测
shutil.copy2(SRC, TEST)
print('[1] 测试版已更新 (%d bytes)' % os.path.getsize(TEST))
print('    ' + selftest(TEST, '测试版自测'))

# ② 备份生产版
ver = None
try:
    import ctypes
    from ctypes import wintypes
    v = ctypes.WinDLL('version')
    v.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    n = v.GetFileVersionInfoSizeW(PROD, None)
    buf = ctypes.create_string_buffer(n)
    v.GetFileVersionInfoW(PROD, 0, n, buf)
    p, ln = ctypes.c_void_p(), wintypes.UINT()
    if v.VerQueryValueW(buf, '\\', ctypes.byref(p), ctypes.byref(ln)):
        a = ctypes.cast(p, ctypes.POINTER(wintypes.DWORD * (ln.value // 4))).contents
        ver = '%d.%d.%d' % (a[2] >> 16, a[2] & 0xffff, a[3] >> 16)
except Exception:
    pass
bakname = '校园网助手.exe.v%s.bak' % (ver or 'old')
bak = os.path.join(PROD_DIR, bakname)
if not os.path.exists(bak):
    shutil.copy2(PROD, bak)
    print('[2] 已备份旧生产版 -> %s' % bakname)
else:
    print('[2] 备份已存在，跳过: %s' % bakname)

# ③ 同步生产版
shutil.copy2(SRC, PROD)
print('[3] 已同步生产版 (%d bytes)' % os.path.getsize(PROD))

# ④ 生产件复验
print('    ' + selftest(PROD, '生产件复验'))
