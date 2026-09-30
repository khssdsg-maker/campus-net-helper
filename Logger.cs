using System;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 滚动日志。
    /// 输出目录: %AppData%\CampusNetHelper\logs\campusnet-YYYYMMDD.log
    /// 按天分文件，自动清理 7 天前的旧日志；全程静默失败，绝不影响主流程。
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string logDir;
        private static DateTime lastCleanup = DateTime.MinValue;

        private static string Dir
        {
            get
            {
                if (logDir == null)
                {
                    logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "CampusNetHelper", "logs");
                    try { if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir); }
                    catch { logDir = ""; }
                }
                return logDir;
            }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        /// <summary>
        /// 【自测模式】打开后，所有日志都不写文件。
        ///
        /// 为什么需要：`--selftest` 会把整个 UI 和几个后台子系统（网络质量探测、
        /// 心跳、网速采样）真的跑起来，这些子系统会往**用户当天的正式日志**里写。
        /// 2026-09-30 晚在开发机上反复跑自测，一天下来光"界面自测结果已写入"
        /// 就有 24 条（占 3.9%），"网络质量/心跳"也被灌了一堆 —— 用户在真出问题时
        /// 翻日志，看到的会是我测试留下的噪音，而不是他自己的运行记录。
        ///
        /// 所以自测一进来就把这个开关打开：调用方仍能正常调用 Log，只是不落盘，
        /// 不用到处改调用点。
        /// </summary>
        public static bool SelfTestQuiet = false;

        /// <summary>
        /// 【自测专用】累计"真正写出去过"的日志条数。
        ///
        /// 自测模式下日志不落盘（SelfTestQuiet），但用例仍然需要验证
        /// "某个动作到底记了几条" —— 这时候点数就没法靠读日志文件了。
        /// 所以在这里记一个只增不减的计数，用例读前后差值即可。
        ///
        /// 注意：自测模式下也照常累加，不受 SelfTestQuiet 影响。
        /// </summary>
        public static long Emitted = 0;

        public static void ResetEmitted()
        {
            lock (Sync) { Emitted = 0; }
        }

        public static void Error(string context, Exception ex)
        {
            string detail = ex != null
                ? (ex.GetType().Name + ": " + ex.Message + " | " + (ex.StackTrace ?? ""))
                : "<null exception>";
            Write("ERROR", context + " :: " + detail);
        }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Sync) { Emitted++; }
                if (SelfTestQuiet) return;   // 自测模式：不污染正式日志
                if (string.IsNullOrEmpty(Dir)) return;
                string file = Path.Combine(Dir, "campusnet-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + (msg ?? "") + "\r\n";
                lock (Sync)
                {
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
                CleanupOldLogs();
            }
            catch { }
        }

        private static void CleanupOldLogs()
        {
            if ((DateTime.Now - lastCleanup).TotalHours < 24) return;
            lastCleanup = DateTime.Now;
            try
            {
                foreach (string f in Directory.GetFiles(Dir, "campusnet-*.log"))
                {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
