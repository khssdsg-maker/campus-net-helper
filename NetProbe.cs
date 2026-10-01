using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 统一的"到底能不能上网"探测器。
    ///
    /// 为什么需要它（2026-09-30 的教训）：
    ///   程序原先用两套完全不同的判据判断"有没有网"：
    ///     · 拨号模式 —— 看系统里有没有已连接的 PPP 接口（DialEngine.IsDialConnected）
    ///     · 网页认证 —— 临时写了个 ProbeInternetStrict()
    ///   结果网页认证模式下，认证成功之后系统里**依旧没有 PPP 接口**，
    ///   主页那套判据一直认为"没连上"，于是状态条停在「尚未连接」、
    ///   在线时长不动、丢包延迟不采样 —— 用户看到的就是"主页没有任何网络信息"。
    ///
    ///   其实"上没上网"只有一个可靠答案：**发一个请求看能不能通**。
    ///   本类就是那唯一的答案，主页和认证窗口都调它，判据从此不会打架。
    ///
    /// 探测手法（沿用认证窗口验证过的那套）：
    ///   请求小米的 generate_204 接口。真正连通时返回 204 空响应；
    ///   如果被认证网关劫持，拿到的是 302 或 200 + 一段认证页 HTML。
    ///   → 只有「204」或「200 且响应体极短」才算真通。
    ///   ⚠️ 不敢用"收到任何响应就算通"：认证前的劫持页也是响应，那样永远判成功。
    ///
    /// 缓存：主页每秒都会问一次，不能每秒真发一次请求。
    ///   结果缓存 <see cref="CacheMs"/> 毫秒（需要立刻确认真实结果时用 Force 参数穿透缓存）。
    /// </summary>
    internal static class NetProbe
    {
        /// <summary>探测目标。公共连通性检测接口，不涉及任何学校内网地址。</summary>
        private const string ProbeUrl = "http://connect.rom.miui.com/generate_204";

        /// <summary>结果缓存时长（毫秒）。主页 1 秒刷一次，5 秒一次真探测足够灵敏也不扰民。</summary>
        private const int CacheMs = 5000;

        private static readonly object _lock = new object();
        private static bool _cached;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static int _probing;

        /// <summary>最近一次探测结果（可能来自缓存）。</summary>
        /// <param name="force">true = 无视缓存，立刻真发一次请求。用户点「立即连接」这类场景用。</param>
        public static bool Online(bool force)
        {
            lock (_lock)
            {
                if (!force && _cachedAt != DateTime.MinValue
                    && (DateTime.Now - _cachedAt).TotalMilliseconds < CacheMs)
                {
                    return _cached;
                }
            }

            bool ok = ProbeOnce();

            lock (_lock)
            {
                _cached = ok;
                _cachedAt = DateTime.Now;
            }
            return ok;
        }

        /// <summary>用缓存结果（主页每秒调用走这条）。</summary>
        public static bool Online()
        {
            return Online(false);
        }

        /// <summary>
        /// 只读缓存结果；**缓存过期也不阻塞调用方** —— 让后台线程去真探
        /// （WarmUpAsync 自带防重入），先把已知的值还给调用方。
        ///
        /// ⚠️ 为什么需要它（2026-10-01 审计发现）：
        ///   上面的 Online(false) 在缓存过期时会在**调用方线程**同步发请求，
        ///   而 ProbeOnce 的超时是 6 秒。OnTick 是 DispatcherTimer 回调（UI 线程），
        ///   它每秒都会问一次"能不能上网" —— 于是网页认证模式下一旦断网，
        ///   每 5 秒（缓存过期）就有一次 6 秒的 UI 冻结：窗口拖不动、按钮点不了，
        ///   而且会无限循环下去。项目早就写明"真发请求绝不能在 UI 线程上做"，
        ///   但 Online() 自己没设防。
        ///
        /// ✅ UI 线程一律用这个；需要"立刻要真实结果"的场景（用户主动点按钮）
        ///    才用 Online(true) 穿透缓存，且那类调用点本来就在后台线程上。
        /// </summary>
        public static bool OnlineStale()
        {
            lock (_lock)
            {
                if (_cachedAt != DateTime.MinValue
                    && (DateTime.Now - _cachedAt).TotalMilliseconds < CacheMs)
                {
                    return _cached;
                }
            }

            // 缓存过期了：让后台去探，本次先把"上一次知道的结果"返回去。
            // 代价是状态可能晚几秒才更新，但界面永远不会因此卡住。
            WarmUpAsync();
            return LastKnown();
        }

        /// <summary>最近一次的探测结果，不触发新探测。从未探测过时返回 false。</summary>
        public static bool LastKnown()
        {
            lock (_lock) { return _cached; }
        }

        /// <summary>手动丢弃缓存 —— 认证刚提交完时调用，免得读到提交前的旧结果。</summary>
        public static void Invalidate()
        {
            lock (_lock) { _cachedAt = DateTime.MinValue; }
        }

        /// <summary>
        /// 后台预热一次（不阻塞调用方）。主页在 UI 线程上刷新，
        /// 真发请求要几百毫秒，绝不能在 UI 线程上做。
        /// </summary>
        public static void WarmUpAsync()
        {
            if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { Online(true); }
                catch { }
                finally { Interlocked.Exchange(ref _probing, 0); }
            });
        }

        // ------------------------------------------------------------------

        private static bool ProbeOnce()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ProbeUrl);
                req.Method = "GET";
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;

                // 不跟随跳转 —— 跳转本身就说明被网关劫持了，那就是还没认证
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;

                // 绕开系统代理直连：校园网认证是链路层的事，走代理判断会失真
                req.Proxy = null;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code == 204) return true;   // 正常联通

                    // 200 也可能是劫持页：只有响应体极小才认为是真的 204 语义
                    using (System.IO.Stream s = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[512];
                        int n = s == null ? 0 : s.Read(buf, 0, buf.Length);
                        return n <= 8;
                    }
                }
            }
            catch (WebException wex)
            {
                // 有响应但状态码不是 2xx（多半是 302 跳认证页）→ 不算通
                if (wex.Response != null) return false;

                // 连响应都没有：可能是超时，也可能是 ICMP/HTTP 被出口拦了。
                // 这里 fail-open 会误报"有网"，fail-closed 会误报"断网"，
                // 两难。选 fail-closed，但由调用方配合"上一次的结果"做平滑
                // （见 MainWindow 里的连续失败判定）。
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ==================================================================
        // 适配器选择 —— "该看哪块网卡"
        // ==================================================================

        /// <summary>
        /// 挑出当前**代表真实上网链路**的那块网卡。
        ///
        /// 优先级：
        ///   1. 已连接的 PPP 接口（拨号模式，地址是运营商分的，最准）
        ///   2. 物理网卡（有线优先于无线）
        /// 虚拟网卡全部排除 —— 本机装着向日葵、UU远程、FlClash 的 TUN、Watt Toolkit 等，
        /// 它们都是 Up 状态、也报 Speed，会把结果带偏。
        /// 取不到返回 null。
        ///
        /// ⚠️ 拨号模式下这个结果是 PPP 接口；**网页认证模式没有 PPP 接口**，
        ///    所以自动落到物理网卡上 —— 这正是我们要的（见 PickTrafficAdapter）。
        /// </summary>
        public static NetworkInterface PickPrimaryAdapter()
        {
            NetworkInterface ppp = null;
            NetworkInterface wired = null;
            NetworkInterface wireless = null;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    // 拨号接口：NetConnectionID 里带"宽带"的也算（国产 PPPoE 常见命名）
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                    {
                        if (ppp == null) ppp = ni;
                        continue;
                    }

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (IsVirtualAdapter(ni)) continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                        || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                    {
                        if (wired == null) wired = ni;
                    }
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        if (wireless == null) wireless = ni;
                    }
                }
            }
            catch { }

            if (ppp != null) return ppp;
            if (wired != null) return wired;
            return wireless;
        }

        /// <summary>
        /// 挑出**该统计流量**的那块网卡。
        ///
        /// 为什么不能简单用 PickPrimaryAdapter：
        ///   它是"显示用"的 —— 拨号模式下会挑 PPP 接口（主页要显示运营商给的地址）。
        ///   但流量统计要的是"数据真正过的那块"，两种模式答案不同：
        ///
        ///   · 拨号模式：数据确实走 PPP 接口，统计 PPP 是对的。
        ///     （PPP 的字节计数是独立的，物理网卡那边也会记一份，
        ///      但只统计 PPP 才不会把局域网共享的流量算进来。）
        ///   · 网页认证模式：**根本没有 PPP 接口**，数据直接走物理网卡。
        ///     原先这里写死只认 Ppp，于是认证模式下永远统计到 0 ——
        ///     用户看到的就是"网速曲线是平的、上下行一直是 0"。
        ///     这是 2026-09-30 修主页显示问题时同一类错误的残留。
        ///
        /// 取不到返回 null。
        /// </summary>
        public static NetworkInterface PickTrafficAdapter()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ppp) continue;
                    return ni;                     // 有拨号就统计拨号
                }
            }
            catch { }

            // 没有拨号接口（网页认证模式，或者还没连）→ 统计真实物理网卡。
            // 虚拟网卡被 PickPrimaryAdapter 排掉了，所以不会把加速器的流量算进来。
            return PickPrimaryAdapter();
        }

        /// <summary>
        /// 某块网卡上累计收发的字节数（收 + 发）。
        /// 取不到时把 in/out 都置 0 并返回 false —— 调用方据此判断"这一轮没采到"，
        /// 而不是把 0 当成"没流量"（那样会在切网卡时凭空画出一个掉到 0 的坑）。
        /// </summary>
        public static bool TrafficOf(NetworkInterface ni, out long rx, out long tx)
        {
            rx = 0; tx = 0;
            if (ni == null) return false;
            try
            {
                IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                rx = st.BytesReceived;
                tx = st.BytesSent;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 取该网卡的本机 IPv4 地址；没有返回空串。
        /// 跳过 169.254.x.x（DHCP 没拿到地址时的自动私有地址，不算"有 IP"）。
        /// </summary>
        public static string Ipv4Of(NetworkInterface ni)
        {
            if (ni == null) return "";
            try
            {
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    return s;
                }
            }
            catch { }
            return "";
        }

        /// <summary>取该网卡的默认网关；没有返回空串。</summary>
        public static string GatewayOf(NetworkInterface ni)
        {
            if (ni == null) return "";
            try
            {
                foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                {
                    if (g.Address == null) continue;
                    if (g.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string a = g.Address.ToString();
                    if (a != "0.0.0.0") return a;
                }
            }
            catch { }
            return "";
        }

        /// <summary>取该网卡的 DNS；没有返回空串。</summary>
        public static string DnsOf(NetworkInterface ni)
        {
            if (ni == null) return "";
            try
            {
                foreach (IPAddress d in ni.GetIPProperties().DnsAddresses)
                {
                    if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return d.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 这块网卡是人话里的什么？—— 用于界面上让用户确认"程序看的是哪条线"。
        /// 返回 "有线" / "无线" / "拨号" / ""（认不出）。
        /// </summary>
        public static string KindOf(NetworkInterface ni)
        {
            if (ni == null) return "";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) return "拨号";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) return "有线";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "无线";
            return "";
        }

        /// <summary>
        /// 虚拟网卡关键词黑名单 —— **全项目唯一的一份**，QualityMonitor 也引这里。
        ///
        /// ⚠️ 为什么必须只有一份（2026-10-01 审计确认）：
        ///   以前 NetProbe 和 QualityMonitor 各存了一份，而 QualityMonitor 那份短了 11 个词
        ///   （少了 clash / flclash / docker / watt / steam++ / virtualbox / wsl 等）。
        ///   两份名单一分叉就出事 —— 装了 Docker 或加速器的机器上：
        ///     · "链路速率"会拿虚拟网卡的假 1G 来显示；
        ///     · "到网关"会选中没有真网关的虚拟网卡 → 丢包恒 100% →
        ///       永远给用户下"本地线路不稳定，先换网线"的错误结论。
        /// </summary>
        internal static readonly string[] VirtualAdapterKeywords = new string[]
        {
            "virtual", "vmware", "hyper-v", "vethernet", "tap-", "tap ", "tun",
            "vpn", "loopback", "bluetooth", "tailscale", "zerotier", "wintun",
            "npcap", "oray", "gameviewer", "radmin", "hamachi", "sangfor",
            // 本机实际装过的加速器 / 远程工具（2026-09-22 摸清）
            "watt", "steam++", "clash", "flclash", "uu加速", "uu booster",
            "小米", "miracast", "virtualbox", "docker", "wsl"
        };

        /// <summary>识别虚拟网卡。QualityMonitor 也引这份名单 —— 别再各存一份。</summary>
        private static bool IsVirtualAdapter(NetworkInterface ni)
        {
            string s = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();

            foreach (string b in VirtualAdapterKeywords)
            {
                if (s.IndexOf(b) >= 0) return true;
            }
            return false;
        }
    }
}
