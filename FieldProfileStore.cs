using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 登录页字段档案。
    ///
    /// 解决什么问题：
    ///   有些学校的网页认证页不止"账号 + 密码"两个框 —— 桂电信科那一页就有
    ///   「运营商」「学(工)号」「上网账号」「密码」「验证码」五个框。
    ///   靠 name/id 猜，猜不准；换个学校就更猜不准。
    ///   所以让**用户自己教一次**：打开页面 → 扫描 → 把每个框和它该填的值对上 → 存下来。
    ///   以后每次打开这一页，程序照着档案直接填。
    ///
    /// 存储：%AppData%\CampusNetHelper\fieldprofiles.txt
    ///   一行一个字段：序号|标签文本|name|id|填写类型|固定值|账号字段名
    ///
    /// ⚠️ 档案里**只存"取值方式"，不存密码本身**。
    ///    密码仍然从账号里取（DPAPI 加密那份）——
    ///    这样改一次密码，所有档案自动跟着变，不会各处留一堆过期密码。
    /// </summary>
    public static class FieldProfileStore
    {
        // ---------------- 填写类型 ----------------

        public const string KindAccount = "account";   // 从账号取：用户名 / 密码 / 附加账号
        public const string KindFixed = "fixed";       // 固定值（运营商、学校代码这种）
        public const string KindCaptcha = "captcha";   // 验证码：程序不填，等用户手动填
        public const string KindIgnore = "ignore";     // 不管它

        // ---------------- 匹配方式 ----------------

        /// <summary>按 name/id 精确匹配（最稳，优先）。</summary>
        public const int MatchAttr = 0;
        /// <summary>按页面上的标签文字匹配（页面改 id 也能扛住）。</summary>
        public const int MatchLabel = 1;
        /// <summary>按页面上输入框的出现顺序匹配（最后的兜底）。</summary>
        public const int MatchIndex = 2;

        /// <summary>
        /// 一条字段档案。
        ///
        /// 为什么三个匹配维度都存：
        ///   学校时不时改版。只认 id 的话，改一次就全废；
        ///   三个维度按 属性 → 标签 → 顺序 依次尝试，只要还有一项对得上就还能填。
        /// </summary>
        public class FieldProfile
        {
            /// <summary>页面上这个框的标签文字（如"学(工)号"）。给用户看 + 作为匹配依据。</summary>
            public string Label = "";
            /// <summary>输入框的 name 属性。</summary>
            public string Name = "";
            /// <summary>输入框的 id 属性。</summary>
            public string Id = "";
            /// <summary>该框在页面表单输入控件里的序号（0 起）。</summary>
            public int Index = -1;

            /// <summary>填写类型，取值见本类的 Kind* 常量。</summary>
            public string Kind = KindIgnore;
            /// <summary>Kind = fixed 时填的值。</summary>
            public string Value = "";
            /// <summary>Kind = account 时取账号的哪一项："user" / "password" / "user2"。</summary>
            public string AccountField = "";
            /// <summary>用户是否勾了"参与自动填写"。</summary>
            public bool Enabled = true;

            public FieldProfile Clone()
            {
                return new FieldProfile
                {
                    Label = this.Label,
                    Name = this.Name,
                    Id = this.Id,
                    Index = this.Index,
                    Kind = this.Kind,
                    Value = this.Value,
                    AccountField = this.AccountField,
                    Enabled = this.Enabled
                };
            }
        }

        /// <summary>一个网址对应的一份档案。</summary>
        public class Profile
        {
            /// <summary>本档案适用的认证网址（规范化后的主机+路径）。</summary>
            public string Url = "";
            /// <summary>存档时间，给用户看"这份档案是什么时候教的"。</summary>
            public string SavedAt = "";
            public List<FieldProfile> Fields = new List<FieldProfile>();

            public Profile Clone()
            {
                var p = new Profile { Url = this.Url, SavedAt = this.SavedAt };
                foreach (FieldProfile f in this.Fields) p.Fields.Add(f.Clone());
                return p;
            }
        }

        // ==================================================================
        // 文件位置
        // ==================================================================

        public static string ProfilesPath
        {
            get { return Path.Combine(ConfigStore.AppDataDir, "fieldprofiles.txt"); }
        }

        // ==================================================================
        // 网址规范化
        //
        // 为什么要规范化：用户可能填 "10.6.6.6"、"http://10.6.6.6"、
        // "http://10.6.6.6/login"、"http://10.6.6.6/login?x=1" ——
        // 这些其实指向同一个认证页。都归一到 "主机/路径"，档案才能命中。
        // ==================================================================

        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();

            // 只取协议之后的部分
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);

            // 去掉 #fragment 和 ?query
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s.Substring(0, hash);
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);

            // 去掉末尾斜杠
            s = s.TrimEnd('/');

            // 去掉默认端口
            s = s.Replace(":80/", "/");
            if (s.EndsWith(":80", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3);

            return s.ToLowerInvariant();
        }

        /// <summary>两个网址是不是同一个认证页。</summary>
        public static bool SamePage(string a, string b)
        {
            string na = NormalizeUrl(a);
            string nb = NormalizeUrl(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        // ==================================================================
        // 读写
        // ==================================================================

        /// <summary>读取全部档案。文件坏了返回空列表（不抛异常，调用方在窗口构造链上）。</summary>
        public static List<Profile> LoadAll()
        {
            var list = new List<Profile>();
            try
            {
                if (!File.Exists(ProfilesPath)) return list;
                foreach (string line in File.ReadAllLines(ProfilesPath, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    // 行首 "URL=" 的行开一个新档案
                    if (line.StartsWith("URL=", StringComparison.Ordinal))
                    {
                        var p = new Profile();
                        p.Url = Unescape(line.Substring(4));
                        list.Add(p);
                        continue;
                    }
                    if (line.StartsWith("AT=", StringComparison.Ordinal))
                    {
                        if (list.Count > 0) list[list.Count - 1].SavedAt = Unescape(line.Substring(3));
                        continue;
                    }
                    if (line.StartsWith("F=", StringComparison.Ordinal))
                    {
                        if (list.Count == 0) continue;
                        FieldProfile f = ParseField(line.Substring(2));
                        if (f != null) list[list.Count - 1].Fields.Add(f);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取字段档案失败（按空处理）: " + ex.Message);
            }
            return list;
        }

        /// <summary>按网址取一份档案；没有就返回 null。</summary>
        public static Profile GetForUrl(string url)
        {
            string want = NormalizeUrl(url);
            if (want.Length == 0) return null;
            foreach (Profile p in LoadAll())
            {
                if (string.Equals(NormalizeUrl(p.Url), want, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public static bool HasForUrl(string url)
        {
            return GetForUrl(url) != null;
        }

        /// <summary>保存一份档案（同网址覆盖）。</summary>
        public static bool Save(Profile profile, out string message)
        {
            message = "";
            if (profile == null || NormalizeUrl(profile.Url).Length == 0)
            {
                message = "档案没有关联的网址";
                return false;
            }

            try
            {
                profile.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

                List<Profile> all = LoadAll();
                int found = -1;
                for (int i = 0; i < all.Count; i++)
                {
                    if (string.Equals(NormalizeUrl(all[i].Url), NormalizeUrl(profile.Url),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0) all[found] = profile; else all.Add(profile);

                return WriteAll(all, out message);
            }
            catch (Exception ex)
            {
                message = "保存档案失败: " + ex.Message;
                return false;
            }
        }

        public static bool DeleteForUrl(string url, out string message)
        {
            message = "";
            try
            {
                string want = NormalizeUrl(url);
                List<Profile> all = LoadAll();
                var keep = new List<Profile>();
                int removed = 0;
                foreach (Profile p in all)
                {
                    if (string.Equals(NormalizeUrl(p.Url), want, StringComparison.OrdinalIgnoreCase)) removed++;
                    else keep.Add(p);
                }
                if (removed == 0) { message = "这个网址还没有档案"; return false; }
                return WriteAll(keep, out message);
            }
            catch (Exception ex)
            {
                message = "删除档案失败: " + ex.Message;
                return false;
            }
        }

        private static bool WriteAll(List<Profile> all, out string message)
        {
            message = "";
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# 校园网助手 · 网页认证字段档案（本机，不上传）");
                sb.AppendLine("# 格式： URL=<网址>  AT=<存档时间>  F=启用|标签|name|id|序号|类型|固定值|账号字段");
                foreach (Profile p in all)
                {
                    sb.AppendLine("URL=" + Escape(p.Url));
                    sb.AppendLine("AT=" + Escape(p.SavedAt));
                    foreach (FieldProfile f in p.Fields)
                    {
                        sb.AppendLine("F="
                            + (f.Enabled ? "1" : "0") + "|"
                            + Escape(f.Label) + "|"
                            + Escape(f.Name) + "|"
                            + Escape(f.Id) + "|"
                            + f.Index.ToString() + "|"
                            + Escape(f.Kind) + "|"
                            + Escape(f.Value) + "|"
                            + Escape(f.AccountField));
                    }
                }
                SafeFile.WriteAtomic(ProfilesPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                message = "写档案文件失败: " + ex.Message;
                return false;
            }
        }

        // ==================================================================
        // 行内序列化
        //
        // 分隔符用 | ，所以字段值里的 | 和换行必须转义 ——
        // 标签文字里带 | 不常见，但"忘了转义导致整行读歪"是那种
        // 出了很难查的错，这里一次做对。
        // ==================================================================

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", "").Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'p') { sb.Append('|'); i++; continue; }
                    if (n == 'n') { sb.Append(' '); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static FieldProfile ParseField(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            // 先按未转义的 | 切分 —— 转义后的 \p 不含裸 |
            string[] parts = body.Split('|');
            if (parts.Length < 8) return null;

            var f = new FieldProfile();
            f.Enabled = parts[0] != "0";
            f.Label = Unescape(parts[1]);
            f.Name = Unescape(parts[2]);
            f.Id = Unescape(parts[3]);
            int idx;
            f.Index = int.TryParse(parts[4], out idx) ? idx : -1;
            f.Kind = Unescape(parts[5]);
            f.Value = Unescape(parts[6]);
            f.AccountField = Unescape(parts[7]);
            if (f.Kind.Length == 0) f.Kind = KindIgnore;
            return f;
        }

        // ==================================================================
        // 取值：档案 + 账号 → 这个框该填什么
        // ==================================================================

        /// <summary>
        /// 按账号算出一个字段该填的值。返回 null 表示"这个字段不该由程序填"
        /// （比如验证码、或账号里那项是空的）。
        /// </summary>
        public static string ResolveValue(FieldProfile f, ConfigStore.Account account)
        {
            if (f == null) return null;

            if (f.Kind == KindFixed) return f.Value;
            if (f.Kind == KindCaptcha || f.Kind == KindIgnore) return null;

            if (f.Kind == KindAccount)
            {
                if (account == null) return null;

                if (string.Equals(f.AccountField, "password", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(account.Password) ? null : account.Password;

                if (string.Equals(f.AccountField, "user2", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(account.User2) ? null : account.User2;

                return string.IsNullOrEmpty(account.User) ? null : account.User;
            }

            return null;
        }

        /// <summary>给用户看的类型说明。</summary>
        public static string KindText(FieldProfile f)
        {
            if (f.Kind == KindFixed) return "固定值：" + f.Value;
            if (f.Kind == KindCaptcha) return "验证码（由你手动填）";
            if (f.Kind == KindIgnore) return "不填";
            if (f.Kind == KindAccount)
            {
                if (string.Equals(f.AccountField, "password", StringComparison.OrdinalIgnoreCase)) return "密码";
                if (string.Equals(f.AccountField, "user2", StringComparison.OrdinalIgnoreCase)) return "附加账号（学工号）";
                return "上网账号";
            }
            return "不填";
        }
    }
}
