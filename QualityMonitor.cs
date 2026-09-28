using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 连接质量快照 —— 供 UI 读取的一组只读数据。
    /// </summary>
    public sealed class QualitySample
    {
        /// <summary>是否已有采样数据。</summary>
        public bool HasData;

        /// <summary>本次探测使用的网关地址（可能为空）。</summary>
        public string Gateway = "";

        /// <summary>外网丢包率（0-100，取自最近的滑动窗口）。</summary>
        public int LossPct;

        /// <summary>到网关的丢包率（0-100）。</summary>
        public int GwLossPct;

        /// <summary>外网平均延迟 ms；-1 表示没有有效样本。</summary>
        public long AvgRtt = -1;

        /// <summary>到网关的平均延迟 ms；-1 表示没有有效样本。</summary>
        public long GwAvgRtt = -1;

        /// <summary>最近一次外网延迟 ms；-1 表示超时。</summary>
        public long LastRtt = -1;

        /// <summary>已采集的样本总数。</summary>
        public int SampleCount;

        /// <summary>外网延迟历史序列（-1 = 超时），供画曲线。</summary>
        public List<int> History = new List<int>();

        /// <summary>0=尚未出结论 1=良好 2=一般 3=差。</summary>
        public int Level;

        /// <summary>一句话结论。</summary>
        public string Verdict = "";

        /// <summary>给人看的解释（说人话，指出问题出在哪一段）。</summary>
        public string Detail = "";
    }

    /// <summary>
    /// 连接质量监测 —— 后台线程周期性 ping 网关与外网，统计丢包率与延迟。
    ///
    /// 为什么必须开独立线程：
    ///   ping 一次最长要等 1.5 秒，放进 DispatcherTimer（跑在 UI 线程）会把界面卡住。
    ///   所以这里用后台线程采样，采完写成快照，由 UI 线程自己来取。
    ///
    /// 判读逻辑（这个功能的真正价值，不只是报数字）：
    ///   到网关就丢包        → 问题在"电脑 → 交换机"这一段，属于本地线路
    ///   网关正常、外网丢包  → 问题在"学校出口 / 上级网络"，不是用户的电脑
    ///   网关正常、外网全丢  → 更可能是学校禁了 ICMP 探测，不能一口咬定是出口故障
    /// </summary>
    public sealed class QualityMonitor
    {
        /// <summary>外网探测目标：公共 DNS，不涉及任何校内地址。</summary>
        public const string ExternalHost = "223.5.5.5";

        /// <summary>延迟曲线保留的样本数。</summary>
        private const int HistoryMax = 90;

        /// <summary>计算丢包率 / 平均延迟所用的滑动窗口。</summary>
        private const int LossWindow = 20;

        /// <summary>至少要有几个样本才给结论（避免刚连上就报"网络差"）。</summary>
        private const int MinSamplesForVerdict = 3;

        private const int GwTimeoutMs = 800;
        private const int NetTimeoutMs = 1500;
        private const int IntervalMs = 3000;

        private readonly object _lock = new object();

        private readonly List<int> _history = new List<int>();
        private readonly List<int> _gwHistory = new List<int>();

        private Thread _worker;
        private volatile bool _running;
        private volatile bool _active;

        private string _gateway = "";
        private long _lastRtt = -1;
        private int _lastLevel = -1;

        /// <summary>采样完成时触发（在后台线程上）。UI 订阅后需自行切回 UI 线程再刷新界面。</summary>
        public event Action Updated;

        /// <summary>当前是否在采样。</summary>
        public bool IsActive { get { return _active; } }

        // ==================================================================
        // 生命周期
        // ==================================================================

        /// <summary>
        /// 开始 / 暂停采样。拨号连上时传 true，断开时传 false。
        /// </summary>
        public void SetActive(bool active)
        {
            if (active)
            {
                // 已经在采样就不要再来一遍 —— Reset() 会把已经攒下的样本清空。
                // ApplyState 在不同路径上可能被重复调用，这里必须挡住。
                if (_active) return;

                Reset();
                _active = true;

                if (!_running)
                {
                    _running = true;
                    _worker = new Thread(WorkerLoop);
                    _worker.IsBackground = true;
                    _worker.Name = "QualityMonitor";
                    _worker.Start();
                }
            }
            else
            {
                if (!_active) return;
                _active = false;
                Reset();
            }
        }

        /// <summary>程序退出时调用，结束后台线程。</summary>
        public void Shutdown()
        {
            _active = false;
            _running = false;
            _worker = null;
        }

        private void Reset()
        {
            lock (_lock)
            {
                _history.Clear();
                _gwHistory.Clear();
                _gateway = "";
                _lastRtt = -1;
            }
            _lastLevel = -1;
        }

        // ==================================================================
        // 采样
        // ==================================================================

        private void WorkerLoop()
        {
            while (_running)
            {
                if (_active)
                {
                    try { DoSample(); }
                    catch { }
                }

                // 分片睡眠，让 Shutdown() / SetActive(false) 能立刻生效
                for (int i = 0; i < IntervalMs / 100; i++)
                {
                    if (!_running) return;
                    Thread.Sleep(100);
                }
            }
        }

        private void DoSample()
        {
            string gw = ResolveGateway();
            long gwRtt = gw.Length > 0 ? PingRtt(gw, GwTimeoutMs) : -1;
            long netRtt = PingRtt(ExternalHost, NetTimeoutMs);

            lock (_lock)
            {
                _gateway = gw;
                _lastRtt = netRtt;

                _gwHistory.Add((int)gwRtt);
                while (_gwHistory.Count > HistoryMax) _gwHistory.RemoveAt(0);

                _history.Add((int)netRtt);
                while (_history.Count > HistoryMax) _history.RemoveAt(0);
            }

            Action h = Updated;
            if (h != null)
            {
                try { h(); }
                catch { }
            }

            // 结论发生变化时记一条日志。每 3 秒记一次会把日志刷爆，所以只在结论翻面时记。
            QualitySample s = GetSample();
            if (s.Level != _lastLevel)
            {
                _lastLevel = s.Level;
                Log.Info("网络质量: " + s.Verdict + "（外网丢包 " + s.LossPct
                       + "%，网关丢包 " + s.GwLossPct + "%）");
            }
        }

        // ==================================================================
        // 读取快照
        // ==================================================================

        /// <summary>取一份当前快照（线程安全）。</summary>
        public QualitySample GetSample()
        {
            QualitySample s = new QualitySample();

            lock (_lock)
            {
                s.HasData = _history.Count > 0;
                s.SampleCount = _history.Count;
                s.History = new List<int>(_history);
                s.Gateway = _gateway;
                s.LastRtt = _lastRtt;
                s.LossPct = CalcLoss(_history);
                s.AvgRtt = CalcAvg(_history);
                s.GwLossPct = CalcLoss(_gwHistory);
                s.GwAvgRtt = CalcAvg(_gwHistory);
            }

            Judge(s);
            return s;
        }

        /// <summary>滑动窗口内的丢包率（0-100）。</summary>
        private static int CalcLoss(List<int> seq)
        {
            int n = Math.Min(seq.Count, LossWindow);
            if (n <= 0) return 0;

            int lost = 0;
            for (int i = seq.Count - n; i < seq.Count; i++)
            {
                if (seq[i] < 0) lost++;
            }
            return (int)Math.Round(lost * 100.0 / n);
        }

        /// <summary>滑动窗口内的平均延迟；没有成功样本返回 -1。</summary>
        private static long CalcAvg(List<int> seq)
        {
            int n = Math.Min(seq.Count, LossWindow);
            if (n <= 0) return -1;

            long sum = 0;
            int cnt = 0;
            for (int i = seq.Count - n; i < seq.Count; i++)
            {
                if (seq[i] >= 0) { sum += seq[i]; cnt++; }
            }
            return cnt > 0 ? sum / cnt : -1;
        }

        // ==================================================================
        // 判读
        // ==================================================================

        /// <summary>
        /// 给结论。核心是把"哪一段出了问题"讲清楚，而不是只丢一个数字给用户。
        /// </summary>
        private static void Judge(QualitySample s)
        {
            if (!s.HasData || s.SampleCount < MinSamplesForVerdict)
            {
                s.Level = 0;
                s.Verdict = "正在检测…";
                s.Detail = "已采集 " + s.SampleCount + "/" + MinSamplesForVerdict + " 个样本，稍等几秒。";
                return;
            }

            bool gwBad = s.GwLossPct >= 25;
            bool netDead = s.LossPct >= 100;
            bool netLossy = s.LossPct >= 10;
            bool slow = s.AvgRtt >= 150;

            if (gwBad)
            {
                s.Level = 3;
                s.Verdict = "本地线路不稳定";
                s.Detail = "到网关就丢包（" + s.GwLossPct + "%）。问题在电脑到交换机这一段，"
                         + "先检查网线、水晶头和墙上接口有没有松动。";
                return;
            }

            if (netDead)
            {
                s.Level = 2;
                s.Verdict = "外网探测无响应";
                s.Detail = "到网关正常，但外网完全 Ping 不通。可能是学校限制了 ICMP 探测"
                         + "（这种情况其实上网不受影响），也可能是出口故障。";
                return;
            }

            if (netLossy)
            {
                s.Level = 2;
                s.Verdict = "出口方向有丢包";
                s.Detail = "到网关正常，外网丢包 " + s.LossPct + "%。问题在学校出口方向，"
                         + "不是你的电脑。这种时候只能等，或换个时段再试。";
                return;
            }

            if (s.LossPct > 0)
            {
                s.Level = 2;
                s.Verdict = "偶有丢包";
                s.Detail = "外网丢包 " + s.LossPct + "%，平均延迟 " + Fmt(s.AvgRtt)
                         + "。幅度不大，通常不影响正常使用。";
                return;
            }

            if (slow)
            {
                s.Level = 2;
                s.Verdict = "延迟偏高";
                s.Detail = "没有丢包，但平均延迟 " + Fmt(s.AvgRtt) + "（正常一般低于 100ms）。"
                         + "网关侧 " + Fmt(s.GwAvgRtt) + "，慢在出口方向。";
                return;
            }

            s.Level = 1;
            s.Verdict = "网络良好";
            s.Detail = "无丢包，平均延迟 " + Fmt(s.AvgRtt) + "（网关侧 " + Fmt(s.GwAvgRtt) + "）。";
        }

        private static string Fmt(long ms)
        {
            return ms < 0 ? "—" : ms + " ms";
        }

        // ==================================================================
        // 探测工具
        // ==================================================================

        /// <summary>
        /// 取当前默认网关。优先拨号接口（PPP）—— 同时插着网线又连着 Wi-Fi 时，
        /// 不优先取 PPP 会拿到另一块网卡的网关，测的就不是拨号这条链路了。
        /// </summary>
        private static string ResolveGateway()
        {
            try
            {
                NetworkInterface ppp = null;
                NetworkInterface other = null;

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                    {
                        if (ppp == null) ppp = ni;
                    }
                    else if (other == null)
                    {
                        other = ni;
                    }
                }

                NetworkInterface[] order = new NetworkInterface[] { ppp, other };
                foreach (NetworkInterface ni in order)
                {
                    if (ni == null) continue;

                    foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (g.Address == null) continue;
                        if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        string a = g.Address.ToString();
                        if (a != "0.0.0.0") return a;
                    }
                }
            }
            catch { }
            return "";
        }

        private static long PingRtt(string host, int timeoutMs)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(host, timeoutMs);
                    if (r != null && r.Status == IPStatus.Success) return r.RoundtripTime;
                }
            }
            catch { }
            return -1;
        }

        // ==================================================================
        // 链路速率
        // ==================================================================

        /// <summary>
        /// 取当前链路速率（网卡协商速率），单位 Mbps。取不到返回 0。
        ///
        /// 只认物理网卡：
        ///   · 拨号（PPP）接口报的速率没有参考价值，直接跳过；
        ///   · VPN / 虚拟机 / 加速器的虚拟网卡会谎报速率，必须排掉。
        /// kind 输出"有线"或"无线"。
        /// </summary>
        public static long GetLinkSpeedMbps(out string kind)
        {
            kind = "";
            long best = 0;
            bool bestWired = false;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    bool wired;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        wired = false;
                    }
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                          || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                    {
                        wired = true;
                    }
                    else
                    {
                        continue;   // Loopback / Ppp / Tunnel / 未知类型一律跳过
                    }

                    if (IsVirtualAdapter(ni)) continue;

                    long sp = 0;
                    try { sp = ni.Speed; }
                    catch { }
                    if (sp <= 0) continue;

                    long mbps = sp / 1000000L;
                    if (mbps <= 0) continue;

                    // 有线优先；同为有线（或同为无线）时取更快的那个
                    if (best == 0
                        || (wired && !bestWired)
                        || (wired == bestWired && mbps > best))
                    {
                        best = mbps;
                        bestWired = wired;
                    }
                }
            }
            catch { }

            if (best > 0) kind = bestWired ? "有线" : "无线";
            return best;
        }

        /// <summary>识别虚拟网卡 —— 它们的 Speed 字段不代表真实链路。</summary>
        private static bool IsVirtualAdapter(NetworkInterface ni)
        {
            string s = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();

            string[] bad = new string[]
            {
                "virtual", "vmware", "hyper-v", "vethernet", "tap-", "tap ", "tun",
                "vpn", "loopback", "bluetooth", "tailscale", "zerotier", "wintun",
                "npcap", "oray", "gameviewer", "radmin", "hamachi", "sangfor"
            };

            foreach (string b in bad)
            {
                if (s.IndexOf(b) >= 0) return true;
            }
            return false;
        }
    }
}
