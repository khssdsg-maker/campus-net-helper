using System;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 安全的文件写入：原子替换 + 旧版留档。
    ///
    /// 为什么要有这个类（2026-09-29 事故）：
    ///   程序里原来用 File.WriteAllText 写配置 —— 它是"先把文件清空、再写内容"。
    ///   文件被清空之后、内容还没落盘之前如果进程被杀（关机时"关闭到托盘"导致程序
    ///   拒绝退出、被系统强制结束），文件就只剩一个 UTF-8 文件头（3 字节）。
    ///   用户的账号就是这么丢的：次日开机读到 0 个账号 → 不拨号、也不提示。
    ///
    ///   排查时又发现系统电话簿（rasphone.pbk）用的是同一套危险写法 ——
    ///   那个文件坏了，用户所有的宽带连接都会消失，后果比账号文件严重得多。
    ///
    /// 两道防线：
    ///   ① WriteAtomic —— 先写临时文件，写完再整体替换。
    ///      任何时刻磁盘上要么是旧的完整内容、要么是新的完整内容，不存在"半截文件"。
    ///   ② KeepBackup —— 写之前把当前内容复制一份成 .bak，
    ///      万一还是出了意外（比如文件系统层面出问题），至少有上一版可以对照/恢复。
    /// </summary>
    internal static class SafeFile
    {
        public const string BackupSuffix = ".bak";

        /// <summary>原子替换写入。失败时原文件保持不变。</summary>
        public static void WriteAtomic(string path, string text, Encoding encoding)
        {
            if (encoding == null) encoding = new UTF8Encoding(false);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, encoding);

            if (File.Exists(path))
            {
                try
                {
                    // 原子替换：NTFS 上走 ReplaceFile，权限/属性沿用原文件
                    File.Replace(tmp, path, null);
                    return;
                }
                catch
                {
                    // 个别文件系统/被占用的情况不支持 Replace，退化成"覆盖 + 删临时"
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
            }

            File.Move(tmp, path);
        }

        /// <summary>把当前内容存一份为 .bak（当前文件的"上一版"）。</summary>
        public static void KeepBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Copy(path, path + BackupSuffix, true);
            }
            catch { }
        }

        /// <summary>文件里到底有没有"真内容"（空文件、只剩 BOM 的都不算）。</summary>
        public static bool HasRealContent(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                return new FileInfo(path).Length > 3;   // UTF-8 BOM 是 3 字节
            }
            catch { return false; }
        }
    }
}
