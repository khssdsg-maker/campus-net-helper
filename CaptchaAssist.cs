using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using WinForms = System.Windows.Forms;

namespace CampusNetHelper
{
    /// <summary>
    /// 验证码辅助（C9 第一阶段：放大 + 换一张 + 带标签采集）。
    ///
    /// 这一版**刻意不做识别**。原因有两条，都不是"懒得做"：
    ///   ① 门户的验证码是 4 位彩色字符 + 满屏彩色干扰线，**原图只有 80×24**。
    ///      2026-10-02 拿真实样本量过：干扰线和字符笔画都是 1~2px（粗细重叠），
    ///      任务书设想的"形态学开运算去 1px 细线"实测会把字符的横笔画一起吃掉
    ///      （腐蚀一次：610 墨迹像素 → 75，T 的横杠、E 的中横全没了）。
    ///      想识别就得换思路（字体渲染模板之类），而**没有带标签的真实样本，
    ///      换什么思路都没法验证准确率**。
    ///   ② 所以先做确定有价值、且零风险的那半：看得更清楚 + 攒带标签样本。
    ///      样本攒够了再做识别，那时"准确率"才是一个可以测的数字，不是一句嘴上的话。
    ///
    /// ⚠️ 三条红线（改这里的代码前务必看）：
    ///
    ///   1. **绝不重新请求 /CheckCode**。
    ///      门户把答案绑在 ASP.NET_SessionId 上（实测：每次 GET 都会 Set-Cookie 一个新的
    ///      session id）。程序自己带着 cookie 再 GET 一次，会把当前 session 里的有效答案
    ///      换成另一张图的答案，而页面显示的还是旧图 —— 用户照图填**必然**错。
    ///      所以取图只有一条合法路径：**在页面已经加载好的那张图上想办法**（见 BuildExtractJs）。
    ///
    ///   2. **绝不自动提交**。
    ///      填/识别都可以帮忙，提交永远是人的动作。使用说明里向用户承诺过这一点；
    ///      而且填错一位就会在学校端记一次登录失败，学校普遍对连续失败有次数限制。
    ///
    ///   3. **采集只在"登录真的成功"时写一条，且可关**。
    ///      样本只落在本机 %AppData%，不上传、不联网。
    ///
    /// 为什么采集必须"带标签"：只有图没有答案的样本，对做模板库毫无价值。
    /// 而「用户填了 4 位」+「这次真的登上了」= 一份天然标注好的样本，
    /// 且**不给用户增加任何额外动作**。
    /// </summary>
    internal static class CaptchaAssist
    {
        // ==================================================================
        // 配置
        // ==================================================================

        /// <summary>settings.txt 里的键名：是否采集验证码样本（默认开）。</summary>
        public const string SampleCollectKey = "CaptchaSampleCollect";

        /// <summary>样本目录名（挂在 ConfigStore.AppDataDir 下面）。</summary>
        private const string SampleDirName = "captcha-samples";

        /// <summary>样本数量上限。一张样本约 1.4KB，600 张 ≈ 850KB，够用且不会无限涨。</summary>
        public const int KeepNewestSamples = 600;

        /// <summary>是否开启采集（默认开 —— 不开就永远攒不到带标签的样本）。</summary>
        public static bool SampleCollectEnabled(Dictionary<string, string> settings)
        {
            return ConfigStore.GetBool(settings, SampleCollectKey, true);
        }

        public static string SamplesDir
        {
            get { return Path.Combine(ConfigStore.AppDataDir, SampleDirName); }
        }

        // ==================================================================
        // 注入到页面里的 JS
        //
        // 两个查找函数是所有脚本共用的，单独拎出来（每次注入都带上，
        // 因为页面导航之后 window 上的东西全没了）。
        // ==================================================================

        private const string JsFinder =
            "window.__cnhCaptchaImg = function () {" +
            "  var e = document.getElementById('MainContent_ImageCC');" +
            "  if (e) return e;" +
            "  var a = document.getElementsByTagName('img');" +
            "  for (var i = 0; i < a.length; i++) {" +
            "    var s = (a[i].src || '').toLowerCase();" +
            "    if (s.indexOf('checkcode') >= 0 || s.indexOf('captcha') >= 0 || s.indexOf('verify') >= 0) return a[i];" +
            "  }" +
            "  return null;" +
            "};" +
            "window.__cnhCaptchaInput = function () {" +
            "  var e = document.getElementById('MainContent_TextBoxCC');" +
            "  if (e) return e;" +
            "  var a = document.getElementsByTagName('input');" +
            "  var i;" +
            "  for (i = 0; i < a.length; i++) {" +
            "    if ((a[i].type || 'text').toLowerCase() != 'text') continue;" +
            "    var n = ((a[i].name || '') + '|' + (a[i].id || '')).toLowerCase();" +
            "    if (n.indexOf('textboxcc') >= 0 || n.indexOf('checkcode') >= 0" +
            "        || n.indexOf('captcha') >= 0 || n.indexOf('verify') >= 0) return a[i];" +
            "  }" +
            "  for (i = 0; i < a.length; i++) {" +
            "    if ((a[i].type || 'text').toLowerCase() == 'text' && a[i].maxLength == 4) return a[i];" +
            "  }" +
            "  return null;" +
            "};";

        /// <summary>
        /// 取图脚本：把**页面上已经加载好的**验证码 img 画到 canvas，再吐成 base64 PNG。
        ///
        /// 为什么这条路径是安全的（不是"我觉得安全"，是机制上就不可能出问题）：
        ///   canvas.drawImage 用的是**内存里已经解码好的位图**，不会发起任何网络请求。
        ///   所以不存在"重新请求把 session 里的答案换掉"的问题。
        ///   反之，任何走 cookie 重新 GET /CheckCode 的写法都是**错的**（见类注释红线 1）。
        ///
        /// 为什么不用"截图 + 按元素矩形裁剪"（任务书最初设想的方案）：
        ///   实测 2026-10-02：截图依赖窗口处于可见/非最小化状态，窗口一被最小化，
        ///   GetWindowRect 直接返回 -32000，整条路全废；canvas 这条路在窗口不可见时
        ///   照样能拿到像素。而且 canvas 拿到的是**原始 80×24**，
        ///   截图拿到的是页面拉伸后的 155×47（已经糊过一遍了）。
        /// </summary>
        public static string BuildExtractJs()
        {
            return JsFinder +
                "window.__cnhCaptchaCap = function () {" +
                "  try {" +
                "    var im = window.__cnhCaptchaImg();" +
                "    if (!im) return 'ERR:no-img';" +
                "    if (!im.complete) return 'ERR:not-ready';" +
                "    var w = im.naturalWidth || im.width, h = im.naturalHeight || im.height;" +
                "    if (!w || !h) return 'ERR:zero-size';" +
                "    var c = document.createElement('canvas');" +
                "    c.width = w; c.height = h;" +
                "    var ctx = c.getContext('2d');" +
                "    if (!ctx) return 'ERR:no-canvas';" +
                "    ctx.drawImage(im, 0, 0);" +
                "    var u = c.toDataURL('image/png');" +
                "    if (!u || u.indexOf('data:image') !== 0) return 'ERR:no-dataurl';" +
                "    var r = im.getBoundingClientRect();" +
                "    return 'OK|' + w + 'x' + h + '|' + Math.round(r.left) + ',' + Math.round(r.top)" +
                "         + ',' + Math.round(r.width) + ',' + Math.round(r.height) + '|' + u;" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
                "};";
        }

        /// <summary>
        /// 「换一张」：给 img 的 src 挂一个时间戳，让**页面自己**重新加载这张图。
        ///
        /// ⚠️ 必须是"让页面自己去请求"，不能由程序替代。这样服务端换答案与页面换图
        ///    是同一个动作，两者永远一致；程序插手就会两边错位（红线 1）。
        ///    同时把用户已经填进框里的值清掉 —— 图换了，旧答案必然作废，
        ///    留着只会让他填了错的还以为对。
        /// </summary>
        public static string BuildRefreshJs()
        {
            return JsFinder +
                "window.__cnhCaptchaFresh = function () {" +
                "  try {" +
                "    var im = window.__cnhCaptchaImg();" +
                "    if (!im) return 'ERR:no-img';" +
                "    var u = im.src || '';" +
                "    if (!u) return 'ERR:no-src';" +
                "    u = u + (u.indexOf('?') >= 0 ? '&t=' : '?t=') + (new Date()).getTime();" +
                "    im.src = u;" +
                "    var el = window.__cnhCaptchaInput();" +
                "    if (el) el.value = '';" +
                "    return 'OK';" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
                "};";
        }

        /// <summary>只问一句"新图下载完没有"（换一张之后要等它）。</summary>
        public static string BuildReadyJs()
        {
            return JsFinder +
                "window.__cnhCaptchaReady = function () {" +
                "  try {" +
                "    var im = window.__cnhCaptchaImg();" +
                "    if (!im) return 'no-img';" +
                "    if (!im.complete) return 'no';" +
                "    return ((im.naturalWidth || im.width) > 0) ? 'yes' : 'no';" +
                "  } catch (e) { return 'no'; }" +
                "};";
        }

        /// <summary>
        /// 读用户当前填在验证码框里的内容。
        ///
        /// ⚠️ 必须走 JS 读 value，不能用 HtmlElement.GetAttribute("value") ——
        ///    IE 在标准模式下取到的是 HTML 属性（初始值），不是用户敲进去的实时值。
        /// </summary>
        public static string BuildTypedJs()
        {
            return JsFinder +
                "window.__cnhCaptchaTyped = function () {" +
                "  try {" +
                "    var el = window.__cnhCaptchaInput();" +
                "    if (!el) return 'ERR:no-input';" +
                "    return 'OK|' + (el.value == null ? '' : el.value);" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
                "};";
        }

        // ==================================================================
        // 解析（纯函数 —— 抽出来就是为了能被自测直接断言）
        // ==================================================================

        /// <summary>
        /// 解析取图脚本的返回。格式：OK|宽x高|左,上,宽,高|data:image/png;base64,xxxx
        /// 失败时 error 里是原因（ERR:no-img / ERR:not-ready / …）。
        /// </summary>
        public static bool TryParseShot(string raw, out int width, out int height,
                                       out string base64, out string error)
        {
            width = 0;
            height = 0;
            base64 = "";
            error = "";

            if (string.IsNullOrEmpty(raw)) { error = "空返回"; return false; }
            if (raw.StartsWith("ERR", StringComparison.Ordinal)) { error = raw; return false; }

            string[] p = raw.Split('|');
            if (p.Length != 4 || p[0] != "OK") { error = "返回格式不对"; return false; }

            string[] wh = p[1].Split('x');
            if (wh.Length != 2
                || !int.TryParse(wh[0], out width)
                || !int.TryParse(wh[1], out height)
                || width <= 0 || height <= 0)
            {
                error = "尺寸解析失败: " + p[1];
                return false;
            }

            if (!p[3].StartsWith("data:image", StringComparison.Ordinal)) { error = "不是图片数据"; return false; }
            int comma = p[3].IndexOf(',');
            if (comma < 0) { error = "图片数据格式不对"; return false; }
            base64 = p[3].Substring(comma + 1);
            if (base64.Length == 0) { error = "图片数据为空"; return false; }
            return true;
        }

        /// <summary>解析"用户填了什么"的返回。格式：OK|内容</summary>
        public static bool TryParseTyped(string raw, out string typed)
        {
            typed = "";
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw.StartsWith("ERR", StringComparison.Ordinal)) return false;

            string[] p = raw.Split('|');
            if (p.Length < 2 || p[0] != "OK") return false;
            typed = p[1].Trim();
            return true;
        }

        /// <summary>
        /// 像不像一个验证码：正好 4 位、且都是字母或数字。
        ///
        /// 为什么要卡这么死：这是"这条样本要不要存"的闸门。
        /// 用户可能只敲了 2 位就去点登录、或者在框里粘了别的东西 ——
        /// 那时候存下来的"标签"是错的，而错的标签比没有样本更糟（会把模板带歪）。
        /// </summary>
        public static bool IsValidCaptchaText(string s)
        {
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                char c = t[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!ok) return false;
            }
            return true;
        }

        // ==================================================================
        // 样本落盘
        // ==================================================================

        /// <summary>
        /// 存一条带标签的样本：文件名里直接写答案（yyyyMMdd-HHmmss-fff-XXXX.png）。
        ///
        /// 为什么把答案写进文件名而不另建索引文件：
        ///   人一眼就能核对（拿资源管理器浏览一遍就知道标得对不对），
        ///   也不用维护"索引和文件不同步"这种麻烦。
        /// </summary>
        public static string SaveSample(byte[] png, string typed, out string error)
        {
            error = "";
            if (png == null || png.Length == 0) { error = "没有图片数据"; return ""; }
            if (!IsValidCaptchaText(typed)) { error = "填写内容不像验证码，跳过"; return ""; }

            try
            {
                Directory.CreateDirectory(SamplesDir);
                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                            + "-" + typed.Trim().ToUpperInvariant() + ".png";
                string path = Path.Combine(SamplesDir, name);
                File.WriteAllBytes(path, png);      // 新文件，不覆盖任何已有数据
                int n = PruneSamples(KeepNewestSamples);
                Log.Info("已记录验证码样本 " + name + "（当前 " + n + " 条）");
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Warn("记录验证码样本失败: " + ex.Message);
                return "";
            }
        }

        public static int CountSamples()
        {
            try
            {
                if (!Directory.Exists(SamplesDir)) return 0;
                return Directory.GetFiles(SamplesDir, "*.png").Length;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 超上限就清掉最早的几条，返回清理后的条数。
        ///
        /// ⚠️ 只在**自己这个目录**里、且**文件名符合自己命名的格式**时才删 ——
        ///    万一路径被改错，也不会误伤用户的文件。
        /// </summary>
        public static int PruneSamples(int keepNewest)
        {
            try
            {
                if (keepNewest < 100) return CountSamples();      // 防御：别被传进来一个离谱的值
                if (!Directory.Exists(SamplesDir)) return 0;

                string[] files = Directory.GetFiles(SamplesDir, "*.png");
                if (files.Length <= keepNewest) return files.Length;

                List<string> list = new List<string>(files);
                list.Sort(delegate(string a, string b)
                {
                    // 文件名里带时间戳，按名字倒序 = 新的在前
                    return string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a));
                });

                int removed = 0;
                for (int i = keepNewest; i < list.Count; i++)
                    if (DeleteSample(list[i])) removed++;

                if (removed > 0) Log.Info("验证码样本超过上限，已清掉最早的 " + removed + " 条");
                return list.Count - removed;
            }
            catch { return CountSamples(); }
        }

        private static bool DeleteSample(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.Equals(dir, SamplesDir, StringComparison.OrdinalIgnoreCase)) return false;
                if (!IsSampleFileName(Path.GetFileName(path))) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>样本文件名格式：yyyyMMdd-HHmmss-fff-XXXX.png（纯函数，可自测）。</summary>
        public static bool IsSampleFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim();
            if (n.Length < 5) return false;
            if (!n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;

            string stem = n.Substring(0, n.Length - 4);
            if (stem.Length != 24) return false;                       // 19 + '-' + 4
            if (stem[8] != '-' || stem[15] != '-' || stem[19] != '-') return false;

            for (int i = 0; i < 19; i++)
            {
                if (i == 8 || i == 15) continue;
                if (stem[i] < '0' || stem[i] > '9') return false;
            }
            return IsValidCaptchaText(stem.Substring(20));
        }
    }
}
