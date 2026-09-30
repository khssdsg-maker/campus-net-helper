# -*- coding: utf-8 -*-
"""
把 v2.0.0 的安装包上传到 Gitee 的发行版（Release）。

用法：
    python gitee_release.py <access_token>

为什么要这个脚本：Gitee 没有官方 CLI（GitHub 有 gh），
上传发行版附件只能走 OpenAPI 的 multipart 接口。
而 Gitee 的「从 GitHub 导入」只搬代码和 tag，**不会搬 Release 附件**，
所以每次发版都要单独往 Gitee 传一遍。

依赖：只用了标准库，不需要 pip install 任何东西。
"""
import io
import json
import os
import sys
import mimetypes
import uuid
import urllib.request
import urllib.parse

OWNER = 'khssdsg'
REPO = 'campus-net-helper'
TAG = 'v2.0.0'
NAME = u'校园网助手 v2.0.0'
PROXY = 'http://127.0.0.1:56645'

BODY = u"""## 校园网助手 v2.0.0

这次是个大版本。主要补上了**网页认证模式**的两个短板，顺手把日志和几处小毛病修了。

### 新增

- **网页认证断线自动重连**：以前网页认证（portal）模式下掉线了不会自己恢复 —— 因为它的判断口径和拨号完全不是一回事（拨号看有没有 PPP 接口，网页认证得看真的能不能上网）。现在掉线后会自动重开认证页并填表提交，**最多试 3 次**就停下不硬刷。
- **网页认证支持多个认证入口**：宿舍楼、教学楼可能是不同的认证地址，现在分别记住，下拉框直接选。判定口径是「域名 + 页面路径」，同一台服务器上的 /login 和 /portal 算两个入口。
- **日志大幅减肥**：以前一天下来 72.7% 的日志都在重复刷同一句话（心跳成功 28.1% + 填表次数 27.2% + 无密码框 17.9%）。现在只在「出问题」和「恢复正常」时记，没事翻日志一眼就能看见。

### 修复

- 在线时长偶尔不动 —— 拖动窗口时会饿死界面定时器，改用墙钟自愈
- 网速曲线认错网卡 —— 只在真正切换网卡时才记
- 界面自测运行会污染你本人的日志 —— 现在自测完全不写正式日志

### 关于「打游戏时不打断你」

网页认证掉线自动重连**分两种场景**，这是故意的：

| 场景 | 行为 |
|---|---|
| 你在用电脑 | 直接把认证页窗口调出来给你，验证码你自己补 |
| 你在打游戏 / 挂机 | 只在右下角弹个托盘气泡，**绝不弹窗口打断你** |

验证码仍然不会帮你绕 —— 遇到有验证码的页面，它会填好账号密码并提示你补验证码。

### 下载

- `CampusNetHelper_v2.0.0.zip`（推荐，含使用说明）
- `CampusNetHelper_v2.0.0.exe`（单文件，绿色免安装）

> 软件没有数字签名，杀毒软件可能误报，添加信任即可。

---

> 本仓库为 GitHub 的国内镜像：
> [github.com/khssdsg-maker/campus-net-helper](https://github.com/khssdsg-maker/campus-net-helper)
"""

FILES = [
    ('release/CampusNetHelper_v2.0.0.exe', 'application/x-msdownload'),
    ('release/CampusNetHelper_v2.0.0.zip', 'application/zip'),
]


def opener():
    h = urllib.request.ProxyHandler({'http': PROXY, 'https': PROXY})
    return urllib.request.build_opener(h)


def api(method, path, data=None, headers=None, form=None, files=None):
    url = 'https://gitee.com/api/v5' + path
    if data is not None:
        body = json.dumps(data).encode('utf-8')
        hdrs = {'Content-Type': 'application/json;charset=UTF-8'}
    elif form is not None:
        boundary = '----WB' + uuid.uuid4().hex
        buf = io.BytesIO()
        for k, v in form.items():
            buf.write(('--%s\r\n' % boundary).encode('utf-8'))
            buf.write(('Content-Disposition: form-data; name="%s"\r\n\r\n' % k).encode('utf-8'))
            buf.write(str(v).encode('utf-8'))
            buf.write(b'\r\n')
        for field, filename, content, ctype in files:
            buf.write(('--%s\r\n' % boundary).encode('utf-8'))
            buf.write(('Content-Disposition: form-data; name="%s"; filename="%s"\r\n'
                       % (field, filename)).encode('utf-8'))
            buf.write(('Content-Type: %s\r\n\r\n' % ctype).encode('utf-8'))
            buf.write(content)
            buf.write(b'\r\n')
        buf.write(('--%s--\r\n' % boundary).encode('utf-8'))
        body = buf.getvalue()
        hdrs = {'Content-Type': 'multipart/form-data; boundary=%s' % boundary}
    else:
        body = None
        hdrs = {}
    if headers:
        hdrs.update(headers)
    req = urllib.request.Request(url, data=body, headers=hdrs, method=method)
    try:
        with opener().open(req, timeout=120) as r:
            return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def main():
    if len(sys.argv) < 2:
        print('用法: python gitee_release.py <access_token>')
        return 2
    token = sys.argv[1].strip()

    base = '/repos/%s/%s' % (OWNER, REPO)

    # 1) 看 v2.0.0 的 release 是否已存在
    #    ⚠️ Gitee 这个接口「查不到」时返回的是 HTTP 200 + 字面量 null（不是 404），
    #       所以不能只看状态码，必须解析内容。踩过一次。
    st, txt = api('GET', base + '/releases/tags/' + TAG + '?access_token=' + urllib.parse.quote(token))
    try:
        got = json.loads(txt)
    except Exception:
        got = None
    release_id = got.get('id') if isinstance(got, dict) else None
    if release_id:
        print('[1] 已存在 release id=%s' % release_id)
    else:
        print('[1] 未有该 tag 的 release（HTTP %s / body=%s），开始创建' % (st, txt.strip()[:40]))
        payload = {
            'access_token': token,
            'tag_name': TAG,
            'name': NAME,
            'body': BODY,
            'target_commitish': 'main',
            'prerelease': False,
        }
        st, txt = api('POST', base + '/releases', data=payload)
        print('[2] 创建 release -> HTTP %s' % st)
        if st not in (200, 201):
            print(txt[:800])
            return 1
        release_id = json.loads(txt).get('id')
        print('    release id = %s' % release_id)

    if not release_id:
        print('拿不到 release id，终止')
        return 1

    # 3) 查已上传的附件，避免重复
    st, txt = api('GET', base + '/releases/%s/attach_files?access_token=%s'
                  % (release_id, urllib.parse.quote(token)))
    existing = set()
    try:
        lst = json.loads(txt)
        if isinstance(lst, list):
            for a in lst:
                existing.add(a.get('name'))
    except Exception:
        pass
    print('[3] 已有附件: %s' % (sorted(existing) if existing else '(无)'))

    # 4) 逐个上传
    script_dir = os.path.dirname(os.path.abspath(__file__))
    ok = True
    for rel, ctype in FILES:
        path = os.path.join(script_dir, rel)
        name = os.path.basename(path)
        if not os.path.exists(path):
            print('    !! 找不到 %s' % path)
            ok = False
            continue
        if name in existing:
            print('    跳过（已存在）: %s' % name)
            continue
        content = open(path, 'rb').read()
        st, txt = api('POST', base + '/releases/%s/attach_files' % release_id,
                      form={'access_token': token},
                      files=[('file', name, content, ctype)])
        print('    上传 %s (%d B) -> HTTP %s' % (name, len(content), st))
        if st not in (200, 201):
            print('      ' + txt[:600])
            ok = False
        else:
            try:
                d = json.loads(txt)
                print('      -> %s' % d.get('browser_download_url') or '')
            except Exception:
                pass

    # 5) 复核
    st, txt = api('GET', base + '/releases/%s/attach_files?access_token=%s'
                  % (release_id, urllib.parse.quote(token)))
    if st == 200:
        try:
            lst = json.loads(txt)
        except Exception:
            lst = None
        if isinstance(lst, list):
            print('[5] 复核：现在共 %d 个附件' % len(lst))
            for a in lst:
                print('    %s  %s B' % (a.get('name'), a.get('size')))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
