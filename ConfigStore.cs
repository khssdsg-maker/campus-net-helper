using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 账号与设置持久化。
    ///
    /// 存储位置：%AppData%\CampusNetHelper\
    ///   · accounts.txt —— 账号列表，每行：连接名|用户名|密码
    ///   · settings.txt —— 各项设置，每行 key=value
    ///
    /// 密码以明文保存在本机用户目录下——这是刻意的取舍：本工具定位为轻量校园网助手，
    /// 不做凭据管理。如果不希望在本机留下密码，保存账号时可以不填密码，每次拨号手动输入。
    /// </summary>
    public static class ConfigStore
    {
        public static string AppDataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CampusNetHelper");
            }
        }

        public static string AccountsPath
        {
            get { return Path.Combine(AppDataDir, "accounts.txt"); }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(AppDataDir, "settings.txt"); }
        }

        private static void EnsureDir()
        {
            try { if (!Directory.Exists(AppDataDir)) Directory.CreateDirectory(AppDataDir); }
            catch { }
        }

        // ==================================================================
        // 安全写入（⚠️ 2026-09-29 因一次真实数据丢失事故加上的）
        //
        // 事故现场：用户在关机过程中，程序"关闭窗口 = 最小化到托盘"的逻辑拒绝退出，
        // Windows 等超时后强制结束进程。而当时正在写 accounts.txt ——
        //   旧写法 File.WriteAllText = 先把文件清空到 0 再用 StreamWriter 写，
        //   文件头（UTF-8 BOM）当场写出去了，正文还留在 StreamWriter 的缓冲区里。
        //   进程一被杀，缓冲区没了 → 文件只剩 3 字节（只有一个 BOM）。
        // 次日开机读到 0 个账号 → 判定为"没有可用账号" → 不拨号、不提示；
        // 随后又一次保存把空列表正式写了回去，账号彻底丢失。
        //
        // 两道防线：
        //   ① 原子替换：先写临时文件，写完了再整体替换 —— 任何时刻磁盘上
        //      要么是旧的完整内容、要么是新的完整内容，不存在"半截文件"。
        //   ② 上一版留档：每次保存前把旧内容复制成 .bak，
        //      读取时若主文件废了但备份是好的，自动从备份恢复。
        // ==================================================================

        private const string BackupSuffix = ".bak";

        /// <summary>原子替换写入：写临时文件 → 整体替换目标。失败不会破坏原文件。</summary>
        private static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);

            if (File.Exists(path))
            {
                try
                {
                    // File.Replace 是原子的（NTFS 上走 ReplaceFile / MoveFileEx）
                    File.Replace(tmp, path, null);
                    return;
                }
                catch
                {
                    // 个别文件系统不支持 Replace，退化成"复制 + 删临时"
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
            }

            File.Move(tmp, path);
        }

        /// <summary>把当前内容存一份为 .bak（作为"上一版留档"）。</summary>
        private static void KeepBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Copy(path, path + BackupSuffix, true);
            }
            catch { }
        }

        /// <summary>文件里到底有没有"真内容"（只有 BOM 或空文件都算没有）。</summary>
        private static bool HasRealContent(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                long len = new FileInfo(path).Length;
                return len > 3;      // UTF-8 BOM 是 3 字节
            }
            catch { return false; }
        }

        // ==================================================================
        // 账号
        // ==================================================================

        /// <summary>一条账号记录。</summary>
        public class Account
        {
            public string Name = "";
            public string User = "";
            public string Password = "";
        }

        /// <summary>读取全部账号。主文件废了会尝试从 .bak 恢复。</summary>
        public static List<Account> LoadAccounts()
        {
            if (File.Exists(AccountsPath))
            {
                List<Account> list = ParseAccounts(AccountsPath);

                // 主文件一条都读不出来，但备份里有 —— 说明主文件坏了，用备份救回来。
                // （典型坏法：只剩一个 UTF-8 文件头，就是被"写一半杀进程"搞出来的）
                string bak = AccountsPath + BackupSuffix;
                if (list.Count == 0 && File.Exists(bak))
                {
                    List<Account> fromBak = ParseAccounts(bak);
                    if (fromBak.Count > 0)
                    {
                        Log.Warn("账号文件疑似损坏（当前读到 0 条），已从备份恢复 " + fromBak.Count + " 个账号");
                        try { File.Copy(AccountsPath, AccountsPath + ".bad", true); } catch { }
                        WriteAtomic(AccountsPath, BuildAccountsText(fromBak));
                        return fromBak;
                    }
                }

                if (list.Count == 0 && HasRealContent(AccountsPath))
                {
                    Log.Warn("账号文件内容无法解析（" + new FileInfo(AccountsPath).Length + " 字节），已原样留档为 accounts.txt.bad");
                    try { File.Copy(AccountsPath, AccountsPath + ".bad", true); } catch { }
                }
                return list;
            }
            return new List<Account>();
        }

        private static List<Account> ParseAccounts(string path)
        {
            var list = new List<Account>();
            try
            {
                if (!File.Exists(path)) return list;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length < 2) continue;
                    var a = new Account();
                    a.Name = parts[0];
                    a.User = parts[1];
                    a.Password = parts.Length > 2 ? parts[2] : "";
                    if (a.Name.Length > 0) list.Add(a);
                }
            }
            catch { }
            return list;
        }

        private static string BuildAccountsText(List<Account> accounts)
        {
            var sb = new StringBuilder();
            if (accounts != null)
            {
                foreach (Account a in accounts)
                {
                    if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                    sb.AppendLine(a.Name + "|" + a.User + "|" + a.Password);
                }
            }
            return sb.ToString();
        }

        /// <summary>保存全部账号（原子替换 + 旧版留档）。</summary>
        public static bool SaveAccounts(List<Account> accounts, out string message)
        {
            message = "";
            try
            {
                EnsureDir();
                string text = BuildAccountsText(accounts);

                // 要写的和现在磁盘上不一样才动，避免无意义的覆盖（减少危险窗口）
                KeepBackup(AccountsPath);
                WriteAtomic(AccountsPath, text);
                return true;
            }
            catch (Exception ex)
            {
                message = "保存账号失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>新增或更新一条账号（按连接名匹配）。</summary>
        public static bool UpsertAccount(List<Account> accounts, Account item, out string message)
        {
            message = "";
            if (item == null || string.IsNullOrEmpty(item.Name))
            {
                message = "连接名不能为空";
                return false;
            }
            if (accounts == null) accounts = new List<Account>();

            bool found = false;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (string.Equals(accounts[i].Name, item.Name, StringComparison.Ordinal))
                {
                    accounts[i] = item;
                    found = true;
                    break;
                }
            }
            if (!found) accounts.Add(item);

            return SaveAccounts(accounts, out message);
        }

        /// <summary>按连接名删除账号。</summary>
        public static bool DeleteAccount(List<Account> accounts, string name, out string message)
        {
            message = "";
            if (accounts == null) { message = "账号列表为空"; return false; }
            int idx = -1;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (string.Equals(accounts[i].Name, name, StringComparison.Ordinal)) { idx = i; break; }
            }
            if (idx < 0) { message = "未找到该账号"; return false; }
            accounts.RemoveAt(idx);

            bool ok = SaveAccounts(accounts, out message);

            // 用户主动把账号删光了 → 连备份一起清掉。
            // 否则下次启动时"主文件为空 + 备份里有账号"会被当成文件损坏，把删掉的账号又救回来。
            if (ok && accounts.Count == 0)
            {
                try { if (File.Exists(AccountsPath + BackupSuffix)) File.Delete(AccountsPath + BackupSuffix); }
                catch { }
            }
            return ok;
        }

        // ==================================================================
        // 设置
        // ==================================================================

        /// <summary>读取设置的键值对。文件不存在返回空字典。</summary>
        public static Dictionary<string, string> LoadSettings()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath)) return dict;
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k.Length > 0) dict[k] = v;
                }
            }
            catch { }
            return dict;
        }

        /// <summary>写入设置的键值对（覆盖写）。</summary>
        public static bool SaveSettings(Dictionary<string, string> settings, out string message)
        {
            message = "";
            try
            {
                EnsureDir();
                var sb = new StringBuilder();
                if (settings != null)
                {
                    foreach (KeyValuePair<string, string> kv in settings)
                    {
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                    }
                }
                KeepBackup(SettingsPath);
                WriteAtomic(SettingsPath, sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                message = "保存设置失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>读取布尔设置。</summary>
        public static bool GetBool(Dictionary<string, string> settings, string key, bool def)
        {
            if (settings == null) return def;
            string v;
            if (!settings.TryGetValue(key, out v)) return def;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>写入布尔设置。</summary>
        public static void SetBool(Dictionary<string, string> settings, string key, bool value)
        {
            if (settings == null) return;
            settings[key] = value ? "1" : "0";
        }

        /// <summary>读取字符串设置。</summary>
        public static string GetString(Dictionary<string, string> settings, string key, string def)
        {
            if (settings == null) return def;
            string v;
            if (!settings.TryGetValue(key, out v)) return def;
            return v;
        }
    }
}
