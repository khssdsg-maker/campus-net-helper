using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CampusNetHelper
{
    /// <summary>测速阶段。</summary>
    public enum SpeedPhase { Idle, Ping, Download, Upload, Done, Failed }

    /// <summary>一个测速源（名字 + 完整 URL）。</summary>
    public sealed class SpeedSource
    {
        public readonly string Name;
        public readonly string Url;

        public SpeedSource(string name, string url)
        {
            Name = name;
            Url = url;
        }

        public override string ToString() { return Name; }
    }

    /// <summary>用户可以选的测速节点（LibreSpeed 兼容站点）。</summary>
    public sealed class SpeedTestNode
    {
        public readonly string Name;
        public readonly string BaseUrl;
        public readonly string Hint;

        /// <summary>需要中科大式 PoW 校验（GET pow.php → POST pow_verify.php）。</summary>
        public readonly bool RequiresPow;

        /// <summary>需要 Anubis 反爬校验（GET pass-challenge，南京大学等站在用）。</summary>
        public readonly bool RequiresAnubis;

        public SpeedTestNode(string name, string baseUrl, string hint, bool requiresPow, bool requiresAnubis)
        {
            Name = name;
            BaseUrl = baseUrl;
            Hint = hint;
            RequiresPow = requiresPow;
            RequiresAnubis = requiresAnubis;
        }

        public override string ToString() { return Name; }
    }

    /// <summary>测速进度（后台线程触发，UI 需自行切回 UI 线程）。</summary>
    public sealed class SpeedTestProgress
    {
        public SpeedPhase Phase;
        public int Percent;
        public string Message = "";

        // 已经测出来的部分结果，让界面能边测边显示（-1 = 还没测出来）
        public double PingMs = -1;
        public double JitterMs = -1;
        public double DownloadMbps = -1;
        public double UploadMbps = -1;
    }

    /// <summary>
    /// 一个瞬时速率采样点，供界面画实时波形。
    ///
    /// 为什么不用 Progress 里那个累计 Mbps 画图：累计值是"从头到现在平均多少"，
    /// 画出来是一条缓缓爬升的斜线，根本看不出网络的抖动。
    /// 波形要的是**这一瞬**跑了多少 —— 所以由独立的采样线程每隔一小段
    /// 读一次累计字节数、和上一次做差。
    /// </summary>
    public sealed class SpeedSample
    {
        public SpeedPhase Phase;
        public double Mbps;
    }

    /// <summary>测速结果。任何一项测不出来就是 -1。</summary>
    public sealed class SpeedTestResult
    {
        public double DownloadMbps = -1;
        public double UploadMbps = -1;
        public double PingMs = -1;
        public double JitterMs = -1;

        /// <summary>实际生效的数据源名字（因为会自动降级，要如实告诉用户测的是哪家）。</summary>
        public string DownloadSource = "";
        public string UploadSource = "";
        public string PingSource = "";

        /// <summary>
        /// 本次测速的出口 IP。
        /// 用它来判断流量有没有被代理/加速器接管 —— 一旦走了代理，
        /// 测出来的就不是到目标站点的真实带宽了。
        /// </summary>
        public string EgressIp = "";

        public bool Canceled;
        public string Error = "";
    }

    /// <summary>
    /// 网络测速引擎。三段式：延迟/抖动 → 下载 → 上传。
    ///
    /// 设计要点：
    ///   1. **多源自动降级**。高校测速站的后端大多只对本校校园网开放
    ///      （实测中科大直接返回 "not ustc"），跨校根本用不了。
    ///      所以下载/上传都是"先试所选节点，失败就依次换源"，保证功能可用。
    ///   2. 延迟和抖动用 HTTP 往返测（不依赖 ICMP，校园网里 ICMP 常被拦）。
    ///   3. 全程后台线程，绝不碰 UI 线程。
    /// </summary>
    public sealed class SpeedTestEngine
    {
        // ---------- 站点（用户可选） ----------

        /// <summary>
        /// 内置测速节点（只保留实测可用作"延迟/抖动"节点的）。
        ///
        /// 实测结论（2026-09-28）：
        ///   · 中科大 —— ✅ 可用。校外 IP 需先过一道自研 PoW 校验（见 PowGate.Unlock）。
        ///   · 南大   —— ✅ 可用。装了 Anubis 反爬，解题后放行（见 PowGate.UnlockAnubis）。
        ///
        /// 已删掉的两个（它们的后端不在我们掌控范围内，修不了）：
        ///   · 南航 —— 后端 PHP 没在运行，返回的是源码文本，任何浏览器都测不了。
        ///   · 浙大 —— 域名已无法解析。
        ///
        /// ⚠️ **南航虽然不能当节点，却仍在上传源列表里**（见 FallbackUploads）——
        /// 它的 empty.php 虽然回 405，但数据确实发出去了，而且响应极快，
        /// 是目前国内唯一能用来测上行的免费公开端点。**别顺手把它一起删了。**
        ///
        /// 另外注意：**节点只负责延迟/抖动**。下载走国内 CDN、上传走南航，
        /// 原因见 FallbackDownloads 的注释（混入慢节点会把整体吞吐拖垮）。
        /// </summary>
        public static readonly SpeedTestNode[] Nodes = new SpeedTestNode[]
        {
            new SpeedTestNode("中国科学技术大学", "https://test.ustc.edu.cn",
                "自动完成人机校验，可测出真实带宽。", true, false),
            new SpeedTestNode("南京大学", "https://test.nju.edu.cn",
                "自动完成反爬校验，实测可用。", false, true)
        };

        // ---------- 备用源 ----------

        /// <summary>
        /// 下载测速源。
        ///
        /// ⚠️ 这里**只放最快的源，不要往里加慢源**。实测（2026-09-28，桂电校园网）：
        ///     学习强国CDN 单源 × 16 线程   → 715 Mbps
        ///     学习强国 + 微信              → 676 Mbps
        ///     再加中科大 + 华为云镜像       → 442 Mbps（掉 38%！）
        /// 原因：慢源会把分配到它上面的那几条线程拖住，16 条连接的总吞吐就下来了。
        /// 所以宁可只用一个最快的源，也不要"多源求稳"。
        ///
        /// 这两个源都是国内大厂 CDN 上的公有安装包：体积几百 MB、带宽充足、
        /// 全国都有节点，比任何高校测速站都稳 —— "下载永远测得通"就靠它们。
        /// （思路来自 NexBox 的 speedtest.rs）
        /// </summary>
        private static readonly SpeedSource[] FallbackDownloads = new SpeedSource[]
        {
            new SpeedSource("学习强国CDN", "https://wirelesscdn-download.xuexi.cn/publish/xuexi_android/latest/xuexi_android_10002068.apk"),
            new SpeedSource("腾讯CDN(微信安装包)", "https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe")
        };

        /// <summary>
        /// 上传备用源。
        /// 南航的 empty.php 虽然会回 405，但数据确实发出去了（见 TryUpload 里的说明），
        /// 而且它响应极快，是目前国内唯一能用来测上行的免费公开端点。
        /// Cloudflare 作为兜底（走国际，数值会偏低）。
        /// </summary>
        private static readonly SpeedSource[] FallbackUploads = new SpeedSource[]
        {
            new SpeedSource("南京航空航天大学", "http://speed.nuaa.edu.cn/backend/empty.php"),
            new SpeedSource("Cloudflare", "https://speed.cloudflare.com/__up")
        };

        // ---------- 参数 ----------

        private const int PingCount = 10;
        private const double DownloadSeconds = 8.0;

        /// <summary>
        /// 下载总字节上限。这是**所有线程共享**的全局上限，设小了会直接变成测速天花板 ——
        /// 曾经设成 40MB，结果 16 条并发跑满 40MB 就全体刹车，
        /// 实测 700 Mbps 的链路只能测出 246 Mbps（40MB ÷ 1.3s ≈ 246 Mbps，数字严丝合缝）。
        /// 按 8 秒 × 1 Gbps ≈ 1 GB 估算，留足余量。
        /// </summary>
        private const long DownloadMaxBytes = 2000000000L;
        private const int UploadChunkBytes = 262144;       // 上传单片 256KB

        /// <summary>
        /// 每个上传线程最多发几片。这个值同样是个隐藏天花板：
        /// 曾经设成 24（24 × 256KB × 16 线程 = 96MB），10 秒窗口内最多只能测出 ~77 Mbps。
        /// 现在基本放开，靠 UploadStopSeconds 控制时长。
        /// </summary>
        private const int UploadMaxChunks = 400;
        private const double UploadStopSeconds = 10.0;     // 传够 10 秒就不再继续
        private const long MinUsefulBytes = 262144;        // 低于 256KB 的结果不可信
        private const double MinUsefulSeconds = 1.0;

        /// <summary>
        /// 上传结果的最小可信数据量。
        ///
        /// 低于它说明这个源几乎灌不进数据，结果没有参考价值 ——
        /// 该判失败去换列表里下一个源，而不是给用户一个像模像样的假数字。
        /// 下载那边一直有下限（MinUsefulBytes），**上传漏了**（2026-10-01 审计确认）：
        /// 原来只排除"正好为 0"，某个源限速 / 半开连接 / 服务器慢吞吞收包时，
        /// 10 秒只发出几 KB 也算"成功"，界面上显示"上传 0.003 Mbps"，
        /// 而且因为返回了 true，后面的源连试都不试了。
        /// </summary>
        private const long MinUploadUsefulBytes = 262144;  // 256 KB

        /// <summary>
        /// 预热期（秒）。TCP 慢启动 + 发送缓冲区填满的这段时间速率偏虚高，理论上该剔除。
        /// 但实测（16 线程 / CDN 源）跳过与不跳过分别是 711 / 705 Mbps，差异可以忽略；
        /// 而剔除字节会让有效计数时间变短、反而容易低估。
        /// 所以下载侧不跳（=0），上传侧保留一点（本地发送缓冲堆积确实更明显）。
        /// </summary>
        private const double DownloadWarmupSeconds = 0.0;
        private const double UploadWarmupSeconds = 0.0;

        /// <summary>
        /// 并发连接数。实测（到中科大，2026-09-28）：
        ///     1 条 →  55.7 Mbps
        ///     4 条 →  88.4 Mbps
        ///     8 条 →  93.2 Mbps
        ///    16 条 → 265.6 Mbps   ← 最优点
        ///    24 条 →  反而掉到 95 Mbps（并发过高会互相抢带宽／触发对端限流）
        /// 所以 16 是甜点，不是越多越好。
        /// </summary>
        private const int Streams = 16;

        /// <summary>
        /// 上传的并发数。上传是往对方服务器灌数据，并发太高容易把对端压出 5xx，
        /// 但太低又喂不满上行带宽 —— 实测 8 条只有 26 Mbps，16 条才接近真实上行。
        /// </summary>
        private const int UploadStreams = 16;

        static SpeedTestEngine()
        {
            // .NET 4.0 默认只启用到 TLS 1.0，不手动打开 TLS 1.2 的话
            // 所有 https 测速源都会直接握手失败（3072 = Tls12）。
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch { }
            // 并发测速要靠这个放开。.NET 默认只有 2，不改大并发会被静默压回 2 条。
            try { ServicePointManager.DefaultConnectionLimit = 64; }
            catch { }
        }

        // ---------- 状态 ----------

        private Thread _worker;
        private volatile bool _cancel;
        private volatile bool _running;
        private DateTime _lastTick = DateTime.MinValue;

        /// <summary>采样用：本阶段实际收发到的总字节数（含预热，只为画波形，不参与最终结果）。</summary>
        private long _probeBytes;

        /// <summary>采样线程的运行开关。</summary>
        private volatile bool _sampling;

        /// <summary>当前采样线程（用于换阶段时等它退出）。</summary>
        private Thread _sampler;

        /// <summary>整个测速会话共用的 Cookie 容器 —— 人机校验拿到的凭据就存在这里。</summary>
        private readonly CookieContainer _cookies = new CookieContainer();

        /// <summary>已经通过校验的节点地址，避免重复解题。</summary>
        private string _unlockedNode = "";

        public bool IsRunning { get { return _running; } }

        /// <summary>进度通知（后台线程触发）。</summary>
        public event Action<SpeedTestProgress> Progress;

        /// <summary>结束通知（后台线程触发，正常完成和取消都会触发）。</summary>
        public event Action<SpeedTestResult> Finished;

        /// <summary>瞬时速率采样（后台线程触发，界面自己切线程）。画实时波形用。</summary>
        public event Action<SpeedSample> Sample;

        // ==================================================================
        // 对外接口
        // ==================================================================

        public void Start(SpeedTestNode node)
        {
            if (_running) return;
            if (node == null) node = Nodes[0];

            _cancel = false;
            _running = true;

            SpeedTestNode n = node;
            _worker = new Thread(delegate() { RunGuarded(n); });
            _worker.IsBackground = true;
            _worker.Name = "SpeedTest";
            _worker.Start();
        }

        public void Cancel() { _cancel = true; }

        // ==================================================================
        // 主流程
        // ==================================================================

        private void RunGuarded(SpeedTestNode node)
        {
            SpeedTestResult r = new SpeedTestResult();
            try
            {
                Run(node, r);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                Log.Error("测速异常", ex);
            }
            finally
            {
                _running = false;
                StopSampler();   // 异常/取消时兜底，别把采样线程留下

                // 结果落日志 —— 以后有人问"当时到底测出来多少"，翻日志就有。
                Log.Info(string.Format(
                    "测速结果: 下载 {0} Mbps（{1}）· 上传 {2} Mbps（{3}）· 延迟 {4} ms · 抖动 {5} ms{6}",
                    r.DownloadMbps.ToString("0.0"),
                    string.IsNullOrEmpty(r.DownloadSource) ? "-" : r.DownloadSource,
                    r.UploadMbps.ToString("0.0"),
                    string.IsNullOrEmpty(r.UploadSource) ? "-" : r.UploadSource,
                    r.PingMs.ToString("0.0"),
                    r.JitterMs.ToString("0.0"),
                    r.Canceled ? " [已取消]"
                               : (string.IsNullOrEmpty(r.Error) ? "" : " [错误] " + r.Error)));

                Action<SpeedTestResult> h = Finished;
                if (h != null)
                {
                    try { h(r); }
                    catch { }
                }
            }
        }

        private void Run(SpeedTestNode node, SpeedTestResult r)
        {
            // ---------------- 0. 人机校验 ----------------
            // 有的站点加了防滥用校验，不过这一关的话它的后端会拒绝服务，
            // 看起来就像"节点不可用"。两种校验的内核都是 SHA-256 工作量证明，只是协议壳不同。
            bool needUnlock = node.RequiresPow || node.RequiresAnubis;
            if (needUnlock && _unlockedNode != node.BaseUrl)
            {
                string kind = node.RequiresAnubis ? "反爬校验" : "人机校验";
                Report(SpeedPhase.Ping, 0, "正在通过 " + node.Name + " 的" + kind + "…");

                string err;
                bool ok = node.RequiresAnubis
                    ? PowGate.UnlockAnubis(node.BaseUrl, _cookies, delegate() { return _cancel; }, out err)
                    : PowGate.Unlock(node.BaseUrl, _cookies, delegate() { return _cancel; }, out err);

                if (!ok)
                {
                    r.Error = "该节点" + kind + "未通过：" + err + "。可以换一个节点，或稍后重试。";
                    Report(SpeedPhase.Failed, 0, r.Error);
                    return;
                }

                _unlockedNode = node.BaseUrl;
                Log.Info("测速节点" + kind + "通过: " + node.Name);
            }

            // ---------------- 0.5 记录出口 ----------------
            // 一旦"软件测的"和"浏览器测的"对不上，第一个要看的就是这个：
            // 走了代理、或者校园网有多条出口，都会让数字差好几倍。
            long egressRtt;
            string egress = ProbeEgress(node, out egressRtt);
            if (egress.Length > 0)
            {
                r.EgressIp = egress;
                Log.Info("测速出口: " + egress + "（探测耗时 " + egressRtt + " ms）");
            }
            else
            {
                Log.Warn("未能探测到测速出口 IP");
            }

            // ---------------- 1. 延迟 / 抖动 ----------------
            Report(SpeedPhase.Ping, 0, "正在测量延迟与抖动…（" + node.Name + "）");

            double ping, jitter;
            if (!MeasurePing(node, out ping, out jitter))
            {
                r.Error = "测速节点无响应 —— 无法测量延迟，请换一个节点或检查网络。";
                Report(SpeedPhase.Failed, 0, r.Error);
                return;
            }

            r.PingMs = ping;
            r.JitterMs = jitter;
            r.PingSource = node.Name;

            Report(SpeedPhase.Ping, 100,
                "延迟 " + Fmt(ping) + " ms  ·  抖动 " + Fmt(jitter) + " ms", r);

            if (_cancel) { r.Canceled = true; return; }

            // ---------------- 2. 下载 ----------------
            MeasureDownload(node, r);
            if (_cancel) { r.Canceled = true; return; }

            // ---------------- 3. 上传 ----------------
            MeasureUpload(node, r);
            if (_cancel) { r.Canceled = true; return; }

            // ---------------- 4. 收尾 ----------------
            string tail = "";
            if (r.DownloadMbps < 0 && r.UploadMbps < 0)
            {
                r.Error = "所有下载与上传测速源都不可用，请稍后重试。";
                Report(SpeedPhase.Failed, 0, r.Error);
                return;
            }
            if (r.DownloadMbps < 0) tail = "（下载测速失败）";
            if (r.UploadMbps < 0) tail += "（上传测速失败）";

            Report(SpeedPhase.Done, 100,
                string.Format("测速完成：下载 {0} Mbps · 上传 {1} Mbps{2}",
                    Fmt(r.DownloadMbps), Fmt(r.UploadMbps), tail), r);
        }

        // ==================================================================
        // 延迟 / 抖动
        // ==================================================================

        private bool MeasurePing(SpeedTestNode node, out double avg, out double jitter)
        {
            avg = -1;
            jitter = -1;

            string url = node.BaseUrl.TrimEnd('/') + "/backend/empty.php";

            // 先预热一次：把 DNS 解析和 TLS 握手的一次性开销排除掉，
            // 否则第一次的往返时间会把平均值拉高一大截。
            HttpRoundTrip(url, 6000);

            List<double> samples = new List<double>();
            for (int i = 0; i < PingCount; i++)
            {
                if (_cancel) break;

                double ms = HttpRoundTrip(url, 6000);
                if (ms >= 0) samples.Add(ms);

                Report(SpeedPhase.Ping, (i + 1) * 100 / PingCount,
                    "正在测量延迟… " + (i + 1) + "/" + PingCount);
            }

            if (samples.Count == 0) return false;

            double sum = 0;
            foreach (double v in samples) sum += v;
            avg = sum / samples.Count;

            // 抖动 = 相邻两次往返时间差的平均值（和 LibreSpeed 的口径一致）
            double jsum = 0;
            int jn = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                jsum += Math.Abs(samples[i] - samples[i - 1]);
                jn++;
            }
            jitter = jn > 0 ? jsum / jn : 0;

            return true;
        }

        /// <summary>
        /// 探测本次测速的出口 IP。返回空串表示探测失败。
        /// 这个值会写进日志，用于解释"为什么软件和浏览器测出来不一样"。
        /// </summary>
        private string ProbeEgress(SpeedTestNode node, out long rttMs)
        {
            rttMs = -1;
            try
            {
                HttpWebRequest req = MakeRequest(node.BaseUrl.TrimEnd('/') + "/backend/getIP.php");
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;

                Stopwatch sw = Stopwatch.StartNew();
                using (WebResponse resp = req.GetResponse())
                {
                    using (Stream s = resp.GetResponseStream())
                    {
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        {
                            string t = sr.ReadToEnd();
                            sw.Stop();
                            rttMs = (long)sw.Elapsed.TotalMilliseconds;

                            // 响应形如 {"processedString":"1.2.3.4","rawIspInfo":""}
                            int i = t.IndexOf("\"processedString\"", StringComparison.Ordinal);
                            if (i < 0) return "";
                            i = t.IndexOf(':', i);
                            if (i < 0) return "";
                            i = t.IndexOf('"', i);
                            if (i < 0) return "";
                            int j = t.IndexOf('"', i + 1);
                            if (j < 0) return "";
                            return t.Substring(i + 1, j - i - 1);
                        }
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        private double HttpRoundTrip(string url, int timeoutMs)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                HttpWebRequest req = MakeRequest(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;

                using (WebResponse resp = req.GetResponse())
                {
                    using (Stream s = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[256];
                        s.Read(buf, 0, buf.Length);
                    }
                }

                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }
            catch
            {
                return -1;
            }
        }

        // ==================================================================
        // 下载
        // ==================================================================

        /// <summary>
        /// 下载测速。
        ///
        /// 关键点：**把 16 个线程分散到多个源上并行拉**，而不是"先试 A，A 不行再试 B"。
        /// 单一源很难喂满校园网带宽（实测中科大 16 并发约 190~265 Mbps），
        /// 分散到多个源才能真正跑满（参考实现用这一招跑到了 335 Mbps）。
        /// 每个线程失败时会自动切到下一个源，所以不怕某个源中途挂掉。
        /// </summary>
        private void MeasureDownload(SpeedTestNode node, SpeedTestResult r)
        {
            // 下载**只用最快的 CDN 源**，不把用户选的节点混进来 ——
            // 实测混入中科大/云镜像后总吞吐从 676 掉到 442 Mbps（慢源拖住线程）。
            // 用户选的节点负责延迟/抖动，语义上和参考实现一致
            //（"下载走 CDN 备用源，延迟走所选节点"）。
            List<SpeedSource> sources = new List<SpeedSource>();
            foreach (SpeedSource s in FallbackDownloads) sources.Add(s);

            if (_cancel) return;

            Report(SpeedPhase.Download, 0, "正在测试下载速度…（" + sources.Count + " 个 CDN 源并发）");

            double mbps, mb;
            string used;
            if (TryDownloadMulti(sources, out mbps, out mb, out used))
            {
                r.DownloadMbps = mbps;
                r.DownloadSource = used;
                Report(SpeedPhase.Download, 100,
                    string.Format("{0}：下载 {1} Mbps（{2} MB）", used, Fmt(mbps), Fmt(mb)), r);
                return;
            }

            Log.Warn("所有下载测速源都失败");
        }

        /// <summary>
        /// 多源并发下载。每个线程从不同源起步，失败自动轮换到下一个源。
        /// </summary>
        private bool TryDownloadMulti(List<SpeedSource> sources, out double mbps, out double mb, out string used)
        {
            mbps = 0;
            mb = 0;
            used = "";

            if (sources.Count == 0) return false;

            long total = 0;
            Stopwatch sw = Stopwatch.StartNew();
            _lastTick = DateTime.Now;

            // 记录每个源是否真的产出过数据，最后如实报给用户
            int[] hits = new int[sources.Count];
            object hitLock = new object();

            StartSampler(SpeedPhase.Download);

            Thread[] workers = new Thread[Streams];
            for (int i = 0; i < workers.Length; i++)
            {
                int startIdx = i % sources.Count;

                workers[i] = new Thread(delegate()
                {
                    int idx = startIdx;
                    int switches = 0;

                    while (sw.Elapsed.TotalSeconds < DownloadSeconds && !_cancel)
                    {
                        if (Interlocked.Read(ref total) >= DownloadMaxBytes) break;

                        string url = sources[idx].Url;
                        bool ok = false;

                        try
                        {
                            HttpWebRequest req = MakeRequest(url);
                            req.Timeout = 10000;
                            req.ReadWriteTimeout = 10000;

                            using (WebResponse resp = req.GetResponse())
                            {
                                using (Stream s = resp.GetResponseStream())
                                {
                                    byte[] buf = new byte[262144];
                                    int n;
                                    bool counted = false;

                                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        if (_cancel) break;

                                        // 画波形用：这里不管预热，收到字节就计数 ——
                                        // 波形要的是"这一瞬跑了多少"，预热期那段爬升也得画出来
                                        Interlocked.Add(ref _probeBytes, n);

                                        if (sw.Elapsed.TotalSeconds >= DownloadWarmupSeconds)
                                        {
                                            Interlocked.Add(ref total, n);
                                            counted = true;
                                        }

                                        if (sw.Elapsed.TotalSeconds >= DownloadSeconds) break;
                                        if (Interlocked.Read(ref total) >= DownloadMaxBytes) break;
                                    }

                                    ok = counted;
                                }
                            }
                        }
                        catch { }

                        if (ok)
                        {
                            lock (hitLock) { hits[idx]++; }

                            if ((DateTime.Now - _lastTick).TotalMilliseconds > 300)
                            {
                                _lastTick = DateTime.Now;
                                double el = sw.Elapsed.TotalSeconds - DownloadWarmupSeconds;
                                if (el < 0.3) el = sw.Elapsed.TotalSeconds;
                                long got = Interlocked.Read(ref total);

                                Report(SpeedPhase.Download,
                                    (int)Math.Min(99, sw.Elapsed.TotalSeconds / DownloadSeconds * 100),
                                    string.Format("正在测试下载速度… {0} MB，{1} Mbps", 
                                        Fmt(got / 1e6), Fmt(got * 8.0 / Math.Max(0.001, el) / 1e6)));
                            }
                        }
                        else
                        {
                            // 这个源没出数据（或只吐了几百字节的错误页），换下一个
                            idx = (idx + 1) % sources.Count;
                            switches++;
                            if (switches > sources.Count * 3) break;   // 全都试过两轮以上还不通就别耗着了
                            Thread.Sleep(100);
                        }
                    }
                });
                workers[i].IsBackground = true;
                workers[i].Name = "dl" + i;
                workers[i].Start();
            }

            foreach (Thread t in workers)
            {
                try { t.Join(25000); }
                catch { }
            }

            StopSampler();
            sw.Stop();

            long got2 = Interlocked.Read(ref total);
            double fullSecs = sw.Elapsed.TotalSeconds;
            double secs = fullSecs - DownloadWarmupSeconds;
            if (secs < 1.0) secs = fullSecs;

            if (secs < MinUsefulSeconds || got2 < MinUsefulBytes)
            {
                if (got2 > 0) Log.Warn("下载测速数据不足: " + (got2 / 1024) + " KB / " + fullSecs.ToString("0.0") + "s");
                return false;
            }

            // 列出真正贡献了数据的源。用到的源可能有六七个，
            // 全列出来会把界面撑成两三行，所以超过三个就概括一下。
            List<string> names = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                if (hits[i] > 0) names.Add(sources[i].Name);
            }

            if (names.Count == 0) used = sources[0].Name;
            else if (names.Count <= 3) used = string.Join(" + ", names.ToArray());
            else used = names[0] + " 等 " + names.Count + " 个源";

            mbps = got2 * 8.0 / secs / 1e6;
            mb = got2 / 1e6;
            return true;
        }

        /// <summary>
        /// 多连接并发下载测速。
        ///
        /// 为什么必须并发：单条 TCP 连接的吞吐上限 ≈ 接收窗口 ÷ 往返延迟。
        /// 到中科大的延迟约 80ms，单连接实测只能跑出 4 Mbps，
        /// 而浏览器（LibreSpeed 默认开 4~6 条并发）能跑出 199 Mbps。
        /// 单连接会把带宽严重低估，所以这里必须开多线程同时下。
        /// </summary>
        private bool TryDownload(string url, string name, out double mbps, out double mb)
        {
            mbps = 0;
            mb = 0;

            long total = 0;
            Stopwatch sw = Stopwatch.StartNew();
            _lastTick = DateTime.Now;

            StartSampler(SpeedPhase.Download);

            Thread[] workers = new Thread[Streams];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(delegate()
                {
                    try
                    {
                        HttpWebRequest req = MakeRequest(url);
                        req.Timeout = 12000;
                        req.ReadWriteTimeout = 12000;

                        using (WebResponse resp = req.GetResponse())
                        {
                            using (Stream s = resp.GetResponseStream())
                            {
                                // 512KB 读取块 —— 小块（64KB）在高带宽下光是 read 调用开销就很可观，
                                // 实测把块调大能明显拉高下载读数。
                                byte[] buf = new byte[524288];
                                int n;

                                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (_cancel) break;

                                    // 画波形用（不管预热，收到字节就计入）
                                    Interlocked.Add(ref _probeBytes, n);

                                    // 预热期内只读不计数 —— 那一段是 TCP 慢启动的虚高
                                    if (sw.Elapsed.TotalSeconds >= DownloadWarmupSeconds)
                                    {
                                        Interlocked.Add(ref total, n);
                                    }

                                    if (sw.Elapsed.TotalSeconds >= DownloadSeconds) break;
                                    if (Interlocked.Read(ref total) >= DownloadMaxBytes) break;

                                    if ((DateTime.Now - _lastTick).TotalMilliseconds > 300)
                                    {
                                        _lastTick = DateTime.Now;
                                        double el = sw.Elapsed.TotalSeconds;
                                        long got = Interlocked.Read(ref total);

                                        Report(SpeedPhase.Download,
                                            (int)Math.Min(99, el / DownloadSeconds * 100),
                                            string.Format("正在测试下载速度… {0} MB，{1} Mbps（{2}）",
                                                Fmt(got / 1e6),
                                                Fmt(got * 8.0 / Math.Max(0.001, el) / 1e6), name));
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                });
                workers[i].IsBackground = true;
                workers[i].Name = "dl" + i;
                workers[i].Start();
            }

            foreach (Thread t in workers)
            {
                try { t.Join(25000); }
                catch { }
            }

            StopSampler();
            sw.Stop();

            long got2 = Interlocked.Read(ref total);
            double fullSecs = sw.Elapsed.TotalSeconds;

            // 分子已经不含预热期，分母也要相应减掉，否则速率会被低估
            double secs = fullSecs - DownloadWarmupSeconds;
            if (secs < 1.0) secs = fullSecs;

            if (secs < MinUsefulSeconds || got2 < MinUsefulBytes)
            {
                if (got2 > 0) Log.Warn("下载测速数据不足（" + name + "）: " + (got2 / 1024) + " KB / " + fullSecs.ToString("0.0") + "s");
                return false;
            }

            mbps = got2 * 8.0 / secs / 1e6;
            mb = got2 / 1e6;
            return true;
        }

        // ==================================================================
        // 上传
        // ==================================================================

        private void MeasureUpload(SpeedTestNode node, SpeedTestResult r)
        {
            // 上传优先走南航：它响应最快，是目前国内唯一可用的公开上传端点。
            // 用户选的节点排在后面兜底（万一南航恢复了对外的完整支持）。
            // 这跟参考实现的语义一致 ——「上传走南航，下载走 CDN，延迟走所选节点」。
            List<SpeedSource> tries = new List<SpeedSource>();
            foreach (SpeedSource s in FallbackUploads) tries.Add(s);
            tries.Add(new SpeedSource(node.Name, node.BaseUrl.TrimEnd('/') + "/backend/empty.php"));

            for (int i = 0; i < tries.Count; i++)
            {
                if (_cancel) return;

                SpeedSource s = tries[i];
                Report(SpeedPhase.Upload, 0, "正在测试上传速度…（" + s.Name + "）");

                double mbps, mb;
                if (TryUpload(s.Url, s.Name, out mbps, out mb))
                {
                    r.UploadMbps = mbps;
                    r.UploadSource = s.Name;
                    Report(SpeedPhase.Upload, 100,
                        string.Format("{0}：上传 {1} Mbps（{2} MB）", s.Name, Fmt(mbps), Fmt(mb)), r);
                    return;
                }

                Log.Warn("上传测速源不可用，尝试下一个：" + s.Name);
            }

            Log.Warn("所有上传测速源都失败");
        }

        /// <summary>
        /// 多连接并发上传测速。理由同下载 —— 单连接会被接收窗口卡住，严重低估上行带宽。
        /// 每个线程各自分片 POST，单片控制在 2MB（不少服务器对单次请求体有大小限制）。
        /// </summary>
        private bool TryUpload(string url, string name, out double mbps, out double mb)
        {
            mbps = 0;
            mb = 0;

            long total = 0;
            Stopwatch sw = Stopwatch.StartNew();
            _lastTick = DateTime.Now;

            StartSampler(SpeedPhase.Upload);

            Thread[] workers = new Thread[UploadStreams];
            for (int i = 0; i < workers.Length; i++)
            {
                int idx = i;   // C# 5 的 for 变量是被共享的，必须先copy一份给闭包用
                workers[i] = new Thread(delegate()
                {
                    byte[] buf = new byte[65536];
                    new Random(20260927 + idx).NextBytes(buf);

                    for (int c = 0; c < UploadMaxChunks; c++)
                    {
                        if (_cancel) break;
                        if (c > 0 && sw.Elapsed.TotalSeconds >= UploadStopSeconds) break;

                        long chunkSent = 0;
                        try
                        {
                            HttpWebRequest req = MakeRequest(url);
                            req.Method = "POST";
                            req.ContentType = "application/octet-stream";
                            req.SendChunked = false;
                            req.ContentLength = UploadChunkBytes;
                            req.Timeout = 20000;
                            req.ReadWriteTimeout = 20000;

                            double startedAt = sw.Elapsed.TotalSeconds;

                            using (Stream s = req.GetRequestStream())
                            {
                                while (chunkSent < UploadChunkBytes)
                                {
                                    if (_cancel) break;

                                    int n = (int)Math.Min(buf.Length, UploadChunkBytes - chunkSent);
                                    s.Write(buf, 0, n);
                                    chunkSent += n;

                                    // 画波形用：上行是"边写边发"，只有在这里计数才能看到实时速率
                                    // （total 要等到整片写完才算，画出来会是一格一格的台阶）
                                    Interlocked.Add(ref _probeBytes, n);

                                    if ((DateTime.Now - _lastTick).TotalMilliseconds > 300)
                                    {
                                        _lastTick = DateTime.Now;
                                        double el = sw.Elapsed.TotalSeconds;
                                        long done = Interlocked.Read(ref total) + chunkSent;

                                        Report(SpeedPhase.Upload,
                                            (int)Math.Min(99, el / UploadStopSeconds * 100),
                                            string.Format("正在测试上传速度… {0} MB，{1} Mbps（{2}）",
                                                Fmt(done / 1e6),
                                                Fmt(done * 8.0 / Math.Max(0.001, el) / 1e6), name));
                                    }
                                }
                            }

                            // 把响应读掉，否则连接不会正常归还。
                            // 注意：HTTP 4xx/5xx **不算失败** —— 比如南航的 empty.php 会回 405，
                            // 但数据已经真的发到对方服务器了，上行带宽确实被占用。
                            // 只有连接层面的异常（连不上、被重置）才算这次上传失败。
                            try
                            {
                                using (WebResponse resp = req.GetResponse())
                                {
                                    using (Stream rs = resp.GetResponseStream())
                                    {
                                        byte[] tmp = new byte[1024];
                                        try { rs.Read(tmp, 0, tmp.Length); }
                                        catch { }
                                    }
                                }
                            }
                            catch (WebException we)
                            {
                                HttpWebResponse hr = we.Response as HttpWebResponse;
                                if (hr == null) throw;   // 连接层失败 → 这一片算失败
                                using (Stream rs = hr.GetResponseStream())
                                {
                                    byte[] tmp = new byte[1024];
                                    try { rs.Read(tmp, 0, tmp.Length); }
                                    catch { }
                                }
                            }

                            // 预热期内的片不计数（本地发送缓冲堆积会造成虚高）
                            if (startedAt >= UploadWarmupSeconds)
                            {
                                Interlocked.Add(ref total, chunkSent);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("上传测速失败（" + name + "，连接 " + idx + " 第 " + (c + 1)
                                   + " 片）: " + ex.Message);
                            break;
                        }
                    }
                });
                workers[i].IsBackground = true;
                workers[i].Name = "ul" + i;
                workers[i].Start();
            }

            foreach (Thread t in workers)
            {
                try { t.Join(25000); }
                catch { }
            }

            StopSampler();
            sw.Stop();

            long got = Interlocked.Read(ref total);

            double fullSecs = sw.Elapsed.TotalSeconds;
            double secs = fullSecs - UploadWarmupSeconds;
            if (secs < 1.0) secs = fullSecs;
            if (secs <= 0.05) return false;

            // ⚠️ 数据量不够就判失败，让调用方去换下一个源（2026-10-01 审计确认）。
            //    这里原来是 `if (got <= 0) return false;` —— 只挡"正好为 0"，
            //    于是"连得上但几乎灌不动"的源会产出一个看起来很正常的假数字。
            if (secs < MinUsefulSeconds || got < MinUploadUsefulBytes)
            {
                Log.Warn("上传测速数据不足（换源）: " + (got / 1024) + " KB / "
                    + secs.ToString("0.0") + "s");
                return false;
            }

            mbps = got * 8.0 / secs / 1e6;
            mb = got / 1e6;
            return true;
        }

        // ==================================================================
        // 工具
        // ==================================================================

        private HttpWebRequest MakeRequest(string url)
        {
            // 统一走 PowGate 的工厂：它会带上 Cookie（人机校验拿到的凭据就在里面），
            // 并且对测速强制直连（不走系统代理，否则测的是代理的速度）。
            return PowGate.MakeRequest(url, _cookies);
        }

        // ==================================================================
        // 瞬时速率采样（给界面的实时波形用）
        // ==================================================================

        /// <summary>采样间隔。200ms 足够画出抖动，又不至于把界面刷爆（5 次/秒）。</summary>
        private const int SampleIntervalMs = 200;

        /// <summary>
        /// 每个阶段开头跳过多少个采样窗口（5 × 200ms = 1 秒）。
        ///
        /// 这一秒的数字必然虚高，而且不是链路的错：
        ///   · 上传 —— 16 条流每条先往发送缓冲区里塞 256KB（合计约 4MB），
        ///     缓冲区是一次性吞下去的，统计到的"速度"能到真实带宽的两三倍
        ///     （实测起步 250 Mbps，稳定后 60）；
        ///   · 下载 —— 还在建连和 TCP 慢启动。
        /// 跳掉这一秒，量程才不会被一个假峰顶到天上去、把后面的曲线压扁。
        /// </summary>
        private const int SettleIntervals = 5;

        /// <summary>
        /// 起一个采样线程：每隔 SampleIntervalMs 读一次累计字节数，和上次做差算出瞬时速率。
        ///
        /// 为什么不直接在 16 个工作线程里算：那些线程是"下完一片才报一次"，
        /// 报的点不均匀（有时几百毫秒一个、有时连着来两三个），画出来的横轴疏密不一，
        /// 波形看着会怪。单独一个线程定时采样，横轴才是等距的。
        /// </summary>
        private void StartSampler(SpeedPhase phase)
        {
            // 下载/上传都有多条实现路径（多源并发 + 兜底单源），
            // 换路径时会再调一次 —— 必须先等上一个采样线程退出，
            // 否则两个线程同时发点，波形会叠出锯齿。
            StopSampler();

            Interlocked.Exchange(ref _probeBytes, 0);
            _sampling = true;

            Thread t = new Thread(delegate()
            {
                long lastBytes = Interlocked.Read(ref _probeBytes);
                DateTime lastAt = DateTime.Now;
                double pending = -1;
                int done = 0;

                while (_sampling && !_cancel)
                {
                    Thread.Sleep(SampleIntervalMs);

                    long nowBytes = Interlocked.Read(ref _probeBytes);
                    DateTime nowAt = DateTime.Now;
                    double dt = (nowAt - lastAt).TotalSeconds;

                    // 发的是【上一拍】算出来的值，不是这一拍的 —— 故意慢一拍。
                    //
                    // 原因：一个阶段结束的那一刻，采样窗口一定是残缺的
                    //（线程退出 / 连接收尾，那 200ms 里只跑了很少数据甚至没数据），
                    // 直接发出去，波形尾部就会出现一条垂直砸到 0 的假低谷。
                    // 慢一拍后，最后一个残缺窗口只算"待确认"，不再发出去。
                    if (pending >= 0) FireSample(phase, pending);

                    // 开头那几拍的失真见 SettleIntervals 的注释，直接不发布。
                    done++;
                    if (dt > 0.05 && done > SettleIntervals)
                    {
                        double mbps = (nowBytes - lastBytes) * 8.0 / dt / 1e6;
                        if (mbps < 0) mbps = 0;
                        pending = mbps;
                    }
                    lastBytes = nowBytes;
                    lastAt = nowAt;
                }
            });

            t.IsBackground = true;
            t.Name = "sample";
            _sampler = t;
            t.Start();
        }

        private void StopSampler()
        {
            _sampling = false;

            Thread t = _sampler;
            if (t != null && t.IsAlive)
            {
                // 采样线程 200ms 一轮，等 400ms 足够它退出；
                // 不等的话下一阶段的采样会和它重叠
                try { t.Join(400); }
                catch { }
            }
            _sampler = null;
        }

        private void FireSample(SpeedPhase phase, double mbps)
        {
            Action<SpeedSample> h = Sample;
            if (h == null) return;

            SpeedSample s = new SpeedSample();
            s.Phase = phase;
            s.Mbps = mbps;

            try { h(s); }
            catch { }   // 界面已关闭，不该让测速线程挂掉
        }

        private void Report(SpeedPhase phase, int percent, string msg, SpeedTestResult src = null)
        {
            Action<SpeedTestProgress> h = Progress;
            if (h == null) return;

            SpeedTestProgress p = new SpeedTestProgress();
            p.Phase = phase;
            p.Percent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
            p.Message = msg;

            if (src != null)
            {
                p.PingMs = src.PingMs;
                p.JitterMs = src.JitterMs;
                p.DownloadMbps = src.DownloadMbps;
                p.UploadMbps = src.UploadMbps;
            }

            try { h(p); }
            catch { }
        }

        private static string Fmt(double v)
        {
            if (v < 0) return "—";
            if (v >= 100) return v.ToString("0");
            if (v >= 10) return v.ToString("0.0");
            return v.ToString("0.00");
        }
    }
}
