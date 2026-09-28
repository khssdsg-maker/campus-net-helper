using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using Microsoft.Win32;

// 内嵌浏览器的 DOM 类型在 System.Windows.Forms 里，但那个命名空间还带着
// CheckBox / TextBox / Button / MessageBox 等等 —— 直接 using 会和 WPF 的那批撞名。
// 所以起个别名，只按需引用。
using WinForms = System.Windows.Forms;

namespace CampusNetHelper
{
    /// <summary>
    /// 校园网网页认证窗口。
    ///
    /// 干什么：
    ///   有些学校的校园网不是 PPPoE 拨号，而是打开一个网页、填账号密码点登录。
    ///   这个窗口把这个网页内嵌进来，并**自动把账号密码填好、点掉登录按钮**，
    ///   让"每次开机都要手动登一次"变成一次点击。
    ///
    /// 设计上的取舍（重要）：
    ///   为了保住"绿色单文件"这个核心定位，这里**只能用系统自带的 IE 内核**
    ///   （System.Windows.Forms.WebBrowser + WindowsFormsHost）。
    ///   用 WebView2 效果会好很多，但要额外带 2~3 个 DLL、还可能要求用户先装运行时，
    ///   那就不是"解压双击就能用"了。
    ///
    ///   代价：老式表单页（jQuery 那一代）没问题，现代前端框架（Vue / React）写的
    ///   认证页在 IE 内核下会白屏。遇到这种情况用窗口里的「用系统浏览器打开」兜底。
    ///
    /// 填表逻辑是**通用启发式**，不针对任何一所学校：
    ///   第一个 type=password 的输入框 = 密码框；
    ///   它前面最近的文本输入框（或 name/id 含 user/account/name 的）= 账号框；
    ///   再找提交控件（type=submit / 文字含"登录 / 认证 / 连接"）。
    ///   所以换了学校也不用改代码 —— 但页面结构太特别时可能找不到，那就手动登。
    /// </summary>
    public class WebAuthWindow : Window
    {
        private readonly MainWindow owner;

        private System.Windows.Forms.WebBrowser browser;
        private WindowsFormsHost host;

        private TextBox txtUrl;
        private ComboBox cmbAccount;
        private PasswordBox txtPass;
        private CheckBox chkAuto;
        private Button btnLogin;
        private TextBlock lblStatus;
        private TextBlock lblFound;

        private bool _autoDoneThisLoad = false;
        private int _loadToken = 0;
        private DispatcherTimer _retryTimer;
        private int _retryLeft = 0;

        /// <summary>下拉框里放的是账号名（字符串），这里按同一下标反查完整的账号对象。</summary>
        private List<ConfigStore.Account> _accounts = new List<ConfigStore.Account>();

        public WebAuthWindow(MainWindow owner)
        {
            this.owner = owner;

            Title = "校园网网页认证";
            Width = 900;
            Height = 740;
            MinWidth = 720;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            EnableIe11Emulation();

            BuildUi();
            LoadFromOwner();

            Loaded += (s, e) => { Navigate(txtUrl.Text, true); };
            Closed += delegate(object s, EventArgs e)
            {
                try
                {
                    if (browser != null)
                    {
                        browser.Stop();
                        browser.Dispose();
                    }
                }
                catch { }
            };
        }

        // ==================================================================
        // 让内嵌浏览器用 IE11 内核（默认是 IE7，绝大多数认证页会错版）
        // ==================================================================

        /// <summary>
        /// 往**用户自己的**注册表项里写一条，只为把本程序的浏览器内核登记成 IE11。
        ///
        /// 为什么必须做：.NET 的 WebBrowser 控件在宿主程序未登记时默认按 IE7 渲染，
        /// 绝大多数学校的认证页在 IE7 下直接错版或白屏。
        ///
        /// ⚠️ 值名必须是**当前进程真实的 exe 文件名**：
        /// 分发出去的版本叫「校园网助手.exe」，开发时叫 CampusNetHelper.exe，
        /// 名字对不上这条设置完全不生效，而且不会报任何错 —— 极难排查。
        ///
        /// 只写 HKCU（当前用户），不碰系统级设置，卸载程序后无残留影响。
        /// </summary>
        private static void EnableIe11Emulation()
        {
            try
            {
                string exeName = Process.GetCurrentProcess().MainModule.ModuleName;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    if (k == null) return;
                    object cur = k.GetValue(exeName);
                    if (cur == null || Convert.ToInt32(cur) != 11001)
                    {
                        k.SetValue(exeName, 11001, RegistryValueKind.DWord);
                        Log.Info("已登记内嵌浏览器使用 IE11 内核: " + exeName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("登记 IE11 内核失败（认证页可能显示错版）: " + ex.Message);
            }
        }

        // ==================================================================
        // 界面
        // ==================================================================

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(16, 14, 16, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---------- 第 1 行：认证网址 ----------
            var rowUrl = new StackPanel { Orientation = Orientation.Horizontal };
            rowUrl.Children.Add(MakeLabel("认证网址"));
            txtUrl = new TextBox
            {
                Width = 580,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
            rowUrl.Children.Add(txtUrl);

            var btnOpen = MainWindow.MakeGhostButton("打开", delegate() { Navigate(txtUrl.Text, true); });
            btnOpen.FontSize = 12;
            rowUrl.Children.Add(btnOpen);

            var btnSys = MainWindow.MakeGhostButton("用系统浏览器打开", delegate()
            {
                OpenInSystemBrowser(txtUrl.Text);
            });
            btnSys.FontSize = 12;
            btnSys.Margin = new Thickness(8, 0, 0, 0);
            rowUrl.Children.Add(btnSys);

            Grid.SetRow(rowUrl, 0);
            root.Children.Add(rowUrl);

            // ---------- 第 2 行：账号 / 密码 ----------
            var rowAcc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            rowAcc.Children.Add(MakeLabel("账号"));
            // ⚠️ 不能设 IsEditable = true —— MainWindow.MakeDarkCombo 的自定义模板里
            // 没有 PART_EditableTextBox，可编辑模式下会渲染成一片空白（选中了也看不见）。
            cmbAccount = MainWindow.MakeDarkCombo(200);
            cmbAccount.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                FillPassFromSelected();
            };
            rowAcc.Children.Add(cmbAccount);

            var lblPw = MakeLabel("密码");
            lblPw.Margin = new Thickness(16, 0, 0, 0);
            rowAcc.Children.Add(lblPw);

            txtPass = new PasswordBox
            {
                Width = 180,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12
            };
            rowAcc.Children.Add(txtPass);

            chkAuto = new CheckBox
            {
                Content = "打开后自动填表并提交",
                IsChecked = true,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12
            };
            rowAcc.Children.Add(chkAuto);

            btnLogin = MainWindow.MakePrimaryButton("填表并登录", OnLoginClick);
            btnLogin.FontSize = 12;
            btnLogin.Margin = new Thickness(16, 0, 0, 0);
            rowAcc.Children.Add(btnLogin);

            Grid.SetRow(rowAcc, 1);
            root.Children.Add(rowAcc);

            // ---------- 第 3 行：状态 ----------
            var statusBox = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            lblFound = MakeHint("");
            lblStatus = MakeHint("");
            statusBox.Children.Add(lblFound);
            statusBox.Children.Add(lblStatus);
            Grid.SetRow(statusBox, 2);
            root.Children.Add(statusBox);

            // ---------- 第 4 行：浏览器 ----------
            browser = new System.Windows.Forms.WebBrowser();
            browser.ScriptErrorsSuppressed = true;
            browser.DocumentCompleted += OnDocumentCompleted;
            browser.NewWindow += (s, e) =>
            {
                // 有些学校的"登录"按钮是 target="_blank"，会要求弹新窗口。
                // WebBrowser 的 NewWindow 事件参数里**拿不到目标地址**（只有 Cancel），
                // 所以只能拦下来 + 提示用户，不能自己把这个地址接住继续导航。
                e.Cancel = true;
                SetStatus("页面想弹出一个新窗口，已经拦下了。如果登录按钮点不动，"
                    + "点上面的「用系统浏览器打开」，在浏览器里登录一次也一样。");
            };

            host = new WindowsFormsHost { Child = browser };
            var frame = new Border
            {
                Background = new SolidColorBrush(Theme.FieldBg),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Child = host
            };
            Grid.SetRow(frame, 3);
            root.Children.Add(frame);

            Content = root;
        }

        private static TextBlock MakeLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        private static TextBlock MakeHint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };
        }

        // ==================================================================
        // 配置
        // ==================================================================

        private void LoadFromOwner()
        {
            string url = owner.WebAuthUrl();
            txtUrl.Text = url;

            // 下拉框里塞的是**账号名**，不是 Account 对象 ——
            // MainWindow.MakeDarkCombo 没有配数据显示模板，直接塞对象会显示成类名
            // （"CampusNetHelper.ConfigStore+Account"）。主窗口的账号下拉框也是这么处理的。
            _accounts = owner.AccountsSnapshot();
            foreach (ConfigStore.Account a in _accounts) cmbAccount.Items.Add(a.Name);

            if (_accounts.Count > 0)
            {
                cmbAccount.SelectedIndex = 0;
                FillPassFromSelected();
            }

            if (_accounts.Count == 0)
            {
                SetStatus("还没有保存过账号 —— 先关掉这个窗口，在主界面点「管理账号」加一个，再回来登录。"
                    + "认证网址填在上面，填好后会自动记住。");
            }
            else if (string.IsNullOrEmpty(url))
            {
                SetStatus("第一次用：把你们学校的认证网页地址填到上面，点「打开」。"
                    + "地址填好后会自动记住，下次打开直接就能用。");
            }
        }

        private ConfigStore.Account SelectedAccount()
        {
            int i = cmbAccount.SelectedIndex;
            if (i < 0 || i >= _accounts.Count) return null;
            return _accounts[i];
        }

        private void FillPassFromSelected()
        {
            ConfigStore.Account a = SelectedAccount();
            if (a == null) return;
            if (!string.IsNullOrEmpty(a.Password)) txtPass.Password = a.Password;
        }

        private void SetStatus(string text)
        {
            if (lblStatus != null) lblStatus.Text = text;
        }

        private static void OpenInSystemBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            try { Process.Start(url); }
            catch (Exception ex) { Log.Warn("打开系统浏览器失败: " + ex.Message); }
        }

        // ==================================================================
        // 导航与填表
        // ==================================================================

        private void Navigate(string rawUrl, bool saveIt)
        {
            string url = (rawUrl ?? "").Trim();
            if (url.Length == 0)
            {
                SetStatus("请先填认证网址。");
                return;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
                txtUrl.Text = url;
            }

            if (saveIt) owner.SaveWebAuthUrl(url);

            _autoDoneThisLoad = false;
            _loadToken++;
            SetStatus("正在打开认证页…");
            if (lblFound != null) lblFound.Text = "";
            try { browser.Navigate(url); }
            catch (Exception ex)
            {
                SetStatus("打开失败：" + ex.Message);
                Log.Warn("认证页导航失败: " + ex.Message);
            }
            StartRetryWindow();
        }

        /// <summary>
        /// 补几次填表机会。
        ///
        /// 为什么需要：登录框不一定是 HTML 里写死的 ——
        /// 有些页面是等 JS / AJAX 跑完才把它插进 DOM，
        /// 那时候 DocumentCompleted 早就触发过了，一次性尝试必然落空。
        /// </summary>
        private void StartRetryWindow()
        {
            if (_retryTimer == null)
            {
                _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _retryTimer.Tick += delegate(object s, EventArgs e)
                {
                    if (_autoDoneThisLoad || _retryLeft <= 0)
                    {
                        _retryTimer.Stop();
                        return;
                    }
                    _retryLeft--;
                    if (chkAuto == null || chkAuto.IsChecked != true) return;
                    if (TryAutoFillAndSubmit(true)) _autoDoneThisLoad = true;
                };
            }
            _retryLeft = 6;
            _retryTimer.Start();
        }

        private void OnDocumentCompleted(object sender, System.Windows.Forms.WebBrowserDocumentCompletedEventArgs e)
        {
            // DocumentCompleted 每个 iframe 都会触发一次，只认主文档完成那一次
            if (browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete) return;
            if (e.Url == null) return;

            // ⚠️ 浏览器控件初始化时会先加载 about:blank，那也会触发一次 DocumentCompleted。
            // 如果在那一次就把"本轮已处理"标志置上，真正的认证页反而会被跳过 ——
            // 之前的版本就是这么失败的（页面上明明有密码框，却报"没找到"）。
            if (e.Url.ToString().StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

            if (_autoDoneThisLoad) return;

            if (chkAuto == null || chkAuto.IsChecked != true)
            {
                SetStatus("页面已加载。点「填表并登录」自动填账号密码。");
                return;
            }

            // 只有**真的动过手**才算处理完；没找到框就留给重试，别把机会耗掉
            if (TryAutoFillAndSubmit(true)) _autoDoneThisLoad = true;
        }

        private void OnLoginClick()
        {
            if (TryAutoFillAndSubmit(false)) _autoDoneThisLoad = true;
        }

        /// <summary>
        /// 通用启发式填表：不针对任何一所学校，靠页面结构推断。
        /// 找不到就明确告诉用户，不做危险操作。
        /// 返回 true = 这次真的找到了密码框并处理过（不管成没成功提交）。
        /// </summary>
        private bool TryAutoFillAndSubmit(bool auto)
        {
            string user = GetUser();
            string pass = txtPass.Password;

            if (user.Length == 0 || pass.Length == 0)
            {
                if (!auto) SetStatus("账号或密码是空的 —— 先回主界面「管理账号」里加一个账号，再来这里登录。");
                return false;
            }

            List<WinForms.HtmlDocument> docs = CollectDocuments();
            if (docs.Count == 0)
            {
                if (!auto) SetStatus("页面还没加载好，稍等一下再点。");
                return false;
            }
            Log.Info("认证页填表：可访问文档 " + docs.Count + " 个");

            foreach (WinForms.HtmlDocument doc in docs)
            {
                // ⚠️ 只枚举一次 DOM。WinForms.HtmlElement 是 COM 包装对象，每枚举一次都会新建一批，
                // 分两次枚举拿到的对象用 == 比较必然不等 —— 靠下标去定位账号框会失效。
                List<WinForms.HtmlElement> all = All(doc);

                WinForms.HtmlElement pw = FindPasswordInput(all);
                if (pw == null)
                {
                    // 记下来 —— 用户报"填不上"时，这条日志能直接分清是
                    // "页面还没渲染" / "结构不认识" / "在跨域 iframe 里"
                    Log.Info("认证页填表：这个文档里没有密码框 —— " + DescribeDoc(all));
                    continue;
                }

                WinForms.HtmlElement us = FindUserInput(all, pw);
                if (us == null)
                {
                    SetStatus("找到了密码框，但认不出账号框（这个页面的结构比较特别）。请在页面上手动填一次。");
                    return true;
                }

                SetValue(us, user);
                SetValue(pw, pass);

                WinForms.HtmlElement submit = FindSubmit(all);
                if (submit == null)
                {
                    SetStatus("账号密码已填好，但没找到「登录」按钮 —— 请手动点一下页面上的登录。");
                    return true;
                }

                SetStatus("已识别登录框，正在提交…");
                if (lblFound != null)
                {
                    lblFound.Text = "识别结果：账号框「" + Describe(us) + "」 密码框「" + Describe(pw)
                        + "」 提交按钮「" + Describe(submit) + "」";
                }

                ClickElement(submit);
                AfterSubmit();
                return true;
            }

            SetStatus("这个页面里没找到密码输入框。可能是：① 页面用新前端框架写的，内嵌的 IE 内核渲染不了；"
                + "② 登录框在跨域 iframe 里。建议点上面「用系统浏览器打开」，在浏览器里登录一次。");
            return false;
        }

        private string GetUser()
        {
            ConfigStore.Account a = SelectedAccount();
            if (a != null && !string.IsNullOrEmpty(a.User)) return a.User;
            return "";
        }

        /// <summary>提交之后隔几秒探一次网：页面自己跳成功页或失败页，我们只看"网通没通"。</summary>
        private void AfterSubmit()
        {
            int token = _loadToken;
            Thread t = new Thread(delegate()
            {
                try
                {
                    Thread.Sleep(5000);
                    if (token != _loadToken) return;   // 期间又导航过了，这次结果作废
                    bool netOk = ProbeInternet();
                    Dispatcher.Invoke(new Action(delegate()
                    {
                        if (token != _loadToken) return;
                        SetStatus(netOk
                            ? "已提交，网络已连通 —— 认证成功。"
                            : "已提交，但还没连通。可能是账号密码不对，或者页面还停在登录页 —— 看一眼下面的网页。");
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static bool ProbeInternet()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                    "http://connect.rom.miui.com/generate_204");
                req.Method = "GET";
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return true;
                }
            }
            catch (WebException wex)
            {
                // 有响应就说明通了（认证前通常会拿到 302 跳转到认证页）
                return wex.Response != null;
            }
            catch
            {
                return false;
            }
        }

        // ==================================================================
        // DOM 操作
        // ==================================================================

        /// <summary>主文档 + 可访问的 iframe 文档。跨域的取不到，直接跳过。</summary>
        private List<WinForms.HtmlDocument> CollectDocuments()
        {
            var list = new List<WinForms.HtmlDocument>();
            try
            {
                WinForms.HtmlDocument main = browser.Document;
                if (main == null) return list;
                list.Add(main);
                CollectFrames(main, list, 0);
            }
            catch { }
            return list;
        }

        private static void CollectFrames(WinForms.HtmlDocument doc, List<WinForms.HtmlDocument> into, int depth)
        {
            if (depth > 2) return;   // 够用了，再深多半是广告位
            try
            {
                foreach (WinForms.HtmlWindow w in doc.Window.Frames)
                {
                    WinForms.HtmlDocument d = null;
                    try { d = w.Document; }
                    catch { }
                    if (d == null) continue;
                    into.Add(d);
                    CollectFrames(d, into, depth + 1);
                }
            }
            catch { }
        }

        private static List<WinForms.HtmlElement> All(WinForms.HtmlDocument doc)
        {
            var list = new List<WinForms.HtmlElement>();
            try
            {
                foreach (WinForms.HtmlElement el in doc.All) list.Add(el);
            }
            catch { }
            return list;
        }

        private static string DescribeDoc(List<WinForms.HtmlElement> all)
        {
            int inputs = 0;
            int passwords = 0;
            var sample = new List<string>();
            foreach (WinForms.HtmlElement el in all)
            {
                string t = TagOf(el);
                if (t == "input")
                {
                    inputs++;
                    if ("password".Equals(AttrOf(el, "type"), StringComparison.OrdinalIgnoreCase)) passwords++;
                }
                if (sample.Count < 14 && t.Length > 0) sample.Add(t);
            }
            return "元素总数=" + all.Count + " input=" + inputs + " password=" + passwords
                + " 前若干标签=[" + string.Join(",", sample.ToArray()) + "]";
        }

        private static string TagOf(WinForms.HtmlElement el)
        {
            try { return (el.TagName ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        private static string AttrOf(WinForms.HtmlElement el, string name)
        {
            try
            {
                string v = el.GetAttribute(name);
                return v == null ? "" : v;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 判断布尔型属性是否为真。
        ///
        /// ⚠️ 这里有个 IE 的经典坑：`getAttribute("disabled")` 读一个**没设置**的布尔属性，
        /// 返回的是字符串 **"false"**，而不是空串（IE8+ 标准模式下的行为）。
        /// 所以"长度大于 0 就算禁用"会把所有正常输入框都当成禁用的 ——
        /// 症状是"页面里明明有密码框，程序却说没找到"，极难排查。
        /// </summary>
        private static bool AttrTrue(WinForms.HtmlElement el, string name)
        {
            string v = (AttrOf(el, name) ?? "").Trim().ToLowerInvariant();
            return v.Length > 0 && v != "false" && v != "0";
        }

        private static bool IsDisabled(WinForms.HtmlElement el)
        {
            return AttrTrue(el, "disabled") || AttrTrue(el, "readonly");
        }

        private static WinForms.HtmlElement FindPasswordInput(List<WinForms.HtmlElement> all)
        {
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                if (!"password".Equals(AttrOf(el, "type"), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsDisabled(el)) continue;
                return el;
            }
            return null;
        }

        /// <summary>
        /// 账号框的推断顺序：
        ///   ① name / id 里带 user / account / name / 账号 / 用户名 的文本输入框（最可靠）
        ///   ② 密码框**前面**最近的那个文本输入框（登录表单基本都是"上一行是账号"）
        /// </summary>
        private static WinForms.HtmlElement FindUserInput(List<WinForms.HtmlElement> all, WinForms.HtmlElement pw)
        {
            string[] keys = new string[]
            {
                "username", "userid", "user", "account", "loginname", "login",
                "name", "zhanghao", "yonghu", "wlanuserip", "wlanacname", "stu"
            };

            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type != "" && type != "text" && type != "tel" && type != "search") continue;
                if (IsDisabled(el)) continue;

                string id = (AttrOf(el, "id") + " " + AttrOf(el, "name")).ToLowerInvariant();
                if (id.Length == 0) continue;
                foreach (string k in keys)
                {
                    if (id.IndexOf(k, StringComparison.Ordinal) >= 0) return el;
                }
            }

            // 退而求其次：密码框之前最近的一个文本输入框
            int pwIndex = all.IndexOf(pw);
            if (pwIndex < 0) pwIndex = all.Count;
            for (int i = pwIndex - 1; i >= 0; i--)
            {
                WinForms.HtmlElement el = all[i];
                if (TagOf(el) != "input") continue;
                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type != "" && type != "text" && type != "tel") continue;
                if (IsDisabled(el)) continue;
                return el;
            }
            return null;
        }

        private static WinForms.HtmlElement FindSubmit(List<WinForms.HtmlElement> all)
        {
            // form 元素留着兜底用（最后找不到按钮时直接提交表单）
            WinForms.HtmlElement form = null;
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) == "form")
                {
                    form = el;
                    break;
                }
            }

            string[] words = new string[] { "登录", "登陆", "认证", "连接", "上线", "确定", "提交", "login", "logon", "connect", "submit", "signin" };

            foreach (WinForms.HtmlElement el in all)
            {
                string tag = TagOf(el);
                if (tag != "input" && tag != "button" && tag != "a" && tag != "div" && tag != "span") continue;
                if (IsDisabled(el)) continue;

                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type == "submit" || type == "button") return el;

                string text = "";
                try { text = (el.InnerText ?? "").Trim(); }
                catch { }
                if (text.Length == 0 || text.Length > 12) continue;   // 太长的多半是段落不是按钮

                string low = text.ToLowerInvariant();
                foreach (string w in words)
                {
                    if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return el;
                }
            }

            // ② 实在找不到，就交还表单自己提交
            if (form != null)
            {
                try
                {
                    WinForms.HtmlElementCollection subs = form.GetElementsByTagName("input");
                    foreach (WinForms.HtmlElement el in subs)
                    {
                        if ("submit".Equals(AttrOf(el, "type"), StringComparison.OrdinalIgnoreCase)) return el;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string Describe(WinForms.HtmlElement el)
        {
            string id = AttrOf(el, "id");
            string name = AttrOf(el, "name");
            string type = AttrOf(el, "type");
            string s = TagOf(el);
            if (type.Length > 0) s = s + "[type=" + type + "]";
            if (id.Length > 0) s = s + "#" + id;
            else if (name.Length > 0) s = s + "[name=" + name + "]";
            return s;
        }

        /// <summary>
        /// 给输入框赋值。
        /// 注意必须**同时**改 value 属性和触发 change 事件 ——
        /// 有些页面靠 onchange 才把值同步到内部变量，只赋值不触发等于没填。
        /// </summary>
        private static void SetValue(WinForms.HtmlElement el, string value)
        {
            try { el.SetAttribute("value", value); }
            catch { }
            try { el.InvokeMember("focus"); } catch { }
            try { el.InvokeMember("onchange"); } catch { }
            try { el.InvokeMember("onkeyup"); } catch { }
            try { el.InvokeMember("blur"); } catch { }
        }

        private static void ClickElement(WinForms.HtmlElement el)
        {
            try { el.InvokeMember("click"); return; }
            catch { }

            // click 点不动就退回"直接提交表单"
            try
            {
                WinForms.HtmlElementCollection forms = el.Document.GetElementsByTagName("form");
                if (forms != null && forms.Count > 0) forms[0].InvokeMember("submit");
            }
            catch (Exception ex)
            {
                Log.Warn("提交登录表单失败（请手动点页面上的登录）: " + ex.Message);
            }
        }
    }
}
