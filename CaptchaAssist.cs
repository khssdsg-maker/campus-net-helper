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
    /// ── 2026-10-03 补记（第二阶段已落地，上面①的结论被推翻）────────────────
    /// 上面说的"开运算把横笔画吃掉"，根因找到了，是两条实打实的 bug：
    ///   · 当时那个 erode2x2 **名不副实**：注释写 2×2，代码写了四次移位，
    ///     等价于 3×3 十字腐蚀，2px 笔画整根消失；
    ///   · 它在**所有颜色合并后**的掩膜上做腐蚀，干扰线和字符一相邻就粘成一块、
    ///     腐蚀时连坐。按色分层做，"字符粗、干扰线细"的差别才显出来。
    /// 修好之后（30 张真样本，海辰授权实拉）：字符准确率 81.7%，
    /// 开了置信度闸门后"填入的位里对 92.4%"。识别实现在 `CaptchaOcr.cs`，
    /// 入口是认证窗口里的「自动填写」按钮。
    /// 上面②那句"样本攒够了再做"依然成立且依然有用 ——
    /// 采集一直在跑，攒得越多，模板库和准确率越有得救（见 CaptchaOcr 的 MeasureDirectory）。
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

        /// <summary>
        /// settings.txt 里的键名：网页认证是否**自动填验证码并提交**（默认开）。
        ///
        /// 2026-10-12 海辰明确要求「我要的是全自动连接，而不是我们自己手动点击」，
        /// 于是把原来"识别出来只填给用户看、提交永远由人点"的分工改掉：
        /// 现在「填表并登录」会一路做完 填账号密码 → 认验证码 → 填进框 → 点登录，
        /// 失败还会换一张重试（最多 3 次，见 WebAuthWindow.AutoAuthMaxAttempts）。
        ///
        /// ⚠️ 代价写在明处，别让后来人以为是漏了：
        ///   · 每次重试失败，学校端都会记一次登录失败；3 次是刻意的上限
        ///   · 识别本身不是 100% 准（留出集字符 76.7% / 整图全对 33.3%），
        ///     所以"全自动"的实际体感取决于识别质量，用户也可能还是要手动补一次
        ///   · 想要保守行为（只填不提交）的用户，把这个开关关掉即可
        /// </summary>
        public const string AutoSubmitKey = "PortalAutoSubmit";

        /// <summary>是否自动提交（默认开）。</summary>
        public static bool AutoSubmitEnabled(Dictionary<string, string> settings)
        {
            return ConfigStore.GetBool(settings, AutoSubmitKey, true);
        }

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
                "    var s0 = im.src || '';" +
                "    return 'OK|' + w + 'x' + h + '|' + Math.round(r.left) + ',' + Math.round(r.top)" +
                "         + ',' + Math.round(r.width) + ',' + Math.round(r.height) + '|' + s0 + '|' + u;" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
                "};";
        }

        /// <summary>
        /// 只问一句"现在页面上这张验证码图是哪个 URL"（**只读**：不取像素、不改 src、不发请求）。
        ///
        /// ⚠️ 2026-10-13 新增。为什么需要它（海辰报的 bug）：
        ///    「一键登录填的验证码是刷新前的」——
        ///    程序"认出一张图"到"真正点登录"之间可能隔几百毫秒到几秒，
        ///    而这段时间里页面完全可能已经把图换掉了（回传重载 / 点了换一张）。
        ///    以前程序**无法察觉**，于是把上一张图的答案提交上去，必然失败、
        ///    而且日志里看不出来。有了这个"图的身份"，提交前对一次就行 ——
        ///    不一致就放弃这次提交，等新图。
        /// </summary>
        public static string BuildSrcJs()
        {
            return JsFinder +
                "window.__cnhCaptchaSrc = function () {" +
                "  try {" +
                "    var im = window.__cnhCaptchaImg();" +
                "    if (!im) return 'ERR:no-img';" +
                "    return 'OK|' + (im.src || '');" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
                "};";
        }

        /// <summary>解析 BuildSrcJs 的返回。格式：OK|src</summary>
        public static bool TryParseSrc(string raw, out string src)
        {
            src = "";
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw.StartsWith("ERR", StringComparison.Ordinal)) return false;
            int bar = raw.IndexOf('|');
            if (bar < 0 || raw.Substring(0, bar) != "OK") return false;
            src = raw.Substring(bar + 1);
            return src.Length > 0;
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
        /// 「自动填写」：把识别出的候选写进页面的验证码输入框。
        ///
        /// ⚠️ 这里**只赋值，不做任何提交** —— 没有 form.submit()、没有 click()、
        ///    没有 __doPostBack。项目的红线是"验证码永远由人提交"，验收时会专门
        ///    审查全项目有没有自动提交的路径，这个函数就是重点检查对象。
        ///
        /// 为什么走 JS 赋值而不是设置 element 的 value 属性：
        ///   和 BuildTypedJs 同一个原因 —— IE 标准模式下取/设 HTML 属性拿到的是
        ///   初始值，不是用户在框里看到并会随表单提交的那个实时值。
        ///   必须写 DOM 的 value 属性，页面回传时带的才是它。
        ///
        /// 文本用 JSON 风格转义（只可能是 A-Z0-9，但仍按规矩转义，避免以后改字符集时出事）。
        /// </summary>
        public static string BuildFillJs(string text)
        {
            string safe = (text == null) ? "" : text.Replace("\\", "\\\\").Replace("'", "\\'");
            return JsFinder +
                "window.__cnhCaptchaFill = function () {" +
                "  try {" +
                "    var el = window.__cnhCaptchaInput();" +
                "    if (!el) return 'ERR:no-input';" +
                "    el.value = '" + safe + "';" +
                // ⚠️⚠️ 这两行只是"通知页面"的礼节，必须**各自包 try**（2026-10-12 实测踩到）：
                //   IE11 里**没有 `new Event()` 构造函数**（那是 Edge/现代浏览器的），
                //   原来的写法 `el.dispatchEvent(new Event('input',{bubbles:true}))` 直接抛异常，
                //   而异常在 `el.value = ...` 之后、`return 'OK'` 之前 —— 结果是
                //   **值其实已经写进框里了，函数却报失败**，上层于是显示"写不进输入框"，
                //   把用户（和我自己）往完全错误的方向引。实测真门户上一样会犯。
                //   现在改成 IE 支持的 createEvent('HTMLEvents')，且失败也不影响返回值。
                "    try { var e1 = document.createEvent('HTMLEvents'); e1.initEvent('input', true, true); el.dispatchEvent(e1); } catch (x1) {}" +
                "    try { var e2 = document.createEvent('HTMLEvents'); e2.initEvent('change', true, true); el.dispatchEvent(e2); } catch (x2) {}" +
                "    return (el.value === '" + safe + "') ? 'OK' : 'ERR:not-set';" +
                "  } catch (e) { return 'ERR:' + (e.message || e); }" +
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
        /// 解析取图脚本的返回。格式（**新，5 段**）：
        ///     OK|宽x高|左,上,宽,高|图的src|data:image/png;base64,xxxx
        /// 兼容旧的 4 段格式（没有 src 那一段）。
        /// 失败时 error 里是原因（ERR:no-img / ERR:not-ready / …）。
        /// </summary>
        public static bool TryParseShot(string raw, out int width, out int height,
                                       out string base64, out string error)
        {
            string ignore;
            return TryParseShot(raw, out width, out height, out base64, out error, out ignore);
        }

        /// <summary>
        /// 同上，但多返回一个 <paramref name="src"/> —— "这张图是哪个 URL"。
        ///
        /// ⚠️ 2026-10-13 加 src 的原因（海辰报的"填的验证码是刷新前的"）：
        ///    以前取图**不带图的身份**，程序无法判断"我认的这张"和"提交时页面上的那张"
        ///    是不是同一张。页面回传/换图只要发生在识别与提交之间，提交上去的就是
        ///    隔了一张图的旧答案 —— 必然失败，且日志里看不出来。
        ///    有了 src 才谈得上绑定（见 WebAuthWindow._captchaSrc）。
        /// </summary>
        public static bool TryParseShot(string raw, out int width, out int height,
                                       out string base64, out string error, out string src)
        {
            width = 0;
            height = 0;
            base64 = "";
            error = "";
            src = "";

            if (string.IsNullOrEmpty(raw)) { error = "空返回"; return false; }
            if (raw.StartsWith("ERR", StringComparison.Ordinal)) { error = raw; return false; }

            // 限 5 段：src 里万一带上 '|' 也不会把图片数据切碎
            string[] p = raw.Split(new char[] { '|' }, 5);
            if ((p.Length != 4 && p.Length != 5) || p[0] != "OK") { error = "返回格式不对"; return false; }

            string[] wh = p[1].Split('x');
            if (wh.Length != 2
                || !int.TryParse(wh[0], out width)
                || !int.TryParse(wh[1], out height)
                || width <= 0 || height <= 0)
            {
                error = "尺寸解析失败: " + p[1];
                return false;
            }

            // 图片数据在最后一段（5 段格式里 src 插在前面）
            string dataUrl = p[p.Length - 1];
            if (dataUrl.StartsWith("data:image", StringComparison.Ordinal) && p.Length == 5)
                src = p[3];

            if (!dataUrl.StartsWith("data:image", StringComparison.Ordinal)) { error = "不是图片数据"; return false; }
            int comma = dataUrl.IndexOf(',');
            if (comma < 0) { error = "图片数据格式不对"; return false; }
            base64 = dataUrl.Substring(comma + 1);
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
            }            catch { return 0; }
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

        // ==================================================================
        // 识别失败时留下的图（诊断用）
        //
        // 为什么要单独存一份：2026-10-03 海辰反馈"我看得很清楚，它却说无法识别"，
        // 而**那张失败的图没留下来**，我只能靠猜 —— 这不可接受。
        // 失败样本必须留证，否则同一个问题会反复发生、反复查不到。
        //
        // ⚠️ 故意放在**另一个目录**（captcha-fails），不能混进 captcha-samples：
        //    那个目录里文件名末 4 位是"标准答案"，自测要靠它量准确率。
        //    失败图没有答案，混进去会被当成带标签样本，把准确率算歪。
        //
        // 与样本采集共用同一个开关 —— 用户关掉采集，这里也一起关。
        // ==================================================================

        private const string FailDirName = "captcha-fails";

        /// <summary>失败图保留条数上限。一张约 1.4KB，200 张 ≈ 280KB。</summary>
        public const int KeepNewestFails = 200;

        public static string FailsDir
        {
            get { return Path.Combine(ConfigStore.AppDataDir, FailDirName); }
        }

        /// <summary>
        /// 存一张"没认出来"的验证码原图。note 写清楚当时的情况（几位没切出来）。
        /// 文件名：fail-yyyyMMdd-HHmmss-fff-&lt;note&gt;.png
        /// </summary>
        public static string SaveFailImage(byte[] png, string note, out string error)
        {
            error = "";
            if (png == null || png.Length == 0) { error = "没有图片数据"; return ""; }
            try
            {
                Directory.CreateDirectory(FailsDir);
                string safe = (note == null) ? "" : note.Replace(" ", "").Replace("/", "_");
                string name = "fail-"
                            + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                            + "-" + safe + ".png";
                string path = Path.Combine(FailsDir, name);
                File.WriteAllBytes(path, png);
                PruneFails(KeepNewestFails);
                Log.Info("验证码识别失败，已留存图片: " + name);
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        /// <summary>只清自己这个目录里、以 fail- 开头的 png，别的一概不碰。</summary>
        public static int PruneFails(int keepNewest)
        {
            try
            {
                if (keepNewest < 20) return 0;
                if (!Directory.Exists(FailsDir)) return 0;
                string[] files = Directory.GetFiles(FailsDir, "fail-*.png");
                if (files.Length <= keepNewest) return files.Length;

                List<string> list = new List<string>(files);
                list.Sort(delegate(string a, string b)
                {
                    return string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a));
                });
                for (int i = keepNewest; i < list.Count; i++)
                {
                    try
                    {
                        if (string.Equals(Path.GetDirectoryName(list[i]), FailsDir,
                                          StringComparison.OrdinalIgnoreCase))
                            File.Delete(list[i]);
                    }
                    catch { }
                }
                return keepNewest;
            }
            catch { return 0; }
        }

        public static int CountFails()
        {
            try
            {
                if (!Directory.Exists(FailsDir)) return 0;
                return Directory.GetFiles(FailsDir, "fail-*.png").Length;
            }
            catch { return 0; }
        }
    }
}
