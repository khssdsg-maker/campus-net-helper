using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 检查更新（C5）：**只提示，不自动下载、不自动安装**。
    ///
    /// 为什么坚持"只提示"：
    ///   这是个绿色单文件软件 —— 自动下载安装意味着要写文件、要处理权限、
    ///   要面对"更新到一半断电"这类问题。那套复杂度不适合它。
    ///   而且用户是被同学口口相传装上的，突然自己变了个样会让人不安。
    ///   所以：发现新版就弹个气泡，用户自己点开页面去下。
    ///
    /// 边界（任务书要求）：
    ///   · 更新源不可达（校园网很常见）→ **完全静默**，不报错、不打扰
    ///   · 版本号解析不出来 → 不提示（宁可漏报，不可误报）
    ///   · 24 小时内只查一次（时间戳存 settings.txt）
    ///
    /// ⚠️⚠️ 2026-10-15：更新源从 **GitHub 换成 Gitee**。
    ///    原因：海辰实测 GitHub **连不上**（校园网环境），
    ///    也就是说原来那套检查更新在他那儿**从来没成功过**，形同虚设。
    ///    Gitee 实测匿名可访问（不用 token），返回 JSON 里 `tag_name` 就是版本号。
    /// </summary>
    internal static class UpdateChecker
    {
        private const string Owner = "khssdsg";
        private const string Repo = "campus-net-helper";

        /// <summary>
        /// Gitee 的"最新发行版"接口。
        ///
        /// ⚠️ 为什么用 `/releases/latest` 而不是 `/releases` 列表取第一条 ——
        ///    `latest` 是服务端定义的"最新正式版"，语义明确；
        ///    列表第一条只是"最近创建"的，万一以后发了个旧版本的补丁，
        ///    列表第一条就会是那个旧的，从而**漏报**真正的新版。
        ///
        /// ⚠️ 实测（2026-10-15）：这个接口**不带 access_token 也能用**，
        ///    返回 200 + 完整 JSON。这正是我们要的 —— 用户端不该存任何令牌。
        /// </summary>
        private const string ApiUrl =
            "https://gitee.com/api/v5/repos/" + Owner + "/" + Repo + "/releases/latest";

        /// <summary>
        /// 给用户点开的页面。
        ///
        /// ⚠️ 用 releases 页而不是 API 地址：API 返回的是一坨 JSON，
        ///    用户看到会懵。实测这个地址匿名可开（HTTP 200）。
        /// </summary>
        public const string ReleasesPage =
            "https://gitee.com/" + Owner + "/" + Repo + "/releases";

        /// <summary>settings.txt 里的键名。</summary>
        public const string LastCheckKey = "LastUpdateCheck";
        public const string EnabledKey = "CheckUpdate";

        /// <summary>
        /// 检查完成后回调：是否有新版 / 最新版本号 / 错误消息（空串表示成功）/
        /// 主程序安装包的直链（拿不到就是空串）。
        /// </summary>
        public delegate void CheckedHandler(bool hasNew, string latest, string error, string assetUrl);

        public static string CurrentVersion()
        {
            return MainWindow.VersionText;
        }

        /// <summary>是否开启了自动检查更新（默认开）。</summary>
        public static bool Enabled(Dictionary<string, string> settings)
        {
            return ConfigStore.GetBool(settings, EnabledKey, true);
        }

        /// <summary>距上次检查是否已经过了 24 小时（手动检查不看这个）。</summary>
        public static bool DueForCheck(Dictionary<string, string> settings)
        {
            string last = ConfigStore.GetString(settings, LastCheckKey, "");
            if (last.Length == 0) return true;

            DateTime t;
            if (!DateTime.TryParse(last, out t)) return true;   // 解析不出来就当没查过
            return (DateTime.Now - t).TotalHours >= 24;
        }

        /// <summary>记下"刚查过了"。</summary>
        public static void MarkChecked(Dictionary<string, string> settings)
        {
            settings[LastCheckKey] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string msg;
            // ⚠️ 读-合并-写：这个字典可能是主窗口启动时的快照，
            //    直接全量写回会把别处刚改的设置冲掉（同 SaveAll 那处的教训）
            Dictionary<string, string> disk = ConfigStore.LoadSettings();
            foreach (KeyValuePair<string, string> kv in settings) disk[kv.Key] = kv.Value;
            ConfigStore.SaveSettings(disk, out msg);
        }

        /// <summary>
        /// 后台查一次（不阻塞调用方）。结果通过回调送回 —— 注意回调跑在**线程池线程**上，
        /// 要动 UI 的话调用方自己 Dispatcher 回投。
        /// </summary>
        public static void CheckAsync(CheckedHandler done)
        {
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string err = "";
                string latest = "";
                string assetUrl = "";

                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                    req.Method = "GET";
                    // ⚠️ Gitee 的 API 也要求 UserAgent（不带会被拒）。
                    req.UserAgent = "CampusNetHelper/" + CurrentVersion();
                    req.Timeout = 5000;
                    req.ReadWriteTimeout = 5000;
                    req.AllowAutoRedirect = true;
                    // 和其他网络调用保持一致：绕开系统代理直连
                    //（否则用户开着加速器时可能被它接管，判断失真）
                    req.Proxy = null;

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        latest = ExtractTagName(json);
                        assetUrl = ExtractExeAssetUrl(json);
                    }
                }
                catch (Exception ex)
                {
                    // 校园网连不上更新源是家常便饭 —— 完全静默，只把消息交给回调由它决定
                    err = ex.Message;
                }

                bool hasNew = false;
                if (err.Length == 0)
                {
                    if (latest.Length == 0) err = "没能从返回内容里读出版本号";
                    else hasNew = Compare(latest, CurrentVersion()) > 0;
                }

                if (done != null)
                {
                    try { done(hasNew, latest, err, assetUrl); }
                    catch { }
                }
            });
        }

        /// <summary>
        /// 比较版本号："2.2.0"。a 比 b 新返回 1，相同返回 0，旧返回 -1。
        ///
        /// ⚠️ 必须按**数值段**比，不能按字符串 ——
        ///    字符串比较里 "2.10.0" 是**小于** "2.9.0" 的，那就会漏报新版本。
        /// ⚠️ 解析失败返回 0（当成"没有新版本"）—— 宁可漏报，不可误报。
        ///    误报会让用户白跑一趟下载页，还以为是 bug。
        /// </summary>
        public static int Compare(string a, string b)
        {
            int[] pa = ParseVersion(a);
            int[] pb = ParseVersion(b);
            if (pa == null || pb == null) return 0;

            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
            }
            return 0;
        }

        /// <summary>自测用：暴露 tag_name 解析（私有方法的测试缝）。</summary>
        internal static string TagNameForTest(string json)
        {
            try { return ExtractTagName(json); }
            catch { return ""; }
        }

        /// <summary>自测用：暴露安装包直链解析（私有方法的测试缝）。</summary>
        internal static string ExeAssetUrlForTest(string json)
        {
            try { return ExtractExeAssetUrl(json); }
            catch { return ""; }
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;

            string s = v.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

            // 去掉 -beta / +build 这类后缀，只取数字段
            int cut = s.IndexOfAny(new char[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (s.Length == 0) return null;

            string[] parts = s.Split('.');
            int[] r = new int[] { 0, 0, 0 };
            int n = parts.Length < 3 ? parts.Length : 3;
            for (int i = 0; i < n; i++)
            {
                int x;
                if (!int.TryParse(parts[i].Trim(), out x)) return null;   // 有任何一段不是数字就放弃
                if (x < 0) return null;
                r[i] = x;
            }
            return r;
        }

        /// <summary>
        /// 从 Gitee 的 JSON 里抠 tag_name。
        ///
        /// ⚠️ 故意不引 JSON 库 —— 项目硬约束是"零外部依赖、单文件绿色软件"。
        ///    这里只需要一个字段，手工定位足够；找不到就返回空串（调用方当检查失败处理）。
        /// </summary>
        private static string ExtractTagName(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";

            int i = json.IndexOf("\"tag_name\"", StringComparison.Ordinal);
            if (i < 0) return "";
            i = json.IndexOf(':', i);
            if (i < 0) return "";
            int q1 = json.IndexOf('"', i + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";

            return json.Substring(q1 + 1, q2 - q1 - 1).Trim();
        }

        /// <summary>
        /// 从 Gitee 的 JSON 里抠出**主程序安装包**的直链。
        ///
        /// 为什么要它：一个 release 底下有 4 个附件（exe、zip、源码 zip、源码 tar.gz），
        /// 把不懂电脑的同学扔到那一页上，他很可能**下错**（下成源码包就完全没法用）。
        /// 所以宁可在这里多认一步，直接把 exe 的地址给他。
        ///
        /// 认法：把所有 browser_download_url 扫一遍，取**以 .exe 结尾**的那个。
        ///   · 源码包的地址长这样：.../archive/refs/tags/v2.4.8.zip —— 不以 .exe 结尾，天然排除
        ///   · 主程序地址长这样：.../releases/download/v2.4.8/CampusNetHelper_v2.4.8.exe
        ///
        /// ⚠️ 找不到就返回空串，调用方退回"打开发行版页面"——
        ///    宁可让用户自己挑，也不能给一个假地址。
        /// ⚠️ 同样不引 JSON 库（项目硬约束：零外部依赖、单文件绿色软件）。
        /// </summary>
        private static string ExtractExeAssetUrl(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";

            const string Key = "\"browser_download_url\"";
            string found = "";
            int from = 0;

            while (true)
            {
                int i = json.IndexOf(Key, from, StringComparison.Ordinal);
                if (i < 0) break;
                from = i + Key.Length;

                int colon = json.IndexOf(':', from);
                if (colon < 0) break;
                int q1 = json.IndexOf('"', colon + 1);
                if (q1 < 0) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) break;

                string url = json.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // ⚠️ 取**第一个**以 .exe 结尾的 —— 我们的发版脚本里 exe 排在 zip 前面，
                    //    而且一个版本只有一个 exe。若将来有多个，这里取第一个也不会错得离谱。
                    found = url;
                    break;
                }
            }
            return found;
        }
    }
}
