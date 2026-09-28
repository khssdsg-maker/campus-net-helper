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
        // 账号
        // ==================================================================

        /// <summary>一条账号记录。</summary>
        public class Account
        {
            public string Name = "";
            public string User = "";
            public string Password = "";
        }

        /// <summary>读取全部账号。文件不存在或损坏时返回空列表。</summary>
        public static List<Account> LoadAccounts()
        {
            var list = new List<Account>();
            try
            {
                if (!File.Exists(AccountsPath)) return list;
                foreach (string line in File.ReadAllLines(AccountsPath, Encoding.UTF8))
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

        /// <summary>保存全部账号（覆盖写）。</summary>
        public static bool SaveAccounts(List<Account> accounts, out string message)
        {
            message = "";
            try
            {
                EnsureDir();
                var sb = new StringBuilder();
                if (accounts != null)
                {
                    foreach (Account a in accounts)
                    {
                        if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                        sb.AppendLine(a.Name + "|" + a.User + "|" + a.Password);
                    }
                }
                File.WriteAllText(AccountsPath, sb.ToString(), Encoding.UTF8);
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
            return SaveAccounts(accounts, out message);
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
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
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
