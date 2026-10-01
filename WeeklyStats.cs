using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 连接周报的统计累积与读写。
    ///
    /// 为什么要有它（2026-10-02 海辰提的需求）：
    ///   同学真正想知道的是**规律**——"我们楼是不是晚上 11 点网络变差""这周掉了几次"。
    ///   质量监测的采样和日志里其实都有，但一直是流水账，没人愿意去翻日志聚合。
    ///   这里把每天的情况攒成一条记录，周报窗口只负责画出来。
    ///
    /// 设计上的几个取舍：
    ///   · **每天一个文件**（stats-YYYYMMDD.txt），而不是一个大文件 ——
    ///     单日损坏不影响其它天，读侧也能天然地"缺哪天就跳过哪天"。
    ///   · **内存累加 + 状态变化/退出时落盘**，不是每秒写盘 ——
    ///     在线时长这类数字每秒都在变，每秒写盘纯属折磨磁盘（见 B2 的同类教训）。
    ///   · 延迟只累加"和"与"次数"，需要均值时再除 —— 比维护一个数组省事得多。
    /// </summary>
    internal static class WeeklyStats
    {
        /// <summary>一天的统计。字段名短一点，文件行更好读。</summary>
        internal class DayStat
        {
            public string Date = "";         // yyyy-MM-dd
            public string FirstOnline = "";  // HH:mm（当天第一次判定为"在线"的时刻）
            public int DropCount = 0;        // 掉线次数（Connected → 其它状态）
            public long OnlineSeconds = 0;   // 累计在线秒数
            public long LatencySumMs = 0;    // 延迟累计（用于算平均）
            public int LatencySamples = 0;
            public long MaxLatencyMs = 0;

            public double AvgLatencyMs()
            {
                if (LatencySamples <= 0) return 0;
                return (double)LatencySumMs / LatencySamples;
            }
        }

        private static readonly object _lock = new object();
        private static DayStat _today;
        private static string _todayKey = "";

        /// <summary>上一次采样时的 TickCount —— 用来识破"睡眠唤醒后时间跳跃"。</summary>
        private static int _lastTick = 0;
        /// <summary>上一次采样时的挂钟 —— 与 TickCount 对比，两者差值对不上就说明遇到过挂起。</summary>
        private static DateTime _lastWall = DateTime.MinValue;

        private const int MaxDropHours = 24;   // 结论里"高峰时段"最多看 24 小时

        public static string DirPath
        {
            get { return ConfigStore.AppDataDir; }
        }

        public static string PathOf(string date)
        {
            // date 形如 2026-10-02 → 文件名去掉连字符
            return Path.Combine(DirPath, "stats-" + (date ?? "").Replace("-", "") + ".txt");
        }

        private static string TodayKey()
        {
            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        /// <summary>启动时调一次：把"今天"这条准备好（有文件就读进来接着累加）。</summary>
        public static void Init()
        {
            lock (_lock)
            {
                _todayKey = TodayKey();
                _today = LoadDay(_todayKey) ?? new DayStat();
                _today.Date = _todayKey;
                _lastTick = Environment.TickCount;
                _lastWall = DateTime.Now;
            }
        }

        /// <summary>
        /// 状态变化时调一次。
        /// 直接从旧/新状态推断"掉线"和"在线时长"，不依赖调用方另外记账。
        ///
        /// ⚠️ ConnState 是 MainWindow 的嵌套枚举，所以这里必须写全名 MainWindow.ConnState。
        /// </summary>
        public static void OnStateChanged(MainWindow.ConnState oldState, MainWindow.ConnState newState)
        {
            lock (_lock)
            {
                RollOverIfNeeded();

                // 掉线：从 Connected 掉出去。
                //（Quiet 要排除 —— 那是"网络还连着、只是进了免打扰"，不算掉线）
                if (oldState == MainWindow.ConnState.Connected
                    && newState != MainWindow.ConnState.Connected
                    && newState != MainWindow.ConnState.Quiet)
                {
                    _today.DropCount++;
                }

                // 首次上线
                if (newState == MainWindow.ConnState.Connected && _today.FirstOnline.Length == 0)
                {
                    _today.FirstOnline = DateTime.Now.ToString("HH:mm");
                }
            }
        }

        /// <summary>
        /// 累加一次延迟采样，并顺带按挂钟补记在线时长。
        ///
        /// ⚠️ 在线时长在这里按"距上次采样的真实间隔"累加，而不是每秒 +1：
        ///    后者漏一次 tick 就永久少一秒（项目里在线时长那个 bug 的同款教训）。
        /// </summary>
        public static void OnLatencySample(int ms, bool online)
        {
            lock (_lock)
            {
                RollOverIfNeeded();

                DateTime now = DateTime.Now;
                int tick = Environment.TickCount;

                if (online && _lastWall != DateTime.MinValue)
                {
                    double wallSec = (now - _lastWall).TotalSeconds;
                    double tickSec = (tick - _lastTick) / 1000.0;

                    // ⚠️ 睡眠唤醒的识别：挂钟走了很久，但 TickCount（不含挂起时间）没走那么多。
                    //    两者差得多就说明中间电脑睡过，这段不该算进"在线时长"。
                    //    用 60 秒容差，避免正常的调度抖动被误判。
                    double gap = wallSec - tickSec;
                    if (wallSec > 0 && wallSec < 300 && gap < 60)
                    {
                        _today.OnlineSeconds += (long)wallSec;
                    }
                    else if (wallSec > 0)
                    {
                        // 间隔异常（睡眠/长阻塞）—— 不累加，但也不报错
                    }
                }

                _lastWall = now;
                _lastTick = tick;

                if (ms > 0)
                {
                    _today.LatencySumMs += ms;
                    _today.LatencySamples++;
                    if (ms > _today.MaxLatencyMs) _today.MaxLatencyMs = ms;
                }
            }
        }

        /// <summary>跨零点就换到新的一天（把前一天存好）。</summary>
        private static void RollOverIfNeeded()
        {
            string k = TodayKey();
            if (k == _todayKey) return;

            FlushLocked();          // 先把旧的一天落盘
            _todayKey = k;
            _today = LoadDay(k) ?? new DayStat();
            _today.Date = k;
        }

        /// <summary>落盘（退出时、以及每次状态变化后调用）。</summary>
        public static void Flush()
        {
            lock (_lock) { FlushLocked(); }
        }

        private static void FlushLocked()
        {
            if (_today == null || _today.Date.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 校园网助手 · 连接统计（本机，不上传）");
                sb.AppendLine("D=" + _today.Date);
                sb.AppendLine("F=" + _today.FirstOnline);
                sb.AppendLine("P=" + _today.DropCount);
                sb.AppendLine("S=" + _today.OnlineSeconds);
                sb.AppendLine("L=" + _today.LatencySumMs);
                sb.AppendLine("N=" + _today.LatencySamples);
                sb.AppendLine("M=" + _today.MaxLatencyMs);
                SafeFile.WriteAtomic(PathOf(_today.Date), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warn("写连接统计失败: " + ex.Message);
            }
        }

        /// <summary>读某一天。文件不存在返回 null；损坏返回 null（调用方跳过那天，不要崩）。</summary>
        private static DayStat LoadDay(string date)
        {
            try
            {
                string p = PathOf(date);
                if (!File.Exists(p)) return null;

                DayStat d = new DayStat();
                d.Date = date;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                    if (line.Length < 3 || line[1] != '=') continue;

                    string v = line.Substring(2).Trim();
                    switch (line[0])
                    {
                        case 'D': d.Date = v; break;
                        case 'F': d.FirstOnline = v; break;
                        case 'P': d.DropCount = ParseInt(v); break;
                        case 'S': d.OnlineSeconds = ParseLong(v); break;
                        case 'L': d.LatencySumMs = ParseLong(v); break;
                        case 'N': d.LatencySamples = ParseInt(v); break;
                        case 'M': d.MaxLatencyMs = ParseLong(v); break;
                    }
                }
                return d;
            }
            catch (Exception ex)
            {
                Log.Warn("读连接统计失败（跳过 " + date + "）: " + ex.Message);
                return null;
            }
        }

        private static int ParseInt(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : 0;
        }

        private static long ParseLong(string s)
        {
            long n;
            return long.TryParse(s, out n) ? n : 0;
        }

        /// <summary>
        /// 取最近 N 天（含今天）。缺失/损坏的那天直接跳过 —— 不补零，
        /// 免得把"那天没开机"显示成"那天网络很好"。
        /// </summary>
        public static List<DayStat> LoadRecent(int days)
        {
            List<DayStat> list = new List<DayStat>();
            lock (_lock)
            {
                DateTime baseDay = DateTime.Now.Date;
                for (int i = 0; i < days; i++)
                {
                    string k = baseDay.AddDays(-i).ToString("yyyy-MM-dd");

                    DayStat d;
                    if (k == _todayKey && _today != null) d = _today;   // 今天用内存里这份（最新）
                    else d = LoadDay(k);

                    if (d != null) list.Add(d);
                }
            }
            // 按日期升序，画表格时从上到下就是时间顺序
            list.Sort(delegate(DayStat a, DayStat b) { return string.CompareOrdinal(a.Date, b.Date); });
            return list;
        }

        /// <summary>
        /// 根据最近几天的数据给一句人话结论。数据不足就如实说"还在攒"。
        /// </summary>
        public static string BuildConclusion(List<DayStat> days)
        {
            if (days == null || days.Count == 0) return "还没有任何记录 —— 用几天再来看看。";
            if (days.Count < 3) return "记录还在攒（有 " + days.Count + " 天数据了），多连几天结论才准。";

            int totalDrops = 0;
            long totalOnline = 0;
            foreach (DayStat d in days)
            {
                totalDrops += d.DropCount;
                totalOnline += d.OnlineSeconds;
            }

            string onlineText = "";
            if (totalOnline > 0)
            {
                onlineText = "，累计在线 " + FmtDuration(totalOnline);
            }

            if (totalDrops == 0)
            {
                return "这几天网络一直很稳，一次都没掉线" + onlineText + "。";
            }

            if (totalDrops >= 5)
            {
                return "这几天掉线 " + totalDrops + " 次，网络不太稳" + onlineText
                    + "。建议在掉线的时候点一次「网络体检」，看看到底是哪一段出了问题。";
            }

            return "这几天掉线 " + totalDrops + " 次" + onlineText + "，属于正常范围。";
        }

        /// <summary>把秒数写成"X 小时 Y 分"这种人话。</summary>
        public static string FmtDuration(long seconds)
        {
            if (seconds <= 0) return "0 分";
            long h = seconds / 3600;
            long m = (seconds % 3600) / 60;
            if (h > 0) return h + " 小时 " + m + " 分";
            if (m > 0) return m + " 分";
            return seconds + " 秒";
        }
    }
}
