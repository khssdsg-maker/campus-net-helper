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
    ///   · GitHub 不可达（校园网很常见）→ **完全静默**，不报错、不打扰
    ///   · 版本号解析不出来 → 不提示（宁可漏报，不可误报）
    ///   · 24 小时内只查一次（时间戳存 settings.txt）
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>GitHub 的"最新发行版"接口。</summary>
        private const string ApiUrl =
            "https://api.github.com/repos/khssdsg-maker/campus-net-helper/releases/latest";

        /// <summary>给用户点开的页面（用 releases 页而不是 API 地址）。</summary>
        public const string ReleasesPage =
            "https://github.com/khssdsg-maker/campus-net-helper/releases/latest";

        /// <summary>settings.txt 里的键名。</summary>
        public const string LastCheckKey = "LastUpdateCheck";
        public const string EnabledKey = "CheckUpdate";

        /// <summary>检查完成后回调：是否有新版 / 最新版本号 / 错误消息（空串表示成功）。</summary>
        public delegate void CheckedHandler(bool hasNew, string latest, string error);

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

                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl);
                    req.Method = "GET";
                    // ⚠️ GitHub 的 API **必须**带 UserAgent，否则直接 403。
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
                        latest = ExtractTagName(sr.ReadToEnd());
                    }
                }
                catch (Exception ex)
                {
                    // 校园网连不上 GitHub 是家常便饭 —— 完全静默，只把消息交给回调由它决定
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
                    try { done(hasNew, latest, err); }
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
        /// 从 GitHub 的 JSON 里抠 tag_name。
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
    }
}
