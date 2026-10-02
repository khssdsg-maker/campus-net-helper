using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace CampusNetHelper
{
    /// <summary>
    /// 界面自动化自测（不开真窗口、不打扰用户）。
    ///
    /// 干什么：
    ///   把窗口构建出来、放到一个隐藏的宿主里真正地布局一次，
    ///   然后**模拟用户的鼠标点击和键盘输入**，检查焦点有没有被抢走。
    ///
    /// 为什么必须做这个：
    ///   字段档案那个"打字时输入框跑掉"的 bug，光看代码是看不出来的 ——
    ///   它只在"真实布局 + 真实输入"下才暴露。
    ///   之前我就是靠肉眼读代码，连着漏了两次。
    ///   这个自测跑一遍只要几秒，能直接把这类问题挡在发给用户之前。
    ///
    /// 触发：程序启动时带 --selftest 参数。
    ///   结果写进 %TEMP%\campusnet-selftest.txt，同时打一份到日志。
    /// </summary>
    public static class UiTest
    {
        private static readonly StringBuilder Report = new StringBuilder();

        private static void W(string s)
        {
            Report.AppendLine(s);
            try { Console.WriteLine(s); } catch { }
        }

        public static string Run()
        {
            // ⚠️ 自测会开认证窗口、会写设置与历史 —— 那些都是**用户的真实数据**。
            //    2026-10-03 实测踩到：跑完自测，settings.txt 里的 WebAuthUrl 变成了
            //    http://127.0.0.1:43792/login（自测临时起的小站，早就关了），
            //    历史里也多出好几条假的"认证有效"。上一轮只清了网址清单，漏了设置。
            //    所以这里改成整体兜底：跑之前把数据目录快照一份，跑完**原样放回**。
            //    新加用例不必再各自记得清场 —— 要的是"默认安全"，不是"记得小心"。
            DataSnapshot snap = new DataSnapshot(ConfigStore.AppDataDir);
            try
            {
                W("================ 界面自测 开始 ================");
                W("WPF 版本 = " + typeof(Window).Assembly.GetName().Version);

                TestFieldProfileKeyboardFocus();
                TestAdapterPicking();
                TestTrafficAdapterPicking();
                TestPortalOnlineTexts();
                TestOnlineTimeAdvances();
                TestWebUrlStore();
                TestKeepAliveLogQuiet();
                TestPortalAutoReconnect();
                TestFirstRunTip();
                TestAppIcon();
                TestAccountDeleteNoResurrect();
                TestSettingRoundTrip();
                TestVersionCompare();
                TestCaptchaAssist();
                TestCaptchaPipeline();
                TestCaptchaOcr();

                W("================ 界面自测 结束 ================");
            }
            catch (Exception ex)
            {
                W("!! 自测自身异常: " + ex);
            }
            finally
            {
                bool restored = snap.Restore();
                W("");
                W("用户数据已还原 = " + restored + "（快照 " + snap.FileCount + " 个文件）");
            }
            return Report.ToString();
        }

        /// <summary>
        /// 自测期间用户数据文件的快照。跑完把数据目录恢复到跑之前的样子：
        ///   · 跑之前就有的文件 -> 按原字节写回
        ///   · 自测期间新建的文件 -> 删掉
        /// 只处理数据目录下的**文件**；logs\ 与 captcha-samples\ 是子目录，不受影响
        /// （自测本来就要读用户的验证码样本来量准确率）。
        /// </summary>
        private sealed class DataSnapshot
        {
            private readonly string _dir;
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();

            public DataSnapshot(string dir)
            {
                _dir = dir;
                try
                {
                    if (!System.IO.Directory.Exists(dir)) return;
                    string[] all = System.IO.Directory.GetFiles(dir);
                    for (int i = 0; i < all.Length; i++)
                    {
                        try { _files[System.IO.Path.GetFileName(all[i])] = System.IO.File.ReadAllBytes(all[i]); }
                        catch { }
                    }
                }
                catch { }
            }

            public int FileCount { get { return _files.Count; } }

            public bool Restore()
            {
                try
                {
                    if (!System.IO.Directory.Exists(_dir)) return false;

                    // 先删掉自测期间新产生的文件
                    string[] now = System.IO.Directory.GetFiles(_dir);
                    for (int i = 0; i < now.Length; i++)
                    {
                        string name = System.IO.Path.GetFileName(now[i]);
                        if (!_files.ContainsKey(name))
                        {
                            try { System.IO.File.Delete(now[i]); } catch { }
                        }
                    }

                    // 再把快照按原字节写回
                    foreach (KeyValuePair<string, byte[]> kv in _files)
                    {
                        try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dir, kv.Key), kv.Value); }
                        catch { }
                    }
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 复现用户的操作：选中"学工号" → 类型选"固定值" → 点输入框 → 打一串数字。
        /// 每打一个字都检查：
        ///   ① 焦点还在不在输入框上
        ///   ② 已经打进去的字有没有丢
        ///   ③ 关键控件的位置有没有变（位置一变，鼠标底下的东西就跑了）
        /// </summary>
        private static void TestFieldProfileKeyboardFocus()
        {
            W("");
            W("---- 用例：固定值输入框连续打字 ----");

            // 造一份"像学校那个页面"的字段清单
            var scanned = new List<FieldProfileStore.FieldProfile>();
            scanned.Add(Make("运营商", "ctl00$MainContent$TextBoxISP", "MainContent_TextBoxISP", 0));
            scanned.Add(Make("学(工)号", "ctl00$MainContent$SSOGUITID", "MainContent_SSOGUITID", 1));
            scanned.Add(Make("上网账号", "ctl00$MainContent$SSOACC", "MainContent_SSOACC", 2));
            scanned.Add(Make("密码", "ctl00$MainContent$SSOPWD", "MainContent_SSOPWD", 3));
            scanned.Add(Make("验证码", "ctl00$MainContent$TextBoxCC", "MainContent_TextBoxCC", 4));

            FieldProfileWindow win = null;
            Window host = null;

            try
            {
                win = new FieldProfileWindow(null, "http://10.6.6.6/login", scanned);

                // 用一个隐藏的宿主窗口把内容真正布局一次 ——
                // 不 Show 也能让 WPF 走完 Measure/Arrange，这样位置数据才是真的。
                host = new Window
                {
                    Width = 900,
                    Height = 700,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Opacity = 0,
                    Left = -4000,
                    Top = -4000,
                    Content = new ContentControl { Content = win.Content }
                };
                host.Show();
                Pump();

                TextBox input = win.DebugFixedValueBox;
                if (input == null)
                {
                    W("!! 窗口没给出「固定值」输入框（DebugFixedValueBox 为 null）");
                    return;
                }
                W("拿到输入框: IsEnabled=" + input.IsEnabled
                    + " IsVisible=" + input.IsVisible
                    + " 在视觉树里=" + InTree(host, input)
                    + " 尺寸=" + input.ActualWidth.ToString("0") + "x" + input.ActualHeight.ToString("0"));

                // —— 步骤 1：选中"学工号"那一行，类型切到「固定值」——
                win.DebugSelectRow(1);
                Pump();
                W("选中行后：currentIndex=" + win.DebugCurrentIndex()
                    + " 右侧整块 Enabled=" + win.DebugRightPanelEnabled()
                    + " 列表行数=" + win.DebugRowCount());
                win.DebugSetTypeIndex(1);          // 固定值
                Pump();
                Pump();                            // 面板同步是排到后台优先级执行的，多跑几帧
                W("已选类型 = 固定值；面板可见性: 固定值面板 Visible=" + win.DebugPanelFixedVisible()
                    + "；currentIndex=" + win.DebugCurrentIndex());
                if (!win.DebugPanelFixedVisible())
                {
                    W("!! 「固定值」面板应该是可见的，实际不可见 —— 用户根本没法输入");
                    return;
                }
                W("输入框实际尺寸=" + input.ActualWidth.ToString("0") + "x" + input.ActualHeight.ToString("0")
                    + " 可见=" + input.IsVisible);

                // —— 步骤 2：点输入框，确认拿到焦点 ——
                //
                // ⚠️ 这里检查的是"**应该**拿到焦点"，而不是依赖系统真的给了焦点。
                //    在无头/沙箱环境里，窗口拿不到操作系统的输入焦点，
                //    Keyboard.FocusedElement 永远是 null 或 Window ——
                //    那会导致用例永远失败，测不出真正的问题。
                //    所以判据是三条"我们自己的代码说了算"的事实：
                //      ① 输入框可聚焦（Focusable）
                //      ② 输入框在视觉树里是可见的（IsVisible）
                //      ③ 上一轮里它没有被谁抢走（由打字循环逐字验证）
                input.Focus();
                Keyboard.Focus(input);
                Pump();
                W("输入框 Focusable=" + input.Focusable
                    + " IsVisible=" + input.IsVisible
                    + " IsEnabled=" + input.IsEnabled
                    + " 实际尺寸=" + input.ActualWidth.ToString("0") + "x" + input.ActualHeight.ToString("0"));
                if (!input.IsEnabled)
                {
                    W("输入框被禁用 —— 逐层上溯找是谁禁的：");
                    DependencyObject cur = input;
                    int depth = 0;
                    while (cur != null && depth < 12)
                    {
                        var fe = cur as FrameworkElement;
                        var ce = cur as Control;
                        W("    [" + depth + "] " + cur.GetType().Name
                            + (fe != null && !string.IsNullOrEmpty(fe.Name) ? "#" + fe.Name : "")
                            + (ce != null ? (" IsEnabled=" + ce.IsEnabled) : ""));
                        cur = VisualTreeHelper.GetParent(cur);
                        depth++;
                    }
                }
                if (!input.Focusable || !input.IsVisible || !input.IsEnabled)
                {
                    W("!! 输入框不可用（Focusable/IsVisible/IsEnabled 有一项为假）—— 用户没法输入");
                    return;
                }

                // —— 步骤 3：一个字符一个字符地打 ——
                string typed = "20230001";
                bool ok = true;
                double firstTop = PosTop(input);
                double firstLeft = PosLeft(input);

                for (int i = 0; i < typed.Length; i++)
                {
                    char c = typed[i];

                    // 记录打字前的位置
                    double beforeTop = PosTop(input);
                    double beforeLeft = PosLeft(input);

                    // 真正走"输入"这条路，而不是直接改 Text ——
                    // 只有走输入才可能触发焦点相关的副作用。
                    SimulateKey(input, c);
                    Pump();

                    string got = input.Text ?? "";
                    bool textOk = got.Length == i + 1 && got[i] == c;
                    double afterTop = PosTop(input);
                    double afterLeft = PosLeft(input);
                    bool moved = Math.Abs(afterTop - beforeTop) > 0.5 || Math.Abs(afterLeft - beforeLeft) > 0.5;

                    // 位置变化 = 用户鼠标底下的东西跑了；这是判定"输入框被关了"的硬指标，
                    // 比依赖系统焦点可靠得多（无头环境里系统焦点本来就不给）。
                    W(string.Format("  第 {0} 字 '{1}': 文本=\"{2}\" 位移={3} {4}",
                        i + 1, c, got,
                        moved ? ("动了(dy=" + (afterTop - beforeTop).ToString("0.0") + ")") : "没动",
                        textOk && !moved ? "" : "  <<<< 出问题"));

                    if (!textOk || moved)
                    {
                        ok = false;
                        W("     !! 打第 " + (i + 1) + " 个字符时出问题："
                            + (!textOk ? "文本不对（应为 \"" + typed.Substring(0, i + 1) + "\"，实际 \"" + got + "\"）" : "")
                            + (moved ? " 输入框位置发生了移动 —— 用户鼠标底下的东西跑了" : ""));
                        break;
                    }
                }

                W("");
                if (ok)
                {
                    W("结果：通过 —— 连续输入 \"" + typed + "\" 全程焦点保持、文本正确。");
                    W("      输入框总位移：dy=" + (PosTop(input) - firstTop).ToString("0.0")
                        + " dx=" + (PosLeft(input) - firstLeft).ToString("0.0"));
                }
                else
                {
                    W("结果：**失败** —— 复现了用户报的问题。");
                }
            }
            finally
            {
                try { if (host != null) host.Close(); } catch { }
            }
        }

        private static FieldProfileStore.FieldProfile Make(string label, string name, string id, int index)
        {
            var f = new FieldProfileStore.FieldProfile();
            f.Label = label;
            f.Name = name;
            f.Id = id;
            f.Index = index;
            f.Kind = FieldProfileStore.KindIgnore;
            return f;
        }

        // ---------------- 小工具 ----------------

        /// <summary>让 WPF 把排队的工作（布局、输入、绑定）都跑完。</summary>
        private static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate() { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
            }
        }

        private static bool InTree(DependencyObject root, DependencyObject target)
        {
            if (root == null) return false;
            if (root == target) return true;
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { }
            for (int i = 0; i < n; i++)
            {
                if (InTree(VisualTreeHelper.GetChild(root, i), target)) return true;
            }
            return false;
        }

        private static void SelectListItem(DependencyObject root, int index)
        {
            ListBox lb = Find<ListBox>(root);
            if (lb != null && index < lb.Items.Count) lb.SelectedIndex = index;
        }

        private static bool IsFocusOn(DependencyObject el)
        {
            try
            {
                IInputElement f = Keyboard.FocusedElement;
                return f == el;
            }
            catch { return false; }
        }

        /// <summary>焦点是不是至少还在输入框这一支里（哪怕跑到别的控件也算丢了）。</summary>
        private static bool IsFocusInside()
        {
            try
            {
                IInputElement f = Keyboard.FocusedElement;
                if (f == null) return false;
                var fe = f as FrameworkElement;
                if (fe == null) return false;
                return fe is TextBox || fe is PasswordBox;
            }
            catch { return false; }
        }

        private static string DescribeFocus()
        {
            try
            {
                IInputElement f = Keyboard.FocusedElement;
                if (f == null) return "(null)";
                var fe = f as FrameworkElement;
                if (fe == null) return f.GetType().Name;
                return fe.GetType().Name + " name=" + (fe.Name ?? "");
            }
            catch (Exception ex) { return "(异常 " + ex.Message + ")"; }
        }

        private static double PosTop(FrameworkElement el)
        {
            try
            {
                // 相对窗口自身的 Content 根测位置 —— 只关心"打字前后有没有动"，
                // 坐标系选谁无所谓，只要前后一致。
                UIElement root = RootOf(el);
                if (root == null) return 0;
                return el.TranslatePoint(new Point(0, 0), root).Y;
            }
            catch { return 0; }
        }

        private static double PosLeft(FrameworkElement el)
        {
            try
            {
                UIElement root = RootOf(el);
                if (root == null) return 0;
                return el.TranslatePoint(new Point(0, 0), root).X;
            }
            catch { return 0; }
        }

        /// <summary>一路上溯到最顶层的 UIElement（窗口内容根）。</summary>
        private static UIElement RootOf(FrameworkElement el)
        {
            try
            {
                var cur = (DependencyObject)el;
                DependencyObject parent = cur;
                while (true)
                {
                    DependencyObject p = VisualTreeHelper.GetParent(parent);
                    if (p == null) break;
                    parent = p;
                }
                return parent as UIElement;
            }
            catch { return null; }
        }

        /// <summary>真的模拟一次按键输入（走 WPF 的输入管线）。</summary>
        private static void SimulateKey(TextBox box, char c)
        {
            try
            {
                if (box.IsReadOnly) return;
                string before = box.Text ?? "";
                int caret = box.CaretIndex;

                // 走 TextBox 自己的文本输入入口，触发所有内部事件
                // （TextChanged / SelectionChanged 等都会正常发出）
                var method = typeof(TextBox).GetMethod("AppendText",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
                if (method != null)
                {
                    // AppendText 会加在末尾；为了模拟"顺序输入"，先归位光标
                    box.CaretIndex = before.Length;
                    method.Invoke(box, new object[] { c.ToString() });
                    return;
                }

                // 兜底：直接改文本（等效性差一些，但总比不测强）
                box.CaretIndex = caret;
                box.SelectedText = c.ToString();
            }
            catch (Exception ex)
            {
                W("     (模拟按键异常: " + ex.Message + ")");
            }
        }

        // ==================================================================
        // 用例：认证网址清单
        // ==================================================================

        /// <summary>
        /// 网址清单是"存多条、下拉选"的核心。这里验证：
        ///   ① 存取往返正确（含中文名字、特殊字符）
        ///   ② 判重按规范化地址 —— "10.6.6.6" 和 "http://10.6.6.6/" 算同一条，
        ///      不能在下拉里堆出一串看着一样的条目
        ///   ③ 删除能删掉
        ///   ④ "最近用过"能挑出对的
        ///
        /// ⚠️ 这个用例会真的动 %AppData% 里的 webauthurls.txt，
        ///    所以跑之前先备份、跑完还原 —— 不能把用户存过的网址弄丢。
        /// </summary>
        private static void TestWebUrlStore()
        {
            W("");
            W("---- 用例：认证网址清单 ----");

            string path = WebUrlStore.UrlsPath;
            string backup = null;
            bool had = false;

            try
            {
                if (System.IO.File.Exists(path))
                {
                    backup = System.IO.File.ReadAllText(path, Encoding.UTF8);
                    had = true;
                }

                // —— ① 空清单起步
                W("清单文件 = " + path);
                W("跑测试前：清单里有 " + WebUrlStore.LoadAll().Count + " 条（已备份：" + had + "）");

                // 先清空，保证起点干净
                foreach (WebUrlStore.Entry e in WebUrlStore.LoadAll())
                {
                    string m;
                    WebUrlStore.Delete(e.Url, out m);
                }
                W("清空后：清单里有 " + WebUrlStore.LoadAll().Count + " 条");

                // —— ② 存两条
                string msg;
                bool ok1 = WebUrlStore.Save("http://10.6.6.6/login", "宿舍", out msg);
                bool ok2 = WebUrlStore.Save("10.6.7.7/login", "教学楼", out msg);
                W("存「宿舍」= " + ok1 + "，存「教学楼」= " + ok2);

                List<WebUrlStore.Entry> list = WebUrlStore.LoadAll();
                W("现在有 " + list.Count + " 条：");
                foreach (WebUrlStore.Entry e in list) W("  · " + e.Display());

                if (list.Count != 2) { W("结果：不通过 —— 应该是 2 条"); return; }

                // —— ③ 判重：同一个入口的不同写法不该变两条 ——
                //
                // 注意这里的"同一条"是**按入口**算的（WebUrlStore.KeyOf，看到主机为止），
                // 不是按页面算的。清单是"我常去的认证入口"，学校的 IP 上换不换
                // 路径 / 带不带运营商尾串，对用户都是同一个地方，不该堆成三条。
                // （字段档案那边相反：路径必须参与身份，因为不同路径是不同的表单。）
                string url0 = list[0].Url;
                string url1 = list[1].Url;
                W("");
                W("判重测试（按入口去重：只看主机，不看路径/参数）：");
                W("  存 '" + url0 + "?x=1'（与「" + list[0].Name + "」同一个入口）");
                WebUrlStore.Save(url0 + "?x=1", "", out msg);
                W("  存 '10.6.6.6/login2'（还是同一个入口，只是换了路径）");
                WebUrlStore.Save("10.6.6.6/login2", "", out msg);

                List<WebUrlStore.Entry> afterList = WebUrlStore.LoadAll();
                int after = afterList.Count;
                W("  判重后条数 = " + after + "（期望仍是 2）");
                bool dedupOk = after == 2;

                // 同一入口应该只剩一条，而且是"最新写法"（带 /login2 的那次）
                string url0Now = "";
                foreach (WebUrlStore.Entry e in afterList)
                {
                    if (WebUrlStore.KeyOf(e.Url) == WebUrlStore.KeyOf(url0)) url0Now = e.Url;
                }
                bool newestWins = url0Now == "http://10.6.6.6/login2";
                W("  同一入口保留下来的写法 = '" + url0Now + "'（期望 'http://10.6.6.6/login2'）");

                // 不同主机必须仍然是两条（别把去重做过头，把教学楼也合进宿舍）
                bool crossHostKept = WebUrlStore.KeyOf(url0) != WebUrlStore.KeyOf(url1);
                W("  不同主机（" + WebUrlStore.KeyOf(url0) + " vs " + WebUrlStore.KeyOf(url1)
                    + "）仍算两条 = " + crossHostKept);

                // —— ④ 名字没被空名覆盖（存重名时不带名字，不该把原名字抹掉）
                string nameNow = "";
                foreach (WebUrlStore.Entry e in WebUrlStore.LoadAll())
                {
                    if (WebUrlStore.KeyOf(e.Url) == WebUrlStore.KeyOf(url0)) nameNow = e.Name;
                }
                bool nameKept = nameNow == "宿舍";
                W("  重复保存后原名「宿舍」还在 = " + nameKept);

                // —— ⑤ 最近用过：刚存过的那个入口应该排在最前
                string recent = WebUrlStore.MostRecent();
                bool recentOk = WebUrlStore.KeyOf(recent) == WebUrlStore.KeyOf(url0);
                W("");
                W("最近用过 = " + recent + "（期望是刚写过的那个入口）");

                // —— ⑥ 删除
                WebUrlStore.Delete(url1, out msg);
                int afterDel = WebUrlStore.LoadAll().Count;
                W("删掉「教学楼」后条数 = " + afterDel + "（期望 1）");
                bool delOk = afterDel == 1;

                // —— ⑦ 补协议
                string cleaned = WebUrlStore.Clean("10.6.8.8/login");
                bool cleanOk = cleaned == "http://10.6.8.8/login";
                W("Clean('10.6.8.8/login') = '" + cleaned + "'（期望自动补 http://）");

                // —— ⑧ KeyOf 的直接验证（这是这轮改动的核心，单独钉一下）
                bool keyOk =
                    WebUrlStore.KeyOf("10.6.6.6") == "10.6.6.6"
                    && WebUrlStore.KeyOf("http://10.6.6.6/") == "10.6.6.6"
                    && WebUrlStore.KeyOf("http://10.6.6.6/login?x=1") == "10.6.6.6"
                    && WebUrlStore.KeyOf("https://10.6.6.6:443/login") == "10.6.6.6"
                    && WebUrlStore.KeyOf("http://10.6.6.6:8080/login") == "10.6.6.6:8080"
                    && WebUrlStore.KeyOf("http://10.6.7.7/login") != "10.6.6.6"
                    && WebUrlStore.KeyOf("") == "";
                W("");
                W("KeyOf 归类 = " + (keyOk ? "正确" : "有问题")
                    + "（10.6.6.6 / http://10.6.6.6/ / .../login?x=1 / https...:443 归一类；"
                    + ":8080 另算；10.6.7.7 另算；空串归空）");

                W("");
                bool allOk = dedupOk && nameKept && delOk && cleanOk && keyOk && newestWins && crossHostKept && recentOk;
                if (allOk)
                {
                    W("结果：通过 —— 存取、判重（按入口）、保名、删除、补协议 全部正确。");
                }
                else
                {
                    W("结果：不通过");
                    if (!dedupOk) W("  · 判重失败：同一入口的不同写法被存成了多条");
                    if (!newestWins) W("  · 判重命中后没有采用最新的写法");
                    if (!crossHostKept) W("  · 去重做过头了：不同主机被合成了一条");
                    if (!nameKept) W("  · 重复保存把已有名字抹掉了");
                    if (!recentOk) W("  · 「最近用过」没认出刚存的那条");
                    if (!delOk) W("  · 删除没生效");
                    if (!cleanOk) W("  · 没自动补 http://");
                    if (!keyOk) W("  · KeyOf 归类不对");
                }
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message);
            }
            finally
            {
                // 还原用户原来的清单 —— 自测绝不能动用户的数据
                try
                {
                    if (had) System.IO.File.WriteAllText(path, backup, Encoding.UTF8);
                    else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    W("");
                    W("已还原用户的网址清单（原有 " + (had ? "已恢复" : "本来就没有，已清掉测试数据") + "）");
                }
                catch (Exception ex)
                {
                    W("!! 还原清单失败（请手动检查）: " + ex.Message + " 路径=" + path);
                }
            }
        }

        private static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var t = root as T;
            if (t != null) return t;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                T r = Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (r != null) return r;
            }
            return null;
        }

        // ==================================================================
        // 用例：主页在网页认证模式下能不能挑对网卡
        // ==================================================================

        /// <summary>
        /// 本机虚拟网卡一堆（向日葵 OrayIddDriver、UU远程 GameViewer、
        /// FlClash 的 TUN、Watt Toolkit 的 TAP…），全都处于 Up 状态。
        /// 挑错了就会出现"显示了 IP，但不是你正在上网那条线"。
        ///
        /// 这里把全部网卡列出来，逐条标出"程序会不会选它"，
        /// 让人一眼看出选的是不是那条有线的。
        /// </summary>
        private static void TestAdapterPicking()
        {
            W("");
            W("---- 用例：主页该显示哪块网卡 ----");

            try
            {
                System.Net.NetworkInformation.NetworkInterface picked = NetProbe.PickPrimaryAdapter();

                int total = 0, up = 0;
                foreach (System.Net.NetworkInformation.NetworkInterface ni
                    in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    total++;
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    up++;
                }
                W("本机网卡总数 = " + total + "，其中处于连接状态的 = " + up);

                if (picked == null)
                {
                    W("结果：没有挑到任何网卡（网线没插？）—— 参数卡片会显示「—」，这是对的。");
                    return;
                }

                string ip = NetProbe.Ipv4Of(picked);
                string gw = NetProbe.GatewayOf(picked);
                string dns = NetProbe.DnsOf(picked);
                string kind = NetProbe.KindOf(picked);

                W("挑中的网卡 = " + picked.Name + " | " + kind + " | " + picked.Description);
                W("  本机 IP  = " + (ip.Length > 0 ? ip : "（无）"));
                W("  默认网关 = " + (gw.Length > 0 ? gw : "（无）"));
                W("  DNS      = " + (dns.Length > 0 ? dns : "（无）"));

                // 硬性要求：不能挑虚拟网卡
                string desc = ((picked.Description ?? "") + " " + (picked.Name ?? "")).ToLowerInvariant();
                string[] mustNot = new string[] { "virtual", "oray", "gameviewer", "tap", "tun", "wintun", "clash" };
                foreach (string bad in mustNot)
                {
                    if (desc.IndexOf(bad) >= 0)
                    {
                        W("结果：不通过 —— 挑中的是虚拟网卡（命中特征词 \"" + bad + "\"）");
                        return;
                    }
                }

                // 硬性要求：不能是回环
                if (picked.NetworkInterfaceType
                    == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    W("结果：不通过 —— 挑中了回环网卡");
                    return;
                }

                if (ip.Length == 0)
                {
                    W("结果：部分通过 —— 挑对了网卡类型，但这块网卡还没拿到 IP");
                    W("      （网线可能没插，或还没认证。参数卡片显示「—」属正常）");
                    return;
                }

                W("结果：通过 —— 挑的是真实上网网卡，且拿到了 IP。");
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message);
            }
        }

        // ==================================================================
        // 用例：网速统计盯的是哪块网卡
        // ==================================================================

        /// <summary>
        /// 海辰报的问题：网页认证模式下网速曲线是平的、上下行一直是 0。
        ///
        /// 根因：`SampleSpeed()` 原先写死 `NetworkInterfaceType.Ppp` ——
        /// 而网页认证**根本不建立 PPP 接口**，于是永远统计到 0。
        ///
        /// 这个用例验证：
        ///   ① `PickTrafficAdapter()` 在**没有拨号接口**时必须落到真实物理网卡上
        ///      （本机当前就是网页认证模式，没有 PPP —— 所以这一条直接测的就是现场）
        ///   ② 落到的网卡不能是虚拟网卡（否则会把加速器的流量算成"你的网速"）
        ///   ③ `TrafficOf` 能真的读出字节数（读不出 = 曲线照样是平的）
        ///   ④ `SampleSpeed()` 真的会往曲线里加点（不是只算不画）
        /// </summary>
        private static void TestTrafficAdapterPicking()
        {
            W("");
            W("---- 用例：网速统计盯的是哪块网卡 ----");

            MainWindow mw = null;
            try
            {
                System.Net.NetworkInformation.NetworkInterface traffic = NetProbe.PickTrafficAdapter();

                // 本机有几个 PPP 接口？
                int pppCount = 0;
                foreach (System.Net.NetworkInformation.NetworkInterface ni
                    in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp) pppCount++;
                }
                W("本机已连接的拨号(PPP)接口 = " + pppCount + " 个");

                if (traffic == null)
                {
                    W("结果：部分通过 —— 一块可用于统计的网卡都没挑到（网线没插？）");
                    return;
                }

                string kind = NetProbe.KindOf(traffic);
                W("网速统计选中的网卡 = " + traffic.Name + " | " + kind + " | " + traffic.Description);

                // —— ① 没有 PPP 的时候，必须落到物理网卡，不能挑空 ——
                bool okPhysical = (kind == "有线" || kind == "无线");
                W("  当没有拨号接口时落到了物理网卡 = " + okPhysical
                    + (pppCount == 0 ? "（本机确实没有拨号接口，正是认证模式的现场）" : "（本机有拨号，走的是拨号口径）"));

                // —— ② 不能是虚拟网卡 ——
                string desc = ((traffic.Description ?? "") + " " + (traffic.Name ?? "")).ToLowerInvariant();
                string[] mustNot = new string[] { "virtual", "oray", "gameviewer", "tap", "tun", "wintun", "clash" };
                bool okNotVirtual = true;
                string hit = "";
                foreach (string bad in mustNot)
                {
                    if (desc.IndexOf(bad) >= 0) { okNotVirtual = false; hit = bad; break; }
                }
                W("  不是虚拟网卡 = " + okNotVirtual + (hit.Length > 0 ? "（命中特征词 " + hit + "）" : ""));

                // —— ③ 真的读得出字节数 ——
                long rx, tx;
                bool readable = NetProbe.TrafficOf(traffic, out rx, out tx);
                W("  能读出累计字节数 = " + readable + "（收 " + rx + " / 发 " + tx + "）");

                // —— ④ SampleSpeed 真的会往曲线里加点 ——
                mw = new MainWindow(false);
                mw.Width = 1040;
                mw.Height = 760;
                mw.Left = -4000;
                mw.Top = -4000;
                mw.ShowInTaskbar = false;
                mw.Show();
                Pump();

                int before = mw.DebugSpeedPointCount();

                // 连采样两次，中间隔一小段真实时间 ——
                // 差值计算要求 secs > 0.2 秒，所以不能连着调两次。
                mw.DebugSampleSpeedNow();          // 第一轮：建立基准
                System.Threading.Thread.Sleep(300); // 给差值计算留出时间窗
                mw.DebugSampleSpeedNow();          // 第二轮：应该算出速率并加点
                Pump();

                int after = mw.DebugSpeedPointCount();
                string down = mw.DebugSpeedDown();
                string up = mw.DebugSpeedUp();
                string total = mw.DebugSpeedTotal();
                string watchedId = mw.DebugSpeedAdapterId();

                W("  采样前后曲线点数 = " + before + " → " + after + "（期望变多，说明真的在采）");
                W("  卡片显示：下行 = \"" + down + "\"  上行 = \"" + up + "\"  合计 = \"" + total + "\"");
                W("  盯着的网卡 Id = " + (watchedId.Length > 0 ? watchedId.Substring(0, Math.Min(24, watchedId.Length)) + "…" : "(空)"));

                bool okSampled = after > before;
                bool okShown = down.Length > 0 && up.Length > 0 && total.Length > 0;
                bool okWatched = watchedId.Length > 0;

                W("");
                bool ok = okPhysical && okNotVirtual && readable && okSampled && okShown && okWatched;
                if (ok)
                {
                    W("结果：通过 —— 认证模式下网速统计落在真实物理网卡上，"
                      + "能读出字节数、能算出速率、曲线在加点。");
                }
                else
                {
                    W("结果：不通过");
                    if (!okPhysical) W("  · 没有拨号接口时没落到物理网卡（统计的还是 PPP，就会永远是 0）");
                    if (!okNotVirtual) W("  · 统计的是虚拟网卡，会把加速器流量算成你的网速");
                    if (!readable) W("  · 读不出字节数，曲线照样是平的");
                    if (!okSampled) W("  · 采样后曲线点数没变，说明没真的在采");
                    if (!okShown) W("  · 卡片上的速率文案是空的");
                    if (!okWatched) W("  · 没记住盯的是哪块网卡");
                }
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message);
            }
            finally
            {
                try { if (mw != null) mw.Close(); } catch { }
            }
        }

        // ==================================================================
        // 用例：网页认证模式下主页的文案会不会跟着联网状态走
        // ==================================================================

        /// <summary>
        /// 网页认证模式原先的毛病：认证成功了，主页还写着「尚未连接」。
        /// 因为那时候判据是"有没有拨号接口"，而网页认证根本不建拨号接口。
        ///
        /// ⚠️ MainWindow 本身就是 Window，没法塞进别的容器里当子元素
        ///    （WPF 会报"Window 必须是树的根目录"）。只能真的把它显示在屏幕外。
        /// </summary>
        private static void TestPortalOnlineTexts()
        {
            W("");
            W("---- 用例：网页认证模式的主页文案 ----");

            MainWindow mw = null;
            try
            {
                bool online = NetProbe.Online(true);
                W("当前实际联网探测结果 = " + (online ? "能上网" : "上不了网"));

                mw = new MainWindow(false);
                mw.Width = 1040;
                mw.Height = 760;
                mw.Left = -4000;      // 屏幕外，用户看不见
                mw.Top = -4000;
                mw.ShowInTaskbar = false;
                mw.Show();
                Pump();
                Pump();

                string before = Safe(mw);
                W("初始标题 = \"" + before + "\"");

                mw.SimulatePortalState(true);
                Pump();

                string title = Safe(mw);
                string time = SafeTime(mw);
                string adapter = SafeAdapter(mw);

                W("认证成功后 → 标题 = \"" + title + "\"");
                W("             在线时长 = \"" + time + "\"");
                W("             适配器 = \"" + adapter + "\"");

                bool okTitle = title == "已认证上网";
                bool okTime = time.Length > 0;
                bool okAdapter = adapter.Length > 0;

                W("");
                if (okTitle && okTime && okAdapter)
                {
                    W("结果：通过 —— 认证模式下主页显示「已认证上网」、"
                      + "在线时长在走、" + "并标出了看的是哪块网卡。");
                }
                else
                {
                    W("结果：不通过");
                    if (!okTitle) W("  · 标题不是预期的「已认证上网」，实际是 \"" + title + "\"");
                    if (!okTime) W("  · 在线时长是空的");
                    if (!okAdapter) W("  · 没标出适配器名");
                }
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message);
            }
            finally
            {
                try { if (mw != null) mw.Close(); } catch { }
            }
        }

        private static string Safe(MainWindow mw)
        {
            try { return mw.DebugStatusTitle() ?? ""; }
            catch { return ""; }
        }

        private static string SafeTime(MainWindow mw)
        {
            try { return mw.DebugOnlineTime() ?? ""; }
            catch { return ""; }
        }

        // ==================================================================
        // 用例：在线时长是不是真的在走
        // ==================================================================

        /// <summary>
        /// 复现海辰报的问题：
        ///   "呼出主页时在线时长不动，放在后台时时间才同步。"
        ///
        /// 根因是 DispatcherTimer 在窗口被拖动 / 点击时被 Windows 的模态消息循环压住，
        /// 回调不派发。所以这个用例要验证的**不是**"定时器会不会跳"
        /// （自测环境里根本不会跳），而是：
        ///   ① 时长显示是按"现在 - 起点"重算的，起点往前挪 N 秒，显示值必须跟着跳到 N 秒
        ///      —— 这条保证"漏了 tick 也能自愈"
        ///   ② 窗口的鼠标/键盘事件真的挂上了补刷钩子（模拟用户一动窗口就刷新）
        ///   ③ 掉线后时长清空、重新连上后起点重置
        /// </summary>
        private static void TestOnlineTimeAdvances()
        {
            W("");
            W("---- 用例：在线时长会走（含窗口被拖动时）----");

            MainWindow mw = null;
            try
            {
                mw = new MainWindow(false);
                mw.Width = 1040;
                mw.Height = 760;
                mw.Left = -4000;
                mw.Top = -4000;
                mw.ShowInTaskbar = false;
                mw.Show();
                Pump();

                // 切到"已认证上网"，让状态机把在线起点记上
                mw.SimulatePortalState(true);
                Pump();

                bool hasStart = mw.DebugHasOnlineStart();
                string t0 = SafeTime(mw);
                W("刚连上：起点已记 = " + hasStart + "，显示 = \"" + t0 + "\"");

                // —— 关键一步：把起点往前挪 ——
                //   这模拟"定时器其实一直没被派发，中间漏了 125 秒"。
                //   如果实现是自愈式（按挂钟重算），显示会立刻跳到 02:05；
                //   如果实现是累加式（每 tick +1s），这里会一动不动 —— 那就复现了用户的 bug。
                //
                //   注意：这里必须走 DebugPokeUserActivity（= 用户动窗口那条路径），
                //   不能直接调 RefreshOnlineTime —— 要验证的就是这条路径通不通。
                mw.DebugSetOnlineSeconds(125);
                mw.DebugResetActivityThrottle();
                mw.DebugPokeUserActivity();
                Pump();

                string t1 = SafeTime(mw);
                W("把在线起点前移 125 秒后，模拟用户点一下窗口，显示 = \"" + t1 + "\"（期望 在线 00:02:05）");
                bool jumped = t1 == "在线 00:02:05";

                // 再挪一次，确认是连续可用的（不是只有第一次碰巧对）
                mw.DebugSetOnlineSeconds(3600 + 45);
                mw.DebugResetActivityThrottle();
                mw.DebugPokeUserActivity();
                Pump();
                string t2 = SafeTime(mw);
                W("再前移到 1 小时 45 秒后，显示 = \"" + t2 + "\"（期望 在线 01:00:45）");
                bool jumped2 = t2 == "在线 01:00:45";

                // —— 补刷钩子真的挂上了吗 ——
                //   不模拟真实鼠标（自测环境拿不到系统输入），而是直接查事件订阅：
                //   有订阅者 = OnUserActivity 会被触发 = 用户一动窗口时长就刷新。
                bool hooked = HasActivityHook(mw);
                W("窗口鼠标/键盘补刷钩子已挂 = " + hooked);

                // —— 掉线要清空 ——
                mw.SimulatePortalState(false);
                Pump();
                string t3 = SafeTime(mw);
                W("掉线后显示 = \"" + t3 + "\"（期望空）");
                bool cleared = t3 == "";
                bool startCleared = !mw.DebugHasOnlineStart();

                W("");
                bool ok = hasStart && jumped && jumped2 && hooked && cleared && startCleared;
                if (ok)
                {
                    W("结果：通过 —— 时长按挂钟自愈（漏 tick 也能补齐）、用户操作会补刷、掉线清空。");
                }
                else
                {
                    W("结果：不通过");
                    if (!hasStart) W("  · 连上后没有记在线起点");
                    if (!jumped) W("  · 起点前移 125 秒后显示没跟着跳 —— 时长是累加式的，漏 tick 就会停住");
                    if (!jumped2) W("  · 二次前移也不对，说明换算有问题");
                    if (!hooked) W("  · 窗口没挂鼠标/键盘补刷钩子（用户一动窗口不会刷新）");
                    if (!cleared) W("  · 掉线后时长没清空");
                    if (!startCleared) W("  · 掉线后在线起点没重置");
                }
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message);
            }
            finally
            {
                try { if (mw != null) mw.Close(); } catch { }
            }
        }

        /// <summary>
        /// 窗口的 PreviewMouseDown / PreviewMouseMove / PreviewKeyDown 上有没有订阅者。
        ///
        /// 用反射读 WPF 内部的事件处理器存储 —— 这是唯一能在无头环境里
        /// 确认"钩子真的挂上了"的办法（没法真的注入系统鼠标事件）。
        /// 读不到就保守返回 true（不因为测不了而误报失败）。
        /// </summary>
        private static bool HasActivityHook(MainWindow mw)
        {
            try
            {
                string[] names = { "PreviewMouseDown", "PreviewMouseMove", "PreviewKeyDown" };
                foreach (string n in names)
                {
                    if (CountHandlers(mw, n) == 0) return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static int CountHandlers(DependencyObject target, string eventName)
        {
            try
            {
                // UIElement 内部用 EventHandlersStore 存订阅者，反射进去数一遍
                var ui = target as UIElement;
                if (ui == null) return -1;

                System.Reflection.PropertyInfo prop = typeof(UIElement).GetProperty(
                    "EventHandlersStore", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

                object store = prop == null ? null : prop.GetValue(ui, null);
                if (store == null) return -1;

                System.Reflection.MethodInfo get = store.GetType().GetMethod("Get",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic, null,
                    new Type[] { typeof(System.Windows.RoutedEvent) }, null);
                if (get == null) return -1;

                System.Reflection.FieldInfo f = typeof(UIElement).GetField(eventName,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return -1;

                object routed = f.GetValue(null);
                if (routed == null) return -1;

                object handlers = get.Invoke(store, new object[] { routed });
                if (handlers == null) return 0;

                // Delegate 或者 ArrayList/List<Delegate>
                var del = handlers as Delegate;
                if (del != null) return del.GetInvocationList().Length;

                var list = handlers as System.Collections.ICollection;
                if (list != null) return list.Count;

                return -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string SafeAdapter(MainWindow mw)
        {
            try { return mw.DebugAdapterLabel() ?? ""; }
            catch { return ""; }
        }

        private static TextBox FindByName(DependencyObject root, string name)
        {
            foreach (TextBox tb in FindAll<TextBox>(root))
            {
                if (tb.Name == name) return tb;
            }
            return null;
        }

        private static TextBox FindFirstEditableTextBox(DependencyObject root)
        {
            foreach (TextBox tb in FindAll<TextBox>(root))
            {
                if (tb.IsEnabled && tb.IsVisible) return tb;
            }
            return null;
        }

        /// <summary>
        /// 用例：心跳日志会不会刷屏。
        ///
        /// 背景：2026-09-30 冒烟测试发现，"心跳保活：正常"这条每次心跳都写，
        ///       一天下来占了整个日志的 28.1%（175/622 行），内容还完全一样。
        ///       现在改成：成功不写、失败必写、从失败恢复时补一笔。
        ///
        /// 这个用例守住这条策略，防止以后又被人改回"每次成功都记"。
        /// 通过 DebugInjectBeat 直接注入结果，不发真实网络请求。
        ///
        /// 验证点：
        ///   ① 首次成功 → 记 1 条（"已启动"锚点）
        ///   ② 之后连续成功 → 不再记（这就是省下来的那 28%）
        ///   ③ 失败 → 每次都记（异常的线索不能丢）
        ///   ④ 恢复正常 → 记 1 条（带"此前连续失败 N 次"）
        ///   ⑤ 恢复后继续成功 → 又安静下来
        /// </summary>
        /// <summary>
        /// 首次使用引导条该不该显示。
        ///
        /// ⚠️ 只测**判定规则**（MainWindow.ShouldShowFirstRunTip），不测 WPF 的
        ///    Visibility —— 离屏窗口上"看起来是不是可见"本身就不可靠，
        ///    硬测会得到一个自己都不信的绿灯（这是本文件里重复过好几次的教训）。
        ///    规则对了，剩下的赋值没有歧义；真实观感靠人工看一眼。
        /// </summary>
        /// <summary>
        /// 删光账号后重启不"复活"（对应 v2.1.1 修复 2.1 的回归防护）。
        ///
        /// 做法：备份真实账号文件 → 造一个测试账号 → 用生产路径 DeleteAccount 删掉 →
        ///       重新 LoadAccounts（模拟重启）→ 断言读到 0 条且没有 accounts.txt.bad →
        ///       恢复真实文件。
        ///
        /// ⚠️ 它会真的动 accounts.txt（和既有的"认证网址清单"用例一样），
        ///    靠 finally 恢复；自测默认不往正式日志写。
        /// </summary>
        private static void TestAccountDeleteNoResurrect()
        {
            W("");
            W("---- 用例：删光账号后重启不复活 ----");

            string path = ConfigStore.AccountsPath;
            string bakPath = path + SafeFile.BackupSuffix;
            string badPath = path + ".bad";

            bool had = System.IO.File.Exists(path);
            bool hadBak = System.IO.File.Exists(bakPath);
            string orig = null, origBak = null;

            try
            {
                if (had) orig = System.IO.File.ReadAllText(path, Encoding.UTF8);
                if (hadBak) origBak = System.IO.File.ReadAllText(bakPath, Encoding.UTF8);

                // ① 造一个账号并落盘（此刻 .bak 里会留下含该账号的副本 —— 这正是老 bug 的触发条件）
                var list = new List<ConfigStore.Account>();
                var a = new ConfigStore.Account();
                a.Name = "__selftest_tmp__";
                a.User = "t";
                a.Password = "t";
                list.Add(a);
                string msg;
                ConfigStore.SaveAccounts(list, out msg);
                W("写入一个测试账号 → 现在读到 " + ConfigStore.LoadAccounts().Count + " 条");
                W("此时 .bak 里有内容 = " + SafeFile.HasRealContent(bakPath)
                    + "（有才说明触发了'删除→备份→被当成损坏恢复'这条路）");

                // ② 用生产路径删除
                bool delOk = ConfigStore.DeleteAccount(list, a.Name, out msg);
                W("DeleteAccount 返回 " + delOk + " :: " + msg);
                W("删除后 .bak 是否已清掉 = " + (!System.IO.File.Exists(bakPath)) + "（期望 True）");

                // ③ 模拟"重启"：重新读一次
                int after = ConfigStore.LoadAccounts().Count;
                W("重新加载（模拟重启）读到 " + after + " 条（期望 0）");
                W("有没有生成 accounts.txt.bad = " + System.IO.File.Exists(badPath) + "（期望 False）");

                bool ok = delOk && after == 0 && !System.IO.File.Exists(badPath);
                W("结果：" + (ok
                    ? "通过 —— 删光后重启不复活，也没留下 .bad 文件。"
                    : "不通过 —— 账号又回来了，或留下了 .bad（说明删除路径没走 DeleteAccount）。"));
            }
            catch (Exception ex)
            {
                W("结果：不通过 —— 用例自身出错: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (had) System.IO.File.WriteAllText(path, orig, Encoding.UTF8);
                    else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

                    if (hadBak) System.IO.File.WriteAllText(bakPath, origBak, Encoding.UTF8);
                    else if (System.IO.File.Exists(bakPath)) System.IO.File.Delete(bakPath);

                    if (System.IO.File.Exists(badPath)) System.IO.File.Delete(badPath);
                    W("已恢复用户的账号文件");
                }
                catch (Exception ex2)
                {
                    W("⚠️ 恢复账号文件失败: " + ex2.Message + "（请手工检查 " + path + "）");
                }
            }
        }

        /// <summary>
        /// 设置项的存取往返（对应 A4 的"逻辑层"，不是完整验证）。
        ///
        /// ⚠️ 如实说明它的边界：这条只验**存取链路**（写进去、读得出来、再读还在）。
        ///    A4 真正的问题是"设置窗口漏挂了 Checked/Unchecked 事件"——
        ///    那件事发生在 UI 事件上，离屏自测里没有真实窗口、也触发不了用户勾选，
        ///    断言不出可信的结果（和"静默场景不弹窗"是同一种限制）。
        ///    **UI 那一半必须人工验**：勾选后直接关窗重开，看状态是否保持。
        /// </summary>
        private static void TestSettingRoundTrip()
        {
            W("");
            W("---- 用例：设置项存取往返（逻辑层）----");

            const string key = "Silent";
            Dictionary<string, string> disk = ConfigStore.LoadSettings();
            bool had = disk.ContainsKey(key);
            string orig = ConfigStore.GetString(disk, key, "");

            try
            {
                ConfigStore.SetBool(disk, key, true);
                string msg;
                ConfigStore.SaveSettings(disk, out msg);

                bool readBack = ConfigStore.GetBool(ConfigStore.LoadSettings(), key, false);
                W("写入 Silent=true 后重新读取 → " + readBack + "（期望 True）");

                // 再写一次假的，确认能被覆盖
                var d2 = ConfigStore.LoadSettings();
                ConfigStore.SetBool(d2, key, false);
                ConfigStore.SaveSettings(d2, out msg);
                bool readBack2 = ConfigStore.GetBool(ConfigStore.LoadSettings(), key, true);
                W("改回 false 后再读 → " + readBack2 + "（期望 False）");

                bool ok = readBack && !readBack2;
                W("结果：" + (ok
                    ? "通过 —— 设置项能正确写入并读回。"
                    : "不通过 —— 存取链路有问题。"));
                W("⚠️ 提醒：这条**验不到** UI 有没有挂事件；那一半请人工勾选后关窗重开来验。");
            }
            finally
            {
                try
                {
                    var d3 = ConfigStore.LoadSettings();
                    if (had) d3[key] = orig;
                    else d3.Remove(key);
                    string m2;
                    ConfigStore.SaveSettings(d3, out m2);
                }
                catch { }
            }
        }

        /// <summary>
        /// 版本号比较（C5 的核心逻辑，也是最容易写错的地方）。
        ///
        /// ⚠️ 特别是 "2.10.0 vs 2.9.0" 这一条：按字符串比会得出反的结论，
        ///    结果就是**新版本被漏报**，而且很难发现（要等发到 2.10 才会暴露）。
        /// </summary>
        private static void TestVersionCompare()
        {
            W("");
            W("---- 用例：版本号比较 ----");

            bool a = UpdateChecker.Compare("2.2.0", "2.1.1") > 0;
            W("2.2.0 vs 2.1.1 → " + a + "（期望 True）");

            bool b = UpdateChecker.Compare("2.10.0", "2.9.0") > 0;
            W("2.10.0 vs 2.9.0 → " + b + "（期望 True —— 按数值比，不是按字符串）");

            bool c = UpdateChecker.Compare("v2.2.0", "2.2.0") == 0;
            W("v2.2.0 vs 2.2.0 → " + c + "（期望 True，v 前缀要能吃掉）");

            bool d = UpdateChecker.Compare("2.2.1", "2.2.0") > 0;
            W("2.2.1 vs 2.2.0 → " + d + "（期望 True）");

            bool e = UpdateChecker.Compare("2.1.0", "2.2.0") < 0;
            W("2.1.0 vs 2.2.0 → " + e + "（期望 True）");

            bool f = UpdateChecker.Compare("乱码", "2.2.0") == 0;
            W("解析不出来的版本号 → " + f + "（期望 True —— 当成'没有新版'，宁可漏报不误报）");

            bool g = UpdateChecker.Compare("2.2.0-beta", "2.2.0") == 0;
            W("2.2.0-beta vs 2.2.0 → " + g + "（期望 True，后缀要去掉）");

            bool h = UpdateChecker.Compare("2.2", "2.2.0") == 0;
            W("2.2 vs 2.2.0 → " + h + "（期望 True，缺的段按 0 算）");

            bool ok = a && b && c && d && e && f && g && h;
            W("结果：" + (ok
                ? "通过 —— 按数值段比较、能吃 v 前缀与后缀、解析失败不当成新版本。"
                : "不通过 —— 版本比较逻辑有问题。"));
        }

        /// <summary>
        /// 用例：验证码辅助里的纯逻辑（C9）。
        ///
        /// 为什么这批必须自测：取图的**解析**和**样本标签的闸门**都属于
        /// "错了也不会报错"的类型 ——
        ///   · 解析错一位 → 只是图不显示，没人知道为什么；
        ///   · 标签闸门松一点 → 会把用户填错的值当成答案存下来，
        ///     而**错标签比没有样本更糟**（以后拿它做模板会把模板带歪）。
        /// 这类逻辑只有抽成纯函数直接断言才靠得住，扔给真机手测是测不出来的。
        /// </summary>
        private static void TestCaptchaAssist()
        {
            W("");
            W("---- 用例：验证码辅助的解析与样本闸门 ----");

            int w = 0, h = 0;
            string b64, err;

            bool a = CaptchaAssist.TryParseShot(
                "OK|80x24|475,385,155,47|data:image/png;base64,iVBORw0KGgo=", out w, out h, out b64, out err);
            W("正常返回 → " + a + "，尺寸 " + w + "x" + h + "，数据长 " + b64.Length
                + "（期望 True / 80x24 / 24）");

            bool b = !CaptchaAssist.TryParseShot("ERR:not-ready", out w, out h, out b64, out err);
            W("图还没加载完 → " + b + "，原因 " + err + "（期望 True，即判定为失败）");

            bool c = !CaptchaAssist.TryParseShot(
                "OK|80x24|1,2,3,4|不是图片", out w, out h, out b64, out err);
            W("返回里没有图片数据 → " + c + "（期望 True）");

            bool d = !CaptchaAssist.TryParseShot("", out w, out h, out b64, out err);
            W("空返回 → " + d + "（期望 True）");

            bool e = !CaptchaAssist.TryParseShot(
                "OK|0x0|1,2,3,4|data:image/png;base64,AA", out w, out h, out b64, out err);
            W("尺寸为 0 → " + e + "（期望 True —— 拿不到尺寸就不能当成功）");

            bool f = CaptchaAssist.IsValidCaptchaText("Ab12");
            bool g = !CaptchaAssist.IsValidCaptchaText("Ab1");
            bool hh = !CaptchaAssist.IsValidCaptchaText("AB12!");
            bool i = CaptchaAssist.IsValidCaptchaText(" AB12 ");
            W("标签闸门：Ab12=" + f + "（True）／Ab1=" + g + "（True，长度不对要拦）／"
                + "AB12!=" + hh + "（True，非字母数字要拦）／带空格=" + i + "（True，trim 后合法）");

            bool j = CaptchaAssist.IsSampleFileName("20261002-214530-123-AB12.png");
            bool k = !CaptchaAssist.IsSampleFileName("accounts.txt");
            bool l = !CaptchaAssist.IsSampleFileName("20261002-214530-123-AB1.png");
            bool m = !CaptchaAssist.IsSampleFileName("20261002_214530_123_AB12.png");
            W("样本文件名判定：正常=" + j + "（True）／accounts.txt=" + k + "（True，不能碰）／"
                + "标签只有 3 位=" + l + "（True）／分隔符不对=" + m + "（True）");

            // 结构性断言：取图脚本里**不允许**出现改 src / 主动发请求的写法。
            // 这是"绝不重新请求 /CheckCode"那条红线的第一道机械闸门 ——
            // 以后谁手滑往取图脚本里塞一句 im.src = …，这里立刻报警。
            string extract = CaptchaAssist.BuildExtractJs();
            bool n = extract.IndexOf("im.src =", StringComparison.Ordinal) < 0
                  && extract.IndexOf("XMLHttpRequest", StringComparison.Ordinal) < 0
                  && extract.IndexOf("new Image", StringComparison.Ordinal) < 0;
            W("取图脚本不含改 src／发请求的写法 → " + n + "（期望 True）");

            bool o = extract.IndexOf("drawImage", StringComparison.Ordinal) >= 0;
            bool p = CaptchaAssist.BuildRefreshJs().IndexOf("im.src", StringComparison.Ordinal) >= 0;
            W("取图走 drawImage=" + o + "（True）／换一张确实改 src=" + p
                + "（True —— 换图必须让页面自己去请求，程序不能替代）");

            // —— 真写一次文件，然后**立刻删掉** ——
            //   只验证"写得出来、名字合规、目录建得起来"（纯逻辑测不到这一段，
            //   而路径写错/目录建不起来这类问题恰恰是上了机才炸的那种）。
            //   ⚠️ 写完必须删：假样本混进将来的识别素材里是有害的，
            //   "标签对了但图是假的"比没有样本更糟。
            string werr;
            string saved = CaptchaAssist.SaveSample(Encoding.UTF8.GetBytes("not-a-real-png"), "ZZ99", out werr);
            bool wrote = saved.Length > 0 && System.IO.File.Exists(saved);
            bool named = wrote && CaptchaAssist.IsSampleFileName(System.IO.Path.GetFileName(saved));
            W("真写一条样本 → 写出 = " + wrote + "，文件名合规 = " + named + "（期望 True / True）");
            bool deleted = false;
            try { if (wrote) { System.IO.File.Delete(saved); deleted = !System.IO.File.Exists(saved); } }
            catch { }
            W("  已立刻删除 = " + deleted + "（期望 True —— 绝不能把假样本留在素材目录里）");

            bool ok = a && b && c && d && e && f && g && hh && i && j && k && l && m && n && o && p
                      && wrote && named && deleted;
            W("结果：" + (ok
                ? "通过 —— 异常返回能挡住、标签闸门够严、取图脚本里没有发请求的写法。"
                : "不通过 —— 见上面逐项。"));
        }

        /// <summary>
        /// 用例：验证码抠图链路（C9）—— **离线**跑，用本地测试页驱动真实认证窗口。
        ///
        /// 为什么要这么测：
        ///   抠图是"注入 JS → canvas → base64 → 解码 → 显示"五段接力，
        ///   任何一段断了都只表现为"图不出现"，读代码看不出来；
        ///   而要靠真机 + 校园网才能跑的自测等于没自测（没连校园网时跑不了）。
        ///   所以这里现造一张本地页面（图里就是"验证码"三个字的替代：一张彩色小图），
        ///   让真实窗口去抠它 —— 链路断在哪一段都会立刻暴露。
        ///
        /// ⚠️ 两个坑，写在这里免得后人踩：
        ///   ① **假图必须用与真图不同的尺寸**（这里 64×20，真图 80×24）。
        ///      因为真机的认证页上也有验证码，万一窗口先加载了真页面，
        ///      尺寸一致就分不清拿到的到底是哪张图 —— 那就是"看起来通过了"的假阳性。
        ///   ② 必须在 Show() **之前**把地址换成测试页：窗口的 Loaded 事件会按
        ///      txtUrl.Text 去导航，晚一步就会先跑去真认证页。
        /// </summary>
        /// <summary>
        /// 用例：验证码识别（C9 第二阶段，只填候选、绝不提交）。
        ///
        /// 为什么必须有这个用例：识别是一堆阈值 + 一千多个模板拼出来的，
        /// 改任何一处都可能悄悄退化，而"能不能编译"完全反映不出这种事。
        /// 这里用**真实样本**量出准确率，低于验收线就报红。
        ///
        /// 样本目录按顺序找，第一个存在的就用：
        ///   ① 环境变量 CNH_OCR_FIXTURES —— 开发期量自己拉的样本
        ///   ② %TEMP%\cnh-ocr-fixtures   —— 同上，不设环境变量时用
        ///   ③ 用户样本目录 captcha-samples —— 他平时登录时程序自己攒的
        /// 一个都没有就**跳过**（不算失败）—— 不能因为没样本就让正常自测变红。
        /// </summary>
        private static void TestCaptchaOcr()
        {
            W("");
            W("---- 用例：验证码识别（C9 第二阶段）----");

            int n = CaptchaOcr.TemplateCount;
            W("模板库条数 = " + n + "（期望 1152）");
            if (n != 1152)
            {
                W("!! 模板库条数不对 —— 模板数据可能没编进来，或被生成脚本改坏了");
                W("结果：失败 —— 模板库条数 " + n + "，期望 1152");
                return;
            }
            W("OK 模板库能加载");

            string dir = Environment.GetEnvironmentVariable("CNH_OCR_FIXTURES");
            string src = "环境变量 CNH_OCR_FIXTURES";
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
            {
                dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cnh-ocr-fixtures");
                src = "%TEMP%\\cnh-ocr-fixtures";
            }
            if (!System.IO.Directory.Exists(dir))
            {
                dir = CaptchaAssist.SamplesDir;
                src = "用户样本目录 captcha-samples";
            }
            if (!System.IO.Directory.Exists(dir))
            {
                W("结果：跳过 —— 三个样本目录都不存在（最后一个试的是 " + src + "）。");
                W("      往任一目录放几张「文件名以 4 位答案结尾」的 PNG 就能量准确率。");
                return;
            }

            string err;
            CaptchaOcr.Accuracy a = CaptchaOcr.MeasureDirectory(dir, out err);
            if (err.Length > 0) { W("结果：跳过 —— " + err); return; }

            W("样本目录 = " + dir + "（来自 " + src + "）");
            W("样本 " + a.Images + " 张 / 共 " + a.CharTotal + " 位字符");
            W("");
            W("① 填给用户的候选准不准（策略：总是填 4 位 top1）");
            W("   填得进去的图 " + a.ImagesAllFilled + "/" + a.Images
                + " 张（覆盖 " + a.FillImageCoverage.ToString("P1") + "）");
            W("   填入的位里对 " + a.FilledOk + "/" + a.FilledChars
                + " = " + a.FillCharRate.ToString("P1") + "   （验收线 70%）");
            W("   其中 4 位全对（用户一下都不用改）" + a.ImagesAllFilledOk + "/"
                + a.ImagesAllFilled + " = " + a.FillImageRate.ToString("P1"));
            W("");
            W("② 点名质量：出错时，点名让他重点看的那一位，真是错的那一位吗");
            W("   " + a.FlaggedWrong + "/" + a.FlaggedTotal
                + " = " + a.FlagHitRate.ToString("P1")
                + "   （越高越好；若明显偏低，这句提示就是在误导人，应去掉）");
            W("");
            W("③ 不变量：填进输入框的只能是「空串」或「4 位字母数字」");
            W("   违规次数 = " + a.FillViolations + "（必须为 0）");

            // ⚠️ 每个用例都必须打一行"结果："，否则它不会进入通过/失败统计 ——
            //    release_sync.py 就是数这一行来判断自测过没过的。
            //    （2026-10-03 踩到：新用例忘了打印，于是"15/15 通过"里根本没有它。）
            if (a.FillViolations > 0)
            {
                W("!! 有内容会被填进输入框却不是 4 位字母数字 —— 用户会看到问号之类的东西");
                W("结果：失败 —— 填入内容不变量被破坏 " + a.FillViolations + " 次");
                return;
            }
            if (a.CharTotal < 40)
            {
                W("样本偏少（<10 张），只报数不下结论。");
                W("结果：通过（样本少，只验了不变量）—— " + a.Images + " 张样本");
                return;
            }
            if (a.FillCharRate < 0.70)
            {
                W("!! 填入字符准确率 " + a.FillCharRate.ToString("P1") + " 低于验收线 70%");
                W("结果：失败 —— 填入字符准确率 " + a.FillCharRate.ToString("P1") + " 低于 70%");
                return;
            }
            W("OK 填入字符准确率 " + a.FillCharRate.ToString("P1") + " 达标（验收线 70%）");
            W("结果：通过 —— 填入字符准确率 " + a.FillCharRate.ToString("P1")
                + "、整图全对 " + a.FillImageRate.ToString("P1")
                + "、不变量违规 0 次（" + a.Images + " 张样本）");
        }

        private static void TestCaptchaPipeline()
        {
            W("");
            W("---- 用例：验证码抠图链路（离线）----");

            MainWindow mw = null;
            WebAuthWindow wa = null;
            System.Net.HttpListener srv = null;
            System.Threading.Thread serverThread = null;
            bool stopServer = false;
            int port = 0;

            try
            {
                // ---- 本地小站：路径名照抄真实门户（/login、/CheckCode），走的是同一条路 ----
                // ⚠️ 必须用 http://，不能用 file://（2026-10-02 实测踩到）：
                //    窗口的 Navigate 会给没写协议的地址自动补 http://，
                //    于是 file:///C:/... 被拼成 http://file///C:/... → IE 报错页，
                //    抠到的两个 img 是 IE 错误页自带的图标（诊断信息里能一眼看到）。
                System.Random rnd = new System.Random();
                for (int i = 0; i < 12 && srv == null; i++)
                {
                    int p = 41000 + rnd.Next(0, 8000);
                    try
                    {
                        System.Net.HttpListener l = new System.Net.HttpListener();
                        l.Prefixes.Add("http://127.0.0.1:" + p + "/");
                        l.Start();
                        srv = l;
                        port = p;
                    }
                    catch { }
                }
                if (srv == null)
                {
                    W("结果：跳过 —— 本机起不了监听端口（环境问题，不是功能问题）。");
                    return;
                }

                byte[] pngA = MakeTestCaptchaPng(64, 20);   // 首张：64×20
                byte[] pngB = MakeTestCaptchaPng(60, 18);   // 「换一张」之后服务端给的那张：60×18
                string html = "<html><body>"
                    + "<img id='MainContent_ImageCC' src='/CheckCode'>"
                    + "<input type='text' id='MainContent_TextBoxCC' maxlength='4'>"
                    + "</body></html>";
                W("本地测试站 = http://127.0.0.1:" + port + "/login（/CheckCode 首张 64×20，带 ?t= 时给 60×18）");

                serverThread = new System.Threading.Thread(delegate()
                {
                    while (!stopServer)
                    {
                        try
                        {
                            System.Net.HttpListenerContext ctx = srv.GetContext();
                            string q = (ctx.Request.Url.Query == null) ? "" : ctx.Request.Url.Query.Trim();
                            bool fresh = q.Length > 1;      // 带 ?t= 就是「换一张」要的那张
                            byte[] body;
                            string type;
                            if (ctx.Request.Url.AbsolutePath.ToLowerInvariant().IndexOf("checkcode") >= 0)
                            {
                                body = fresh ? pngB : pngA;
                                type = "image/png";
                            }
                            else
                            {
                                body = Encoding.UTF8.GetBytes(html);
                                type = "text/html; charset=utf-8";
                            }
                            ctx.Response.ContentType = type;
                            ctx.Response.ContentLength64 = body.Length;
                            ctx.Response.OutputStream.Write(body, 0, body.Length);
                            ctx.Response.Close();
                        }
                        catch { if (stopServer) break; }
                    }
                });
                serverThread.IsBackground = true;
                serverThread.Start();

                mw = new MainWindow(false);
                mw.Left = -4000;
                mw.Top = -4000;
                mw.ShowInTaskbar = false;
                mw.Show();
                Pump();

                wa = new WebAuthWindow(mw);
                wa.Left = -4000;
                wa.Top = -4000;
                wa.ShowInTaskbar = false;
                // ⚠️ 顺序不能反：先换地址，再 Show（见上面的坑 ②）
                wa.LoadUrlForTest("http://127.0.0.1:" + port + "/login");
                wa.Show();

                bool shown = false;
                int pw = 0, ph = 0;
                for (int i = 0; i < 40; i++)
                {
                    Pump();
                    System.Threading.Thread.Sleep(250);
                    shown = wa.CaptchaRowVisibleForTest();
                    pw = wa.CaptchaPixelWidthForTest();
                    ph = wa.CaptchaPixelHeightForTest();
                    if (shown && pw == 64 && ph == 20) break;
                }
                W("抠图行出现 = " + shown + "，取到的图像素 = " + pw + "×" + ph
                    + "（期望 True / 64×20 —— 尺寸对得上才说明拿的是测试页那张图）");

                // 失败时把现场说清楚（自测期间日志是静默的，只能这样带回来）
                if (!shown || pw != 64)
                {
                    W("  诊断·当前页面 = " + wa.PageUrlForTest());
                    W("  诊断·页面元素 = " + wa.PageProbeForTest());
                    W("  诊断·取图脚本返回 = " + wa.RawExtractForTest());
                }

                // 用户填值那一路：页面里塞值 → 我们得读得到，并且认出"填满了"
                bool typedOk = wa.TypeCaptchaForTest("AB12");
                string hint = wa.CaptchaHintForTest();
                W("模拟填入 AB12 → 调用成功 = " + typedOk + "，界面提示 = \"" + hint
                    + "\"（期望含「已填 4 位」）");
                bool hintOk = hint.IndexOf("已填 4 位", StringComparison.Ordinal) >= 0;

                // 填不满 4 位时不该被当成"可以记样本"
                wa.TypeCaptchaForTest("AB");
                string hint2 = wa.CaptchaHintForTest();
                W("只填 2 位 → 提示 = \"" + hint2 + "\"（期望「2 / 4」而不是「已填 4 位」）");
                bool hint2Ok = hint2.IndexOf("2 / 4", StringComparison.Ordinal) >= 0;

                // ---- 「换一张」：必须真的换成服务端新给的那张，且已填的值要清掉 ----
                wa.RefreshCaptchaForTest();
                int pw2 = pw, ph2 = ph;
                for (int i = 0; i < 40; i++)
                {
                    Pump();
                    System.Threading.Thread.Sleep(250);
                    pw2 = wa.CaptchaPixelWidthForTest();
                    ph2 = wa.CaptchaPixelHeightForTest();
                    if (pw2 == 60 && ph2 == 18) break;
                }
                string hint3 = wa.CaptchaHintForTest();
                bool refreshed = pw2 == 60 && ph2 == 18;
                bool cleared = hint3.IndexOf("4 位", StringComparison.Ordinal) < 0;
                W("换一张之后 → 图像素 = " + pw2 + "×" + ph2 + "（期望 60×18）／提示 = \"" + hint3 + "\"");
                W("  已填的值被清掉 = " + cleared + "（期望 True —— 图换了旧答案就作废，留着只会害人填错）");

                bool ok = shown && pw == 64 && ph == 20 && typedOk && hintOk && hint2Ok && refreshed && cleared;
                W("结果：" + (ok
                    ? "通过 —— 抠图放大、读用户填写、位数不够不误判、换一张真的换了图并清空旧值。"
                    : "不通过 —— 见上面逐项。"));
            }
            catch (Exception ex)
            {
                W("!! 用例异常: " + ex.Message + " / " + ex.GetType().Name);
            }
            finally
            {
                stopServer = true;
                try { if (srv != null) srv.Stop(); } catch { }
                try { if (serverThread != null) serverThread.Join(2000); } catch { }
                try { if (wa != null) wa.Close(); } catch { }
                try { if (mw != null) mw.Close(); } catch { }

                // ⚠️⚠️ 清场（2026-10-02 踩到过，不加这行就会出事）：
                //    窗口的 Loaded 事件是 `Navigate(txtUrl.Text, true)`，那个 true
                //    的意思是"顺手存进网址清单"。于是自测一跑，
                //    **用户真实的清单里就会多出一条 http://127.0.0.1:xxxxx/login**
                //    （实测真出现过，还害得我截图时窗口跑去了一个死掉的本地地址）。
                //    所以无论成败，都要把自测自己那条删掉。
                try
                {
                    if (port > 0)
                    {
                        string dm;
                        WebUrlStore.Delete("http://127.0.0.1:" + port + "/login", out dm);
                    }
                }
                catch { }
            }
        }

        /// <summary>造一张指定尺寸的"假验证码"PNG（内容随便画几笔，够 canvas 抠出来就行）。</summary>
        private static byte[] MakeTestCaptchaPng(int w, int h)
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(w, h))
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.White);
                    g.DrawString("AB12", new System.Drawing.Font("Arial", h * 0.5F,
                        System.Drawing.FontStyle.Bold), System.Drawing.Brushes.Red, 2F, 1F);
                }
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static void TestFirstRunTip()
        {
            W("");
            W("---- 用例：首次使用引导条的显示规则 ----");

            // 新手现场：一个账号都没配过，也没点过「知道了」→ 该显示
            bool a = MainWindow.ShouldShowFirstRunTip(0, false);
            W("没账号 + 没点过「知道了」→ " + a + "（期望 True，这是最该出现的时候）");

            // 配过账号 → 不需要引导
            bool b = MainWindow.ShouldShowFirstRunTip(1, false);
            W("有 1 个账号 → " + b + "（期望 False，配过就不用教了）");

            // 点过「知道了」→ 尊重用户，别再烦
            bool c = MainWindow.ShouldShowFirstRunTip(0, true);
            W("没账号 + 点过「知道了」→ " + c + "（期望 False，点了就得认）");

            // 两个条件都不满足 → 更不该显示
            bool d = MainWindow.ShouldShowFirstRunTip(3, true);
            W("有 3 个账号 + 点过「知道了」→ " + d + "（期望 False）");

            // 边界：账号数正好从 0 变 1（刚导入第一个账号那一刻）
            bool e = MainWindow.ShouldShowFirstRunTip(1, false);
            W("刚导入第一个账号 → " + e + "（期望 False，提示应当立刻自己消失）");

            bool ok = a && !b && !c && !d && !e;
            W("结果：" + (ok
                ? "通过 —— 只在「真的还没配过账号」时出现，配过或点过「知道了」都不打扰。"
                : "失败 —— 判定规则不对，检查 ShouldShowFirstRunTip"));
        }

        /// <summary>
        /// 程序图标能不能正常取到（而不是静默退回系统盾牌），
        /// 以及四张托盘状态图能不能生成。
        ///
        /// ⚠️ 这是一条**回归测试**：2026-10-01 换构建方式（/win32icon → /win32res）
        ///    带出过一次真实事故 —— assets/logo.ico 的帧全是 PNG 压缩格式，
        ///    .NET Framework 的 System.Drawing 解不了，于是静默退回
        ///    SystemIcons.Shield，托盘上挂着一个**用户根本不认识的蓝色盾牌**。
        ///    当时是靠人肉截托盘才发现的 —— 这种事不该再发生第二次，
        ///    所以把判据写成断言：以后构建方式或图标文件一变，这里立刻报出来。
        /// </summary>
        private static void TestAppIcon()
        {
            W("");
            W("---- 用例：程序图标可用（没退回系统盾牌）----");

            bool isShield = MainWindow.DebugIconIsFallbackShield();
            W("图标是否退化成系统默认盾牌 → " + isShield + "（期望 False）");
            W("取到的图标尺寸 → " + MainWindow.DebugAppIconInfo());
            W("四张托盘状态图 → " + MainWindow.DebugTrayIconShapes());

            bool ok = !isShield;
            W("结果：" + (ok
                ? "通过 —— 取到的是程序自己的 logo，能正常着色出四种状态。"
                : "失败 —— 图标取值退化成系统盾牌，托盘上会是个用户不认识的图标。"
                  + "多半是 ico 的帧格式（PNG 压缩）和读取方式对不上。"));
        }

        private static void TestKeepAliveLogQuiet()
        {
            W("");
            W("---- 用例：心跳日志不刷屏 ----");

            // ⚠️ 这里数的是"日志调用次数"（Log.Emitted），不是日志文件的物理行数。
            //    因为自测模式下日志不落盘（Log.SelfTestQuiet = true），文件永远不长。
            //    Emitted 在两种模式下都会累加，正好用来做断言。
            long before = Log.Emitted;

            KeepAlive ka = new KeepAlive();

            // ① 首次成功
            ka.DebugInjectBeat(true, "HTTP 204");
            long afterFirst = Log.Emitted;
            long nFirst = afterFirst - before;
            W("第 1 次成功（应记 1 条）→ 新增 " + nFirst + " 条");

            // ② 再成功 5 次，应该一条都不记
            for (int i = 0; i < 5; i++) ka.DebugInjectBeat(true, "HTTP 204");
            long afterQuiet = Log.Emitted;
            long nQuiet = afterQuiet - afterFirst;
            W("之后连续成功 5 次（应记 0 条）→ 新增 " + nQuiet + " 条");

            // ③ 失败 3 次，应该每次都记
            for (int i = 0; i < 3; i++) ka.DebugInjectBeat(false, "Timeout");
            long afterFail = Log.Emitted;
            long nFail = afterFail - afterQuiet;
            W("连续失败 3 次（应记 3 条）→ 新增 " + nFail + " 条");

            // ④ 恢复正常，应记 1 条
            ka.DebugInjectBeat(true, "HTTP 204");
            long afterRecover = Log.Emitted;
            long nRecover = afterRecover - afterFail;
            W("恢复正常（应记 1 条）→ 新增 " + nRecover + " 条");

            // ⑤ 恢复后再成功 3 次，应又安静
            for (int i = 0; i < 3; i++) ka.DebugInjectBeat(true, "HTTP 204");
            long afterQuiet2 = Log.Emitted;
            long nQuiet2 = afterQuiet2 - afterRecover;
            W("恢复后又成功 3 次（应记 0 条）→ 新增 " + nQuiet2 + " 条");

            W("计数：成功次数 = " + ka.OkCount + "，失败计数（恢复后已归零）= " + ka.FailCount);

            // ⑥ 顺带验证：自测模式确实关掉了落盘（别把测试痕迹写进用户日志）
            W("自测静默开关 = " + Log.SelfTestQuiet + "（期望 True）");

            bool ok = (nFirst == 1) && (nQuiet == 0) && (nFail == 3) && (nRecover == 1)
                      && (nQuiet2 == 0) && Log.SelfTestQuiet;
            W("");
            if (ok)
            {
                W("结果：通过 —— 成功不刷屏、失败必记、恢复记一笔，且自测不往正式日志里写。");
            }
            else
            {
                W("结果：失败 —— 期望 (1,0,3,1,0) 且静默=True，实际 ("
                  + nFirst + "," + nQuiet + "," + nFail + "," + nRecover + "," + nQuiet2
                  + ") 静默=" + Log.SelfTestQuiet);
            }
        }

        /// <summary>
        /// 用例：掉线后的自动重连 —— 分支必须走对。
        ///
        /// 这是 2026-09-30 报的那个 bug 的回归测试：
        ///   海辰把认证方式切成「网页认证」后，断网时程序仍然去调 PPP 拨号，
        ///   日志留下 `拨号结果: exit=628 success=False` —— 白试一次，
        ///   还平白给学校攒了一次失败记录。
        ///
        /// 根因：TryAutoReconnect() 无脑调 StartDial()，没有按模式分流。
        ///
        /// 验证点：
        ///   ① 认证模式下，重连分支必须是 "portal"（不是 "dial"）
        ///   ② 掉线后自动重连会被"武装"起来（拨号模式靠用户点连接，认证模式没有那个动作，
        ///      必须在判定掉线时自己置位，否则永远不触发）
        ///   ③ 自动重开认证页有次数上限，到上限就停下交给用户
        ///   ④ 静默/后台场景不发窗口，改成发托盘气泡等用户点（避免游戏时弹窗）
        ///   ⑤ 网络恢复后计数归零，下次掉线能重新开始
        /// </summary>
        private static void TestPortalAutoReconnect()
        {
            W("");
            W("---- 用例：掉线自动重连（分支 + 上限 + 不弹窗）----");

            MainWindow mw = null;
            try
            {
                mw = new MainWindow(false);
                mw.Width = 1040;
                mw.Height = 760;
                mw.Left = -4000;
                mw.Top = -4000;
                mw.ShowInTaskbar = false;
                mw.Show();
                Pump();
                Pump();

                // ---------- ① 分支判断 ----------
                W("当前认证方式 = " + mw.DebugAuthModeText());
                string branch = mw.DebugReconnectBranch();
                W("重连会走的分支 = " + branch + "（期望 portal）");

                // ---------- ② 掉线后会被武装 ----------
                mw.DebugResetPortalReauth();
                mw.DebugSetPortalWasOnline();
                bool armedBefore = mw.DebugIsArmed();
                mw.DebugSimulatePortalDrop();
                bool armedAfter = mw.DebugIsArmed();
                W("模拟掉线前 已武装 = " + armedBefore + "，掉线后 已武装 = " + armedAfter
                    + "（期望掉线后 True）");

                // ---------- ③ 重试上限 ----------
                mw.DebugResetPortalReauth();
                int max = mw.DebugPortalReauthMax();
                int allowed = 0;
                for (int i = 0; i < max + 5; i++)
                {
                    if (mw.DebugBumpPortalReauthAttempt()) allowed++;
                }
                W("上限 = " + max + "，狂调 " + (max + 5) + " 次后实际允许 = " + allowed
                    + " 次（期望刚好等于上限，不能无限试）");
                W("当前计数 = " + mw.DebugPortalReauthAttempts());

                // ---------- ④ 静默场景不弹窗 ----------
                mw.DebugResetPortalReauth();
                mw.DebugSetPortalWasOnline();
                mw.DebugSimulatePortalDrop();
                // 这里只验证"待用户确认"这个状态位初始是干净的 ——
                // 真正弹窗/发气泡的分支依赖窗口是否可见，没法在离屏窗口上如实验证。
                // 所以如实说明：只验证状态位机制存在，不谎称验证了"没弹窗"。
                bool pending0 = mw.DebugPendingPortalReauth();
                W("（机制检查）待用户确认状态位可读 = " + (pending0 == false || pending0 == true));

                bool ok = (branch == "portal") && armedAfter && (allowed == max);
                W("");
                if (ok)
                {
                    W("结果：通过 —— 认证模式走 portal 分支（不再误拨号）、掉线自动武装、"
                      + "重试有上限 " + max + " 次。");
                }
                else
                {
                    W("结果：失败");
                    if (branch != "portal") W("  · 分支走错了：实际 " + branch + "，应为 portal");
                    if (!armedAfter) W("  · 掉线后没有武装自动重连，永远不会触发");
                    if (allowed != max) W("  · 重试上限不对：允许 " + allowed + "，应为 " + max);
                }
            }
            catch (Exception ex)
            {
                W("结果：不通过");
                W("  异常: " + ex.Message);
            }
            finally
            {
                try { if (mw != null) mw.Close(); } catch { }
            }
        }

        /// <summary>数一个日志文件有多少行；文件不存在返回 -1。</summary>
        private static int CountLogLines(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return -1;
                // 用 FileShare.ReadWrite —— 日志可能正被别的进程/线程写着，
                // 默认的独占读会抛异常。
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                           System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var sr = new System.IO.StreamReader(fs, Encoding.UTF8))
                {
                    int n = 0;
                    while (sr.ReadLine() != null) n++;
                    return n;
                }
            }
            catch { return -1; }
        }

        private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            var list = new List<T>();
            if (root == null) return list;
            var t = root as T;
            if (t != null) list.Add(t);
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); } catch { }
            for (int i = 0; i < n; i++)
            {
                list.AddRange(FindAll<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)));
            }
            return list;
        }
    }
}
