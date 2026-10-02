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
            catch (Exception ex)
            {
                // ⚠️ 以前这里是空 catch（2026-10-01 审计确认）：磁盘满 / 文件只读 / 被占用时
                //    备份会**静默缺席** —— 而那恰恰是最需要有一份备份的时刻。
                //    至少要留一条痕迹，否则真出事时根本不知道"当时备份没做成"。
                Log.Warn("留备份失败（" + path + "）: " + ex.Message);
            }
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

        /// <summary>
        /// 文件里有没有**数据行** —— 非空、且不是以 # 开头的注释。
        ///
        /// ⚠️ 为什么又加了这一个（2026-10-02 修 bug）：
        ///   webauthurls.txt / fieldprofiles.txt 这两个清单文件**永远带着两行 '#' 表头**，
        ///   所以"用户把条目全删光"之后，文件仍然有 200 多字节。
        ///   而 HasRealContent 只看字节数 → 会把这种**完全正常的状态**误判成"读失败"，
        ///   于是 Save 里那道闸永久生效：用户删光之后**再也存不进任何一条**。
        ///   （实测复现：自测的「认证网址清单」用例红的，12 通过 / 1 不通过 ——
        ///     而且 v2.2.0 的发布版同样红，属于已经发出去的老 bug。）
        ///
        ///   判据本来就该是"有没有数据行"：注释不是数据，表头跟着文件走。
        /// </summary>
        public static bool HasDataLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string s = line.Trim();
                    if (s.Length == 0) continue;
                    if (s.StartsWith("#", StringComparison.Ordinal)) continue;
                    return true;
                }
                return false;
            }
            catch
            {
                // 读不出来 = 到底有没有数据**不知道**。
                // 这时候按"有内容"处理 —— 宁可拒绝保存，也不能把用户的清单冲掉。
                return true;
            }
        }
    }
}
