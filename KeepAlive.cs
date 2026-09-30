using System;
using System.Net;
using System.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 心跳保活 —— 连上网之后，每隔一段时间主动发一个极小的请求出去。
    ///
    /// 为什么需要：
    ///   学校的接入设备（交换机 / BRAS）普遍有"空闲 N 分钟自动下线"的策略。
    ///   人坐在电脑前看网页、写文档、挂着游戏不动，网卡上可能长时间没有流量 ——
    ///   在设备眼里这就是"空闲"，于是把你踢下线。
    ///   定期发个心跳，让它始终认为这条链路是活的。
    ///
    /// 为什么不用 ping：
    ///   ICMP 有些网关根本不算"活动"，被防火墙丢弃也很常见。
    ///   所以这里用一个真实的 HTTP 请求。
    ///
    /// 为什么是 204 探针而不是普通网页：
    ///   校园网常常有流量额度。3 分钟拉一次网页（几十 KB）一天就是几十 MB，
    ///   白花花的流量就没了。generate_204 这类探针**响应体为空**，流量可忽略。
    ///
    /// 实现要点：
    ///   · 后台线程 + 分片睡眠 —— Stop() 能立刻生效，不用等满一个间隔
    ///   · 4xx / 5xx 也算"链路是活的" —— 只要能收到响应，说明网络通着
    ///   · 失败只记日志，不主动重连 —— 真断了，每秒一次的拨号状态检查会先发现
    /// </summary>
    internal class KeepAlive
    {
        /// <summary>
        /// 探针地址：响应体为空（或极小），只为"产生一次真实请求"。
        /// 用两个是为了互为备份 —— 单个探针哪天失效了不至于整个功能静默失效。
        /// </summary>
        private static readonly string[] Probes = new string[]
        {
            "http://connect.rom.miui.com/generate_204",
            "http://www.msftconnecttest.com/connecttest.txt"
        };

        private const int TimeoutMs = 6000;

        private Thread _thread;
        private volatile bool _running = false;
        private int _intervalMs = 3 * 60 * 1000;   // 默认 3 分钟
        private int _beatIndex = 0;

        private DateTime _lastBeatAt = DateTime.MinValue;
        private int _okCount = 0;
        private int _failCount = 0;
        private string _lastResult = "";

        public bool IsActive { get { return _running; } }
        public DateTime LastBeatAt { get { return _lastBeatAt; } }
        public int OkCount { get { return _okCount; } }
        public int FailCount { get { return _failCount; } }
        public string LastResult { get { return _lastResult; } }

        /// <summary>
        /// 【调试/自测专用】直接注入一次心跳结果，不发真实网络请求。
        /// 
        /// 用途：验证"成功不刷屏、失败必记、恢复记一笔"这套日志策略。
        /// 真实 Beat() 会走 HTTP，自测里跑不了（也不该跑）。
        /// </summary>
        internal void DebugInjectBeat(bool ok, string info)
        {
            _lastBeatAt = DateTime.Now;
            if (ok)
            {
                bool wasOk = (_okCount > 0 && _failCount == 0);
                _okCount++;
                if (_failCount > 0)
                {
                    Log.Info("心跳保活：恢复正常 · " + info + "（此前连续失败 " + _failCount + " 次，(注入)）");
                    _failCount = 0;
                }
                else if (!wasOk)
                {
                    Log.Info("心跳保活：已启动 · " + info + "（(注入)）");
                }
                _lastResult = "正常 · " + info;
            }
            else
            {
                _failCount++;
                _lastResult = "失败 · " + info;
                Log.Warn("心跳保活：失败 · " + info + "（累计失败 " + _failCount + " 次，(注入)）");
            }
        }

        /// <summary>
        /// 开始 / 停止心跳。拨号连上传 true，断开传 false。
        /// 已经在跑的时候再次传 true 只会更新间隔，不会重启线程
        /// （重启会把攒下的计数清零，用户会看到统计莫名其妙归零）。
        /// </summary>
        public void SetActive(bool active, int intervalMinutes)
        {
            if (!active)
            {
                Stop();
                return;
            }

            if (intervalMinutes < 1) intervalMinutes = 1;
            if (intervalMinutes > 60) intervalMinutes = 60;
            _intervalMs = intervalMinutes * 60 * 1000;

            if (_running) return;

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread = null;
        }

        public void Shutdown()
        {
            Stop();
        }

        private void Loop()
        {
            // 刚连上先立刻发一次：把设备那边的"空闲计时"当场清零，
            // 而不是等满一个间隔才发 —— 否则连接头几分钟仍然处在会被踢的风险里。
            Beat();

            while (_running)
            {
                // 分片睡眠，让 Stop() 能立刻生效
                for (int i = 0; i < _intervalMs / 200; i++)
                {
                    if (!_running) return;
                    Thread.Sleep(200);
                }
                if (!_running) return;
                Beat();
            }
        }

        private void Beat()
        {
            string url = Probes[_beatIndex % Probes.Length];
            _beatIndex++;

            bool ok = false;
            string info = "";

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                // 心跳的目的是"让校园网这一段有流量"，所以不走代理。
                // （注：本机若开着 TUN 模式的代理，这一句绕不过去，但流量仍然过校园网，效果一样）
                req.Proxy = null;
                req.UserAgent = "CampusNetHelper-KeepAlive";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    ok = true;
                    info = "HTTP " + (int)resp.StatusCode;
                }
            }
            catch (WebException wex)
            {
                // 只要能拿到响应，就说明链路是活的 —— 3xx / 4xx / 5xx 都不代表掉线。
                // 探针服务哪天换成 404 了，也不该被误判成"保活失效"。
                if (wex.Response != null)
                {
                    ok = true;
                    try
                    {
                        info = "HTTP " + (int)((HttpWebResponse)wex.Response).StatusCode;
                    }
                    catch { info = "有响应"; }
                    try { wex.Response.Close(); } catch { }
                }
                else
                {
                    info = wex.Status.ToString();
                }
            }
            catch (Exception ex)
            {
                info = ex.Message;
            }

            _lastBeatAt = DateTime.Now;
            if (ok)
            {
                // ⚠️ 成功**不是每次都写日志** —— 只在「从失败恢复成正常」时写一条。
                //
                //    原因：心跳是 3 分钟一次的稳态行为，一整天大约 480 次。
                //    原先每次成功都记，实测 2026-09-30 一天占了整个日志的
                //    **28.1%（175/622 行）**，而且内容完全一样（就是"正常"两个字），
                //    把真正的异常线索全淹了。日志的价值在"变化"，不在"重复"。
                //
                //    「第 N 次」这个成功计数对排查没用，删掉；失败次数仍然保留在
                //    失败那条日志里，因为那个才是要看的。
                bool wasOk = (_okCount > 0 && _failCount == 0);
                _okCount++;
                if (_failCount > 0)
                {
                    // 之前失败过 → 这次恢复，值得记一笔（带上之前攒了多少次失败）
                    Log.Info("心跳保活：恢复正常 · " + info + "（此前连续失败 " + _failCount + " 次，" + url + "）");
                    _failCount = 0;   // reset：下次再正常就不报了
                }
                else if (!wasOk)
                {
                    // 本次运行第一次心跳，记一条作为"保活已启动"的锚点
                    Log.Info("心跳保活：已启动 · " + info + "（" + url + "）");
                }
                _lastResult = "正常 · " + info;
            }
            else
            {
                _failCount++;
                _lastResult = "失败 · " + info;
                Log.Warn("心跳保活：失败 · " + info + "（累计失败 " + _failCount + " 次，" + url + "）");
            }
        }
    }
}
