using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 测速站的人机校验。
    ///
    /// 现在支持两种 —— 都是 SHA-256 工作量证明，只是协议壳不同：
    ///
    /// 【A】中科大自研 PoW（见 UnlockUstc）
    ///   校外 IP 直接访问后端会拿到 500 "not ustc"（误导性错误信息），
    ///   必须先解一道题换 pow cookie。
    ///
    /// 【B】Anubis 反爬（见 UnlockAnubis，南京大学等站在用）
    ///   首次访问返回"Making sure you're not a bot!"挑战页，
    ///   解题后换 techaro.lol-anubis-auth cookie。
    ///
    /// 两者的解题内核完全一样：找一个 nonce，使 sha256(随机串 + nonce) 的前若干位为 0。
    /// </summary>
    internal static class PowGate
    {
        /// <summary>求解上限，防止站点把难度调高后把程序卡死在这。</summary>
        private const int MaxAttempts = 200000000;

        /// <summary>
        /// 中科大式 PoW 解锁。
        /// 流程：GET /（拿 ustc cookie）→ GET pow.php（领挑战）→ 解 nonce → POST pow_verify.php。
        /// </summary>
        public static bool Unlock(string baseUrl, CookieContainer jar, Func<bool> canceled, out string error)
        {
            error = "";
            try
            {
                string b = baseUrl.TrimEnd('/');

                // 1) 先访问首页，拿 ustc cookie
                TryGet(b + "/", jar, 8000);

                // 2) 领挑战
                string json = GetString(b + "/backend/pow.php", jar, 10000);
                if (string.IsNullOrEmpty(json))
                {
                    error = "无法获取校验挑战";
                    return false;
                }

                string challenge = JsonField(json, "challenge");
                string token = JsonField(json, "token");

                int difficulty = 20;
                int parsed;
                if (int.TryParse(JsonField(json, "difficulty"), out parsed) && parsed > 0) difficulty = parsed;

                if (challenge.Length == 0 || token.Length == 0)
                {
                    error = "校验挑战格式异常";
                    return false;
                }

                // 3) 本地求解
                string nonce = Solve(challenge, difficulty, canceled);

                if (nonce == null)
                {
                    error = canceled != null && canceled() ? "已取消" : "校验求解失败";
                    return false;
                }

                Log.Info("测速节点人机校验（中科大式）：difficulty=" + difficulty + " 解得 nonce=" + nonce);

                // 4) 提交
                string body = "{\"token\":\"" + Escape(token) + "\",\"nonce\":\"" + nonce + "\"}";
                string resp = PostJson(b + "/backend/pow_verify.php", body, jar, 15000);

                if (resp == null || resp.IndexOf("\"success\":true", StringComparison.Ordinal) < 0)
                {
                    error = "校验未通过";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Anubis 反爬解锁（南京大学等站在用，开源项目 techaro.lol/anubis）。
        ///
        /// 流程（照它的 main.mjs + sha256-webcrypto.mjs 复刻）：
        ///   1. GET /                —— 返回挑战页，内含
        ///      &lt;script id="anubis_challenge"&gt;{rules:{difficulty},challenge:{id,randomData}}&lt;/script&gt;
        ///   2. 求解：找 nonce 使 sha256(randomData + str(nonce)) 的前 floor(difficulty/2) 个字节为 0
        ///      （difficulty 为奇数时再要求下一个字节的高 4 位为 0）
        ///   3. GET /.within.website/x/cmd/anubis/api/pass-challenge
        ///          ?id=..&response=&lt;hex&gt;&nonce=..&redir=/&elapsedTime=..
        ///      —— 服务端下发 techaro.lol-anubis-auth cookie
        ///
        /// 难度通常很低（南大是 3，实测 3 毫秒就解出来）。
        /// </summary>
        public static bool UnlockAnubis(string baseUrl, CookieContainer jar, Func<bool> canceled, out string error)
        {
            error = "";
            try
            {
                string b = baseUrl.TrimEnd('/');

                // 1) 拿挑战页
                // Anubis 的**整个流程**都必须走"干净连接 + 不跟随跳转"，
                // 不只是最后提交那一步 —— 挑战页这一步复用连接同样会导致后面的提交 403。
                string html = GetString(b + "/", jar, 12000, false);
                if (string.IsNullOrEmpty(html))
                {
                    error = "无法获取校验页";
                    return false;
                }

                string json = ExtractChallengeJson(html);
                if (json.Length == 0)
                {
                    // 没有挑战 = 这个 IP 已经在白名单里 / 站点没开 Anubis，不算失败
                    Log.Info("未发现 Anubis 挑战，可能已放行：" + baseUrl);
                    return true;
                }

                string id = JsonField(json, "id");
                string randomData = JsonField(json, "randomData");

                int difficulty = 5;
                int parsed;
                if (int.TryParse(JsonField(json, "difficulty"), out parsed) && parsed > 0) difficulty = parsed;

                if (id.Length == 0 || randomData.Length == 0)
                {
                    error = "校验挑战格式异常";
                    return false;
                }

                // 2) 求解
                Stopwatch sw = Stopwatch.StartNew();
                string nonce = SolveAnubis(randomData, difficulty, canceled);
                if (nonce == null)
                {
                    error = canceled != null && canceled() ? "已取消" : "校验求解失败";
                    return false;
                }
                sw.Stop();

                Log.Info("测速节点人机校验（Anubis）：difficulty=" + difficulty
                       + " 解得 nonce=" + nonce + "（耗时 " + sw.ElapsedMilliseconds + " ms）");

                // 3) 提交（参数顺序无关，这里照它前端的拼法）
                string url = b + "/.within.website/x/cmd/anubis/api/pass-challenge"
                           + "?id=" + Uri.EscapeDataString(id)
                           + "&response=" + Sha256Hex(randomData + nonce)
                           + "&nonce=" + Uri.EscapeDataString(nonce)
                           + "&redir=" + Uri.EscapeDataString("/")
                           + "&elapsedTime=" + sw.ElapsedMilliseconds.ToString();

                Log.Info("Anubis 提交 URL: " + url);

                string resp;
                try
                {
                    // 这一步要用"干净连接 + 不跟随跳转"（keepAlive=false），
                    // 详见 MakeRequest 的说明 —— 否则会被 403。
                    resp = GetString(url, jar, 15000, false);
                }
                catch (WebException we)
                {
                    // 把服务端给的错误原因读出来，否则只知道 403 不知道为啥
                    string detail = ReadErrorBody(we);
                    Log.Warn("Anubis 提交被拒: " + we.Message + " | body=" + detail
                           + " | cookies=" + DumpCookies(jar, b));

                    // Anubis 校验通过后通常会 302 跳回首页，被自动跟随了；
                    // 只要拿到了 auth cookie 就算成功。
                    if (HasAnubisAuth(jar, b))
                    {
                        return true;
                    }
                    error = "校验请求失败: " + we.Message + (detail.Length > 0 ? "（" + detail + "）" : "");
                    return false;
                }

                if (HasAnubisAuth(jar, b))
                {
                    return true;
                }

                error = string.IsNullOrEmpty(resp) ? "校验未返回凭据" : "校验未通过";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>读取失败响应体（排查服务端为什么拒绝）。</summary>
        private static string ReadErrorBody(WebException we)
        {
            try
            {
                if (we.Response == null) return "";
                using (Stream s = we.Response.GetResponseStream())
                {
                    if (s == null) return "";
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        string t = sr.ReadToEnd();
                        if (t.Length > 300) t = t.Substring(0, 300);
                        return t.Replace("\r", " ").Replace("\n", " ");
                    }
                }
            }
            catch { return ""; }
        }

        /// <summary>列出当前持有的 Cookie（排查校验凭据有没有拿到）。</summary>
        private static string DumpCookies(CookieContainer jar, string baseUrl)
        {
            try
            {
                Uri u = new Uri(baseUrl);
                StringBuilder sb = new StringBuilder();
                foreach (Cookie c in jar.GetCookies(u))
                {
                    sb.Append(c.Name).Append('=').Append(c.Value.Length > 12 ? c.Value.Substring(0, 12) + "..." : c.Value).Append("; ");
                }
                return sb.Length == 0 ? "(无)" : sb.ToString();
            }
            catch { return "(读取失败)"; }
        }

        /// <summary>从 Anubis 挑战页里抠出 anubis_challenge 那段 JSON。</summary>
        private static string ExtractChallengeJson(string html)
        {
            int i = html.IndexOf("anubis_challenge", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";

            i = html.IndexOf('>', i);
            if (i < 0) return "";
            i++;

            int j = html.IndexOf("</script>", i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) return "";

            return html.Substring(i, j - i).Trim();
        }

        /// <summary>检查 Cookie 里有没有 Anubis 的授权票。</summary>
        private static bool HasAnubisAuth(CookieContainer jar, string baseUrl)
        {
            try
            {
                Uri u = new Uri(baseUrl);
                foreach (Cookie c in jar.GetCookies(u))
                {
                    if (c.Name.IndexOf("anubis-auth", StringComparison.OrdinalIgnoreCase) >= 0
                        && !string.IsNullOrEmpty(c.Value))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ==================================================================
        // 求解
        // ==================================================================

        /// <summary>
        /// 找 nonce，使 sha256(prefix + nonce) 的前 hexChars 个十六进制字符为 0。
        /// 直接用字节比较，避免每次拼十六进制字符串。
        /// </summary>
        private static string Solve(string prefix, int difficulty, Func<bool> canceled)
        {
            int hexChars = (int)Math.Ceiling(difficulty / 4.0);
            if (hexChars < 1) hexChars = 1;
            if (hexChars > 16) hexChars = 16;

            int fullZeroBytes = hexChars / 2;
            bool halfByte = (hexChars % 2) == 1;

            byte[] input = new byte[80];
            int prefixLen = Encoding.UTF8.GetByteCount(prefix);
            if (prefixLen > 64) prefixLen = 64;
            prefixLen = Encoding.UTF8.GetBytes(prefix, 0, Math.Min(prefix.Length, 64), input, 0);

            using (SHA256 sha = SHA256.Create())
            {
                for (int nonce = 0; nonce < MaxAttempts; nonce++)
                {
                    if (canceled != null && (nonce & 0x3FFF) == 0 && canceled()) return null;

                    string ns = nonce.ToString();
                    int n = prefixLen + Encoding.UTF8.GetBytes(ns, 0, ns.Length, input, prefixLen);

                    byte[] hash = sha.ComputeHash(input, 0, n);

                    bool ok = true;
                    for (int i = 0; i < fullZeroBytes; i++)
                    {
                        if (hash[i] != 0) { ok = false; break; }
                    }
                    if (ok && halfByte && (hash[fullZeroBytes] >> 4) != 0) ok = false;

                    if (ok) return ns;
                }
            }

            return null;
        }

        /// <summary>
        /// Anubis 的求解规则 —— **和中科大那套不一样，别混用**。
        ///
        /// Anubis（见它 worker 里的 sha256-webcrypto.mjs）：
        ///     full = difficulty / 2          （整除，要求这么多个整字节为 0）
        ///     half = difficulty % 2 != 0     （为真时再要求下一个字节的高 4 位为 0）
        /// 它的 difficulty 是**十六进制半字节数**（南大是 3 → 前 3 个 hex 字符为 0）。
        ///
        /// 而中科大的 difficulty 是**比特数**，用 ceil(difficulty/4) 换算。
        /// 早期这里图省事复用了中科大的算法，导致南大 difficulty=3 被算成
        /// "只查半个字节"，条件太松、交上去的答案必然不对（服务端一直回 403）。
        /// </summary>
        private static string SolveAnubis(string prefix, int difficulty, Func<bool> canceled)
        {
            int fullBytes = difficulty / 2;
            bool halfByte = (difficulty % 2) != 0;
            if (fullBytes > 32) fullBytes = 32;

            byte[] input = new byte[80];
            int prefixLen = Encoding.UTF8.GetByteCount(prefix);
            if (prefixLen > 64) prefixLen = 64;
            prefixLen = Encoding.UTF8.GetBytes(prefix, 0, Math.Min(prefix.Length, 64), input, 0);

            using (SHA256 sha = SHA256.Create())
            {
                for (int nonce = 0; nonce < MaxAttempts; nonce++)
                {
                    if (canceled != null && (nonce & 0x3FFF) == 0 && canceled()) return null;

                    string ns = nonce.ToString();
                    int n = prefixLen + Encoding.UTF8.GetBytes(ns, 0, ns.Length, input, prefixLen);

                    byte[] hash = sha.ComputeHash(input, 0, n);

                    bool ok = true;
                    for (int i = 0; i < fullBytes; i++)
                    {
                        if (hash[i] != 0) { ok = false; break; }
                    }
                    if (ok && halfByte && fullBytes < hash.Length && (hash[fullBytes] >> 4) != 0) ok = false;

                    if (ok) return ns;
                }
            }

            return null;
        }

        private static string Sha256Hex(string s)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ==================================================================
        // HTTP 辅助
        // ==================================================================

        internal static HttpWebRequest MakeRequest(string url, CookieContainer jar)
        {
            return MakeRequest(url, jar, true);
        }

        /// <summary>
        /// 构造请求。
        ///
        /// ⚠️ keepAlive 必须按用途区分，否则会静默把延迟测高好几倍：
        ///   · 普通测速请求 → **true**。连接复用能省掉每次的 TCP+TLS 握手，
        ///     延迟测量是 10 次串行请求，一旦每次重连就会从 46ms 涨到 150ms+。
        ///   · Anubis 的 pass-challenge → **false**。它靠 302 响应下发凭据，
        ///     既不能自动跟随跳转，也不能复用连接，必须一次一个干净连接。
        /// </summary>
        internal static HttpWebRequest MakeRequest(string url, CookieContainer jar, bool keepAlive)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                          + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

            req.KeepAlive = keepAlive;
            req.AllowAutoRedirect = keepAlive;

            if (!keepAlive)
            {
                try { req.Headers["Connection"] = "close"; } catch { }
            }

            // 测速要测真实链路，不走系统代理
            req.Proxy = null;

            // ⚠️ 并发测速的关键：必须逐个目标点放大连接上限。
            // ServicePointManager.DefaultConnectionLimit 只对"之后新建"的 ServicePoint 生效，
            // 而测速前做节点校验(PoW)时就已经和目标站建过连接了 ——
            // 那个 ServicePoint 的上限还是默认值 2，于是 16 条并发会被静默压成 2 条，
            // 测速结果直接矮一大截（实测 700 Mbps 的链路只能测出 247）。
            try
            {
                ServicePoint sp = req.ServicePoint;
                if (sp != null && sp.ConnectionLimit < 64) sp.ConnectionLimit = 64;
            }
            catch { }

            // Referer 一律设成站点根路径。
            // 注意踩过的坑：Anubis 的 pass-challenge 地址是 /.within.website/...，
            // 不含 /backend，早期只对 /backend 路径设 Referer，结果提交被判为跨站请求直接 403。
            try
            {
                Uri u = new Uri(url);
                req.Referer = u.Scheme + "://" + u.Authority + "/";
            }
            catch { }

            if (jar != null) req.CookieContainer = jar;
            return req;
        }

        private static bool TryGet(string url, CookieContainer jar, int timeoutMs)
        {
            try
            {
                GetString(url, jar, timeoutMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(string url, CookieContainer jar, int timeoutMs)
        {
            return GetString(url, jar, timeoutMs, true);
        }

        private static string GetString(string url, CookieContainer jar, int timeoutMs, bool keepAlive)
        {
            HttpWebRequest req = MakeRequest(url, jar, keepAlive);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;

            using (WebResponse resp = req.GetResponse())
            {
                using (Stream s = resp.GetResponseStream())
                {
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
        }

        private static string PostJson(string url, string body, CookieContainer jar, int timeoutMs)
        {
            HttpWebRequest req = MakeRequest(url, jar);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;

            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;

            using (Stream s = req.GetRequestStream())
            {
                s.Write(data, 0, data.Length);
            }

            using (WebResponse resp = req.GetResponse())
            {
                using (Stream s = resp.GetResponseStream())
                {
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
        }

        // ==================================================================
        // 极简 JSON 取值（.NET 4.0 没有内置 JSON 库，这里只处理扁平结构）
        // ==================================================================

        private static string JsonField(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return "";

            i = json.IndexOf(':', i);
            if (i < 0) return "";
            i++;

            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return "";

            if (json[i] == '"')
            {
                i++;
                int j = json.IndexOf('"', i);
                return j < 0 ? "" : json.Substring(i, j - i);
            }

            int k = i;
            while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-' || json[k] == '.')) k++;
            return json.Substring(i, k - i);
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
