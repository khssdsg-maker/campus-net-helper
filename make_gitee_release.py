# -*- coding: utf-8 -*-
"""
在 Gitee 建 Release 并上传附件。

用法：
    python make_gitee_release.py 2.1.0

说明文本默认取 项目根目录的 RELEASE_NOTES_v<版本>.md（也可以给第二个参数指定别的文件）。

⚠️ 三个坑（都在这里处理了）：
  1. Gitee 的「从 GitHub 导入」只搬代码和 tag，**不搬 Release 和附件** ——
     每次发版都得单独建一次、单独传一遍。GitHub 那边同步过去了不代表这边也有。
  2. 查 release 是否存在时，Gitee 查不到返回的是 **HTTP 200 + 字面量 null**，
     不是 404。只看状态码会误判成"已存在"，然后拿不到 id 直接退出。
  3. token 不用去网页重新生成 —— Git 凭据管理器里存的就是 32 位 hex 私人令牌，
     用 `git credential fill` 问它要就行。**别把 token 写进脚本**。
"""
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

OWNER = 'khssdsg'
REPO = 'campus-net-helper'
ROOT = os.path.dirname(os.path.abspath(__file__))
REL_DIR = os.path.join(ROOT, 'release')
API = 'https://gitee.com/api/v5/repos/%s/%s' % (OWNER, REPO)


def get_token():
    p = subprocess.run(['git', 'credential', 'fill'],
                       input=b'protocol=https\nhost=gitee.com\n\n',
                       capture_output=True)
    for line in p.stdout.decode('utf-8', 'replace').split('\n'):
        if line.startswith('password='):
            return line.split('=', 1)[1].strip()
    raise SystemExit('拿不到 Gitee token（git credential fill 没返回 password）')


def api_get(url):
    try:
        with urllib.request.urlopen(urllib.request.Request(url), timeout=40) as r:
            return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def api_post_json(url, payload):
    req = urllib.request.Request(url, data=json.dumps(payload).encode('utf-8'),
                                 method='POST')
    req.add_header('Content-Type', 'application/json;charset=UTF-8')
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def api_post_file(url, fields, filepath):
    bd = '----WB' + uuid.uuid4().hex
    body = b''
    for k, v in fields.items():
        body += ('--%s\r\nContent-Disposition: form-data; name="%s"\r\n\r\n%s\r\n'
                 % (bd, k, v)).encode('utf-8')
    body += ('--%s\r\nContent-Disposition: form-data; name="file"; filename="%s"\r\n'
             'Content-Type: application/octet-stream\r\n\r\n'
             % (bd, os.path.basename(filepath))).encode('utf-8')
    body += open(filepath, 'rb').read()
    body += ('\r\n--%s--\r\n' % bd).encode('utf-8')
    req = urllib.request.Request(url, data=body, method='POST')
    req.add_header('Content-Type', 'multipart/form-data; boundary=%s' % bd)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, r.read().decode('utf-8', 'replace')
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode('utf-8', 'replace')


def main():
    if len(sys.argv) < 2:
        raise SystemExit('用法: python make_gitee_release.py <版本号>  [说明文件]')
    ver = sys.argv[1].lstrip('v')
    notes_path = (sys.argv[2] if len(sys.argv) > 2
                  else os.path.join(ROOT, 'RELEASE_NOTES_v%s.md' % ver))
    notes = (open(notes_path, encoding='utf-8').read()
             if os.path.exists(notes_path) else '')
    title = '校园网助手 v%s' % ver

    # 附件：release 目录里带这版版本号的文件（附件名是纯 ASCII）
    files = [f for f in sorted(os.listdir(REL_DIR))
             if os.path.isfile(os.path.join(REL_DIR, f))
             and ('v%s' % ver) in f]
    if not files:
        raise SystemExit('release 目录里找不到 v%s 的发布件' % ver)

    token = get_token()
    print('token: %s...%s (%d 位)' % (token[:6], token[-4:], len(token)))
    print('版本: %s' % ver)
    print('附件: %s' % files)

    # ---- 查是否已存在（200 + null 的坑）----
    st, body = api_get('%s/releases/tags/v%s?access_token=%s' % (API, ver, token))
    rid = None
    if st == 200:
        try:
            obj = json.loads(body)
        except Exception:
            obj = None
        if isinstance(obj, dict) and obj.get('id'):
            rid = obj['id']
            print('Release 已存在 id =', rid)
        else:
            print('查不到（HTTP %d，内容是 %s）→ 新建' % (st, (body or '')[:16]))

    # ---- 创建 ----
    if rid is None:
        st, body = api_post_json('%s/releases' % API, {
            'access_token': token,
            'tag_name': 'v%s' % ver,
            'name': title,
            'body': notes,
            'target_commitish': 'main',
        })
        print('创建 Release → HTTP %d' % st)
        if st not in (200, 201):
            print(body[:500])
            raise SystemExit('创建失败')
        rid = json.loads(body)['id']
        print('新 id =', rid)

    # ---- 上传附件 ----
    st, body = api_get('%s/releases/%s?access_token=%s' % (API, rid, token))
    have = set()
    if st == 200:
        try:
            have = set(a.get('name') for a in json.loads(body).get('assets', []))
        except Exception:
            pass
    for f in files:
        if f in have:
            print('  已存在，跳过:', f)
            continue
        st, body = api_post_file('%s/releases/%s/attach_files' % (API, rid),
                                 {'access_token': token}, os.path.join(REL_DIR, f))
        print('  上传 %-34s → HTTP %d' % (f, st))
        if st not in (200, 201):
            print('    ', body[:300])

    # ---- 回验（Gitee 的 assets 不返回 size，所以只列名字）----
    print()
    print('=== 回验 ===')
    st, body = api_get('%s/releases/%s?access_token=%s' % (API, rid, token))
    obj = json.loads(body)
    print('标题:', obj.get('name'))
    print('标签:', obj.get('tag_name'))
    for a in obj.get('assets', []):
        print('  ', a.get('name'))
    print()
    print('⚠️ Gitee 的 assets 不返回大小/哈希，要真正确认得把附件下回来比对 ——')
    print('   下载地址: https://gitee.com/%s/%s/releases/download/v%s/<文件名>'
          % (OWNER, REPO, ver))


main()
