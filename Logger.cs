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
