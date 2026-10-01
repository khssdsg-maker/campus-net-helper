using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 网页认证网址清单。
    ///
    /// 解决什么问题：
    ///   原先「认证网址」就是一个输入框 —— 只存得下一个。可学校的认证页不止一个：
    ///   宿舍区和教学楼的入口不一样、运营商不同跳转的目标也不一样、
    ///   有时候还要临时试试备用地址。每次换都得手打一遍完整 IP，很烦。
    ///   所以做成**像账号一样可以存多条、下拉选**。
    ///
    /// 存储：%AppData%\CampusNetHelper\webauthurls.txt
    ///   一个网址三行：
    ///     U=<原始网址>
    ///     N=<给用户看的名字，可空>
    ///     T=<最后使用时间>
    ///
    /// ⚠️ 与旧版本设置的兼容：
    ///   旧版把唯一那个网址存在 settings.txt 的 WebAuthUrl 键里。
    ///   升级后第一次读取时，如果清单是空的而旧键有值，
    ///   会自动把旧值**搬进清单**（见 MainWindow.MigrateLegacyUrl），
    ///   用户不会发现自己填过的地址"丢了"。
    /// </summary>
    public static class WebUrlStore
    {
        /// <summary>一条网址记录。</summary>
        public class Entry
        {
            /// <summary>完整网址，如 http://10.6.6.6/login。</summary>
            public string Url = "";
            /// <summary>显示用的名字（如"宿舍"）。空的话界面上就显示网址本身。</summary>
            public string Name = "";
            /// <summary>最后使用时间，用于"默认选哪条"。</summary>
            public string LastUsed = "";

            public Entry Clone()
            {
                return new Entry { Url = this.Url, Name = this.Name, LastUsed = this.LastUsed };
            }

            /// <summary>下拉框里显示的文字 —— 有名字就"名字（网址）"，没名字就只显示网址。</summary>
            public string Display()
            {
                if (string.IsNullOrEmpty(Name)) return Url;
                return Name + "（" + Url + "）";
            }
        }

        // ==================================================================
        // 文件位置
        // ==================================================================

        public static string UrlsPath
        {
            get { return Path.Combine(ConfigStore.AppDataDir, "webauthurls.txt"); }
        }

        /// <summary>清单上限。给个限度免得下拉框长到没法看。</summary>
        public const int MaxEntries = 20;

        // ==================================================================
        // 判重用的"同一站"键
        //
        // ⚠️ 这里**故意不用** FieldProfileStore.NormalizeUrl。
        //
        // 两者的"同一条"标准不一样，不能共用：
        //   · 字段档案认的是"同一个页面" —— 同一个 IP 下 /login 和 /login2
        //     是两个不同的表单，字段对不上，所以路径必须参与身份。
        //   · 网址清单认的是"同一个入口" —— 用户存的是"宿舍的认证页"。
        //     学校那个 IP 上换不换路径、带不带 ?wlanuserip=... 尾串，
        //     对用户来说就是同一个地方。看到清单里躺着
        //     "10.6.6.6"、"10.6.6.6/login"、"10.6.6.6/login?x=1" 三条，
        //     只会觉得这软件没做好。
        // 所以清单这边判到"主机（含端口）"为止，路径不参与身份。
        // ==================================================================

        /// <summary>
        /// 判重键：把各种写法归一到"协议 + 主机 + 端口"。
        ///   · 忽略协议（http / https 视为同一站，学校入口两种都可能出现）
        ///   · 忽略路径、?query、#fragment
        ///   · 忽略末尾斜杠、忽略默认端口
        ///   · 小写
        /// 输入为空（或归不出来）返回空串，调用方一律当"没有键"处理。
        /// </summary>
        public static string KeyOf(string url)
        {
            string s = (url ?? "").Trim();
            if (s.Length == 0) return "";

            // 掐掉协议
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            // 掐掉路径、query、fragment —— 找第一个 '/' '?' '#' 谁在前
            int cut = s.Length;
            int a = s.IndexOf('/'); if (a >= 0 && a < cut) cut = a;
            int b = s.IndexOf('?'); if (b >= 0 && b < cut) cut = b;
            int c = s.IndexOf('#'); if (c >= 0 && c < cut) cut = c;
            s = s.Substring(0, cut).Trim().TrimEnd('.');

            // 去掉默认端口
            if (s.EndsWith(":80", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3);
            else if (s.EndsWith(":443", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 4);

            if (s.Length == 0) return "";
            return s.ToLowerInvariant();
        }

        // ==================================================================
        // 读写
        // ==================================================================

        /// <summary>
        /// 读取全部网址。
        ///
        /// ⚠️ 必须把「文件不存在」和「文件存在但读不了」分开对待（2026-10-01 审计确认）：
        ///   前者是正常的（还没存过），返回空没问题；
        ///   后者是异常（被杀软或备份进程短暂锁住、磁盘出错等）—— 如果也按空返回，
        ///   紧接着的一次 Save 就只会写入新的一条，**原有条目无声丢光**
        ///   （最多 20 条，用户攒这些要挺久）。而这个文件以前**没有 .bak 兜底**
        ///   （accounts.txt 有），丢了就真没了。
        ///
        /// 现在的策略：主文件读不了 → 退到 .bak；两个都读不了才按空处理，
        /// 同时让 Save 侧拒绝覆盖（见 Save 开头那道闸）。
        /// </summary>
        public static List<Entry> LoadAll()
        {
            try
            {
                return ParseFile(UrlsPath);
            }
            catch (Exception ex)
            {
                Log.Warn("读取认证网址清单失败，改用备份: " + ex.Message);
                try
                {
                    return ParseFile(UrlsPath + SafeFile.BackupSuffix);
                }
                catch (Exception ex2)
                {
                    Log.Warn("备份也读不了（本次按空处理，且不会覆盖保存）: " + ex2.Message);
                    return new List<Entry>();
                }
            }
        }

        /// <summary>真正解析文件内容。异常一律向上抛，由 LoadAll 决定怎么兜。</summary>
        private static List<Entry> ParseFile(string path)
        {
            var list = new List<Entry>();
            if (!File.Exists(path)) return list;   // 不存在是正常的，不是错误

            Entry cur = null;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("U=", StringComparison.Ordinal))
                {
                    cur = new Entry();
                    cur.Url = Unescape(line.Substring(2));
                    list.Add(cur);
                    continue;
                }
                if (cur == null) continue;

                if (line.StartsWith("N=", StringComparison.Ordinal)) cur.Name = Unescape(line.Substring(2));
                else if (line.StartsWith("T=", StringComparison.Ordinal)) cur.LastUsed = Unescape(line.Substring(2));
            }

            // 滤掉没网址的残行（文件被截断时可能出现）
            var ok = new List<Entry>();
            foreach (Entry e in list)
            {
                if (e.Url != null && e.Url.Trim().Length > 0) ok.Add(e);
            }
            return ok;
        }

        /// <summary>
        /// 存一条网址。
        ///
        /// 判重按 KeyOf（"同一站"，见上），所以 "10.6.6.6"、"http://10.6.6.6/"、
        /// "http://10.6.6.6/login?x=1" 会被认出是同一条，不会堆出一串看着一样的条目。
        /// 已存在就更新（顺便补名字 / 刷时间），不存在才追加。
        /// </summary>
        public static bool Save(string url, string name, out string message)
        {
            message = "";
            string clean = Clean(url);
            if (clean.Length == 0)
            {
                message = "网址是空的";
                return false;
            }

            try
            {
                List<Entry> all = LoadAll();

                // ⚠️ 第二道防线（2026-10-01 审计确认）：
                //    读到 0 条、但磁盘上明明有内容 —— 这不是"用户把网址删光了"，
                //    而是**读失败**。这时候继续写就会把原有条目全冲掉，所以直接拒绝。
                if (all.Count == 0 && SafeFile.HasRealContent(UrlsPath))
                {
                    message = "现有网址清单读取失败，为防止覆盖原有内容，本次没有保存。请稍后重试。";
                    Log.Warn("保存网址被拒：清单读到 0 条但文件有内容，判定为读取失败");
                    return false;
                }

                string key = KeyOf(clean);
                if (key.Length == 0)
                {
                    message = "这条网址认不出主机名（检查一下有没有打错）";
                    return false;
                }

                Entry hit = null;
                foreach (Entry e in all)
                {
                    if (string.Equals(KeyOf(e.Url), key, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = e;
                        break;
                    }
                }

                if (hit != null)
                {
                    hit.Url = clean;                                   // 用最新写法覆盖（可能带了新路径）
                    if (!string.IsNullOrEmpty(name)) hit.Name = name;   // 名字为空就不动原来的
                    hit.LastUsed = Now();
                }
                else
                {
                    if (all.Count >= MaxEntries)
                    {
                        message = "最多存 " + MaxEntries + " 条，请先删掉不用的";
                        return false;
                    }
                    var e = new Entry();
                    e.Url = clean;
                    e.Name = name ?? "";
                    e.LastUsed = Now();
                    all.Add(e);
                }

                return WriteAll(all, out message);
            }
            catch (Exception ex)
            {
                message = "保存网址失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>按网址删一条（同样按 KeyOf 的"同一站"匹配）。</summary>
        public static bool Delete(string url, out string message)
        {
            message = "";
            try
            {
                string key = KeyOf(url);
                if (key.Length == 0) { message = "清单里没有这条网址"; return false; }
                List<Entry> all = LoadAll();
                var keep = new List<Entry>();
                int removed = 0;
                foreach (Entry e in all)
                {
                    if (string.Equals(KeyOf(e.Url), key, StringComparison.OrdinalIgnoreCase))
                    {
                        removed++;
                    }
                    else
                    {
                        keep.Add(e);
                    }
                }
                if (removed == 0) { message = "清单里没有这条网址"; return false; }
                return WriteAll(keep, out message);
            }
            catch (Exception ex)
            {
                message = "删除网址失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>把某条标记为"最后使用" —— 下次打开窗口默认就选它。</summary>
        public static void Touch(string url)
        {
            try
            {
                string key = KeyOf(url);
                if (key.Length == 0) return;

                List<Entry> all = LoadAll();
                bool changed = false;
                foreach (Entry e in all)
                {
                    if (string.Equals(KeyOf(e.Url), key, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LastUsed = Now();
                        changed = true;
                        break;
                    }
                }
                if (!changed) return;

                string msg;
                WriteAll(all, out msg);
            }
            catch { }
        }

        /// <summary>
        /// 清单里"最近用过"的那条网址；清单为空返回空串。
        /// 时间戳是 "yyyy-MM-dd HH:mm" 这种定长格式，直接字符串比大小即可（同格式下等价于按时间排）。
        /// </summary>
        public static string MostRecent()
        {
            List<Entry> all = LoadAll();
            if (all.Count == 0) return "";

            Entry best = null;
            foreach (Entry e in all)
            {
                if (best == null) { best = e; continue; }
                if (string.CompareOrdinal(e.LastUsed ?? "", best.LastUsed ?? "") > 0) best = e;
            }
            return best == null ? "" : best.Url;
        }

        /// <summary>清单里有没有这条网址。</summary>
        public static bool Contains(string url)
        {
            string key = KeyOf(url);
            if (key.Length == 0) return false;
            foreach (Entry e in LoadAll())
            {
                if (string.Equals(KeyOf(e.Url), key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ==================================================================
        // 工具
        // ==================================================================

        /// <summary>
        /// 清洗用户填的网址：
        ///   · 去掉首尾空格
        ///   · 没写协议的自动补 http://（浏览器地址栏的习惯，用户不会主动写）
        /// 不做规范化 —— 存的是用户写的原始形态，规范化只用于"判重"。
        /// </summary>
        public static string Clean(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "http://" + s;
            }
            return s;
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        private static bool WriteAll(List<Entry> all, out string message)
        {
            message = "";
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# 校园网助手 · 网页认证网址清单（本机，不上传）");
                sb.AppendLine("# 格式：每条三行  U=<网址>  N=<名字>  T=<最后使用时间>");
                foreach (Entry e in all)
                {
                    sb.AppendLine("U=" + Escape(e.Url));
                    sb.AppendLine("N=" + Escape(e.Name));
                    sb.AppendLine("T=" + Escape(e.LastUsed));
                }
                // 写之前留一份上一版 —— 这个文件以前完全没有备份，
                // 一旦被写坏（或像上面那样被误覆盖）就没有任何挽回余地。
                SafeFile.KeepBackup(UrlsPath);
                SafeFile.WriteAtomic(UrlsPath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                message = "写网址清单失败: " + ex.Message;
                return false;
            }
        }

        // 转义：网址里出现 | 或换行的概率极低，但档案那边踩过这个坑，
        // 同一个文件格式就沿用同一套规则，免得两处不一致。
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
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
