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

        private const string BackupSuffix = SafeFile.BackupSuffix;

        /// <summary>原子替换写入（实现见 SafeFile，电话簿/设置共用同一套）。</summary>
        private static void WriteAtomic(string path, string text)
        {
            SafeFile.WriteAtomic(path, text, Encoding.UTF8);
        }

        /// <summary>把当前内容存一份为 .bak。</summary>
        private static void KeepBackup(string path)
        {
            SafeFile.KeepBackup(path);
        }

        /// <summary>文件里到底有没有"真内容"（只有 BOM 或空文件都算没有）。</summary>
        private static bool HasRealContent(string path)
        {
            return SafeFile.HasRealContent(path);
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

            /// <summary>
            /// 附加账号（可空）。
            ///
            /// 有些学校的网页认证要**两个号**：一个身份号（学工号）＋一个上网账号（常见是手机号）。
            /// 拨号只用得到 User，所以这个字段对拨号无影响；
            /// 网页认证填表时如果门户上有第二个输入框，就往这里取。
            ///
            /// ⚠️ 落盘格式是 accounts.txt 的**第 4 段**（Name|User|Password|User2）。
            ///    老文件没有第 4 段 → 读出来是空串，属正常，不要当成损坏。
            /// </summary>
            public string User2 = "";
        }

        /// <summary>读取全部账号。主文件废了会尝试从 .bak 恢复。</summary>
        public static List<Account> LoadAccounts()
        {
            if (File.Exists(AccountsPath))
            {
                bool plainFound;
                List<Account> list = ParseAccounts(AccountsPath, out plainFound);

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

                        // ⚠️ 恢复写入必须兜异常：这个函数是在主窗口构造函数里被调用的，
                        //    一旦抛出去就是"程序启动直接失败"。
                        //    文件只读 / 被占用 / 磁盘满 都可能失败 —— 那种情况下
                        //    也要把从备份读到的账号返回给界面，不能在启动阶段崩。
                        try
                        {
                            WriteAtomic(AccountsPath, BuildAccountsText(fromBak));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("从备份恢复账号时写回失败（不影响本次使用）: " + ex.Message);
                        }
                        return fromBak;
                    }
                }

                if (list.Count == 0 && HasRealContent(AccountsPath))
                {
                    Log.Warn("账号文件内容无法解析（" + new FileInfo(AccountsPath).Length + " 字节），已原样留档为 accounts.txt.bad");
                    try { File.Copy(AccountsPath, AccountsPath + ".bad", true); } catch { }
                }

                // 老版本存的是明文密码 —— 读进来了就顺手加密写回去（一次性迁移）。
                // 包 try/catch：失败也不影响本次使用，下次保存还会再试。
                if (plainFound && list.Count > 0)
                {
                    Log.Info("检测到明文密码，已改为加密保存（Windows DPAPI，仅本机本账户可解）");
                    try { KeepBackup(AccountsPath); WriteAtomic(AccountsPath, BuildAccountsText(list)); }
                    catch (Exception ex) { Log.Warn("明文密码迁移为密文失败（不影响使用）: " + ex.Message); }
                }
                return list;
            }
            return new List<Account>();
        }

        private static List<Account> ParseAccounts(string path)
        {
            bool ignored;
            return ParseAccounts(path, out ignored);
        }

        private static List<Account> ParseAccounts(string path, out bool plainFound)
        {
            plainFound = false;
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
                    // 密码：密文解开，老版本的明文原样返回（下次保存会自动加密）
                    a.Password = parts.Length > 2 ? SecureStore.Unprotect(parts[2]) : "";
                    // 附加账号（学工号等）：老文件没有第 4 段 → 空串，属正常
                    a.User2 = parts.Length > 3 ? parts[3] : "";
                    bool wasPlain = parts.Length > 2 && !string.IsNullOrEmpty(parts[2])
                        && !SecureStore.IsProtected(parts[2]);
                    if (wasPlain) plainFound = true;
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
                    // 落盘一律加密（DPAPI，只有本机本用户能解）
                    // 第 4 段是附加账号，明文存（它就是学工号一类的标识，不是密码）
                    sb.AppendLine(a.Name + "|" + a.User + "|" + SecureStore.Protect(a.Password)
                        + "|" + (a.User2 ?? ""));
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
