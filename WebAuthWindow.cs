using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        /// <summary>已保存的认证网址下拉框。</summary>
        private ComboBox cmbUrl;
        /// <summary>装载下拉项期间为 true —— 用来区分"程序在填"和"用户选了"。</summary>
        private bool _loadingUrlList = false;
        private ComboBox cmbAccount;
        private PasswordBox txtPass;
        private CheckBox chkAuto;
        private CheckBox chkAutoClose;
        private Button btnLogin;
        private TextBlock lblStatus;
        private TextBlock lblFound;

        private bool _autoDoneThisLoad = false;
        private int _loadToken = 0;

        // ------------------------------------------------------------------
        // 验证码辅助（C9 第一阶段：放大 + 换一张 + 带标签采集）
        // 细节见 CaptchaAssist.cs 的类注释；这里只放"界面状态"。
        // ------------------------------------------------------------------

        /// <summary>放大后的验证码图。原图只有 80×24，放大是为了让人一眼看清。</summary>
        private const int CaptchaZoomWidth = 320;    // 80 × 4
        private const int CaptchaZoomHeight = 96;    // 24 × 4

        /// <summary>验证码那一行（页面没有验证码时整行折叠，不留空档）。</summary>
        private StackPanel rowCaptcha;
        private Image imgCaptcha;
        private TextBlock lblCaptchaHint;

        /// <summary>C9 第二阶段：「自动填写」按钮。没有验证码时整行折叠，它也一起藏起来。</summary>
        private Button btnOcrCaptcha;

        /// <summary>当前这张验证码的原始 PNG 字节。采集样本要用它（必须和用户填的那 4 位是同一张）。</summary>
        private byte[] _captchaPng;

        /// <summary>用户当前填在验证码框里的内容（每秒读一次）。</summary>
        private string _captchaTyped = "";

        /// <summary>每秒读一次"用户填了什么"。</summary>
        private DispatcherTimer _captchaTypedTimer;

        /// <summary>等图片真正下载完再抠图的短轮询。</summary>
        private DispatcherTimer _captchaPoll;
        private int _captchaPollLeft = 0;

        /// <summary>手动点「重新抠图」失败时不要把整行藏掉（用户是主动来要结果的，得给他回话）。</summary>
        private bool _captchaPollKeepRow = false;

        /// <summary>
        /// 本轮加载是否已经判定过样本。
        ///
        /// 为什么需要：判定样本用的是"上一轮填的验证码 + 这次页面跳转"，
        /// 而 DocumentCompleted 可能因为 iframe 多次触发；同一次跳转只该判一次，
        /// 否则会在几毫秒内连着存两条一样的样本。
        /// </summary>
        private bool _sampleTriedThisLoad = false;

        /// <summary>
        /// 「可访问文档 N 个」这条日志，最后一轮是第几轮加载时写的。
        ///
        /// ⚠️ 存在的理由：补填定时器每 1.2 秒重试一次、一轮最多 12 次 ——
        ///    这条曾经是无条件写的，实测 2026-09-30 一天占了整个日志的
        ///    **27.2%（169/622 行）**，是全部日志里最吵的一条（同一秒里最多连着写 6 遍）。
        ///    文档个数在一轮加载内不会变，记一遍足够。
        ///
        /// ⚠️⚠️ 注意它和下面 `_noPwdLoggedToken` 是**两个独立的标记**，别合并 ——
        ///    2026-10-01 发现第十一批修这里时只加了判断、漏了赋值，
        ///    导致这条根本没被节流；而一旦补上赋值，又会把「没有密码框」那条
        ///    永久压掉（同一个 token 被先到的用掉了）。所以必须分开。
        /// </summary>
        private int _docsLoggedToken = -1;

        /// <summary>
        /// 密码框相关的两条诊断（「这个文档里没有密码框」/「密码框是按名字认出来的」），
        /// 最后一轮是第几轮加载时写的。
        ///
        /// ⚠️ 存在的理由：这两条原先都是无条件的，而补填定时器每 1.2 秒重试一次、
        ///    一轮最多 12 次，加上 DocumentCompleted 也会调一次 —— 实测一天下来
        ///    「没有密码框」占了整个日志的 17.9%（108/604 行），把真正的线索全淹了。
        ///    同一轮加载里记一遍就够了。
        ///
        /// ⚠️ 与 `_docsLoggedToken` 必须分开，原因见上。
        /// </summary>
        private int _noPwdLoggedToken = -1;

        /// <summary>本轮页面里是否已经"填好并提交过"一次（用来避免反复提交）。导航新页面时复位。</summary>
        private bool _submittedThisLoad = false;
        private DispatcherTimer _retryTimer;
        private int _retryLeft = 0;

        /// <summary>下拉框里放的是账号名（字符串），这里按同一下标反查完整的账号对象。</summary>
        private List<ConfigStore.Account> _accounts = new List<ConfigStore.Account>();

        /// <summary>
        /// 开机自启弹出时，登录成功后是否自动关闭本窗口。
        /// 由 SettingsWindow 的「网页认证开机行为」决定；手动打开时恒为 false。
        /// </summary>
        private bool _startupAutoClose = false;

        /// <summary>由 MainWindow 在开机自启场景调用，设置"登录成功后自动关闭"。</summary>
        internal void SetStartupAutoClose(bool autoClose)
        {
            _startupAutoClose = autoClose;
            if (chkAutoClose != null) chkAutoClose.IsChecked = autoClose;
            if (autoClose)
            {
                SetStatus("开机自启：账号密码已自动填好，填完验证码点登录即可（成功后本窗口会自动关闭）。");
                StartAutoCloseGuard();
            }
        }

        /// <summary>本窗口是否是"自动重连"拉起来的（判断要不要自动填表）。</summary>
        private bool _reauthMode = false;

        /// <summary>
        /// 由 MainWindow 在**自动重连**场景调用：这个窗口是程序自己因为掉线开的，
        /// 不是用户点开的。
        ///
        /// 与开机自启的区别：
        ///   · 开机自启是"刚开机，还没连过"，用户坐在电脑前等
        ///   · 自动重连是"本来好好的，突然断了" —— 用户可能正在打游戏/看视频，
        ///     所以这个窗口**不抢焦点**（由调用方设 ShowActivated=false），
        ///     并且状态文案要说清"是掉线了才自动弹出来的"，不然用户会莫名其妙。
        ///
        /// ⚠️ 这个模式下同样**绝不自动填验证码** —— 那是用户的活。
        /// </summary>
        internal void SetReauthMode(bool reauth)
        {
            _reauthMode = reauth;
            if (reauth)
            {
                SetStatus("检测到掉线，已自动打开认证页。账号密码已帮你填好，"
                    + "填一下验证码再点登录即可。");
                // 连通后自己也关掉 —— 和开机自启同样的道理：
                // 任务完成了就不该留个窗口杵在那儿。用户在打游戏时尤其明显。
                StartAutoCloseGuard();
            }
        }

        /// <summary>用户手动勾/取消「登录成功后自动关闭」时调用。</summary>
        private void ApplyAutoCloseToggle(bool on)
        {
            _startupAutoClose = on;
            if (on)
            {
                SetStatus("已开启：检测到网络真的连通后，本窗口会自动关闭。");
                StartAutoCloseGuard();
            }
            else
            {
                SetStatus("已关闭自动关闭。");
            }
        }

        /// <summary>把界面上的勾选框状态同步成当前配置（防止界面与实际不一致）。</summary>
        private void SyncAutoCloseCheckbox()
        {
            if (chkAutoClose == null) return;
            try
            {
                if (chkAutoClose.IsChecked != _startupAutoClose)
                {
                    // 用 suppress 思路：直接设值会触发 Checked/Unchecked 事件，
                    // 那次事件里又会去 StartAutoCloseGuard —— 这里只想同步显示。
                    // 为简单起见，只在确实不一致时才写，且写之前把标记设好，事件里会走同一条路径。
                    chkAutoClose.IsChecked = _startupAutoClose;
                }
            }
            catch { }
        }

        public WebAuthWindow(MainWindow owner)
        {
            this.owner = owner;

            Title = "校园网网页认证";
            Width = 1040;
            Height = 740;
            // ⚠️ MinWidth 必须 >= 最宽那一行控件的固有宽度，否则用户把窗口拉窄时
            //    右边会被切掉（这是踩过的坑：网址那一行曾经固定要 1062px，
            //    而窗口默认只有 900px，「导出页面结构」「字段档案」直接被裁掉）。
            MinWidth = 760;
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
                    if (_captchaPoll != null) _captchaPoll.Stop();
                    if (_captchaTypedTimer != null) _captchaTypedTimer.Stop();
                }
                catch { }

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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 网址
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 1 功能按钮
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 2 账号/密码
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 3 自动关闭
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 4 状态
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 5 验证码辅助（可折叠）
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 6 浏览器

            // ---------- 第 1 行：认证网址 ----------
            // ⚠️ 这一行只放"下拉框 + 输入框 + 打开 + 存"。
            //    曾经把 4 个按钮全塞在这行，固定宽度要 1062px，比窗口还宽 ——
            //    结果「导出页面结构」「字段档案」在正常窗口大小下直接被切掉。
            //    教训：多一行能解决的事，不要靠撑宽窗口。
            var rowUrl = new StackPanel { Orientation = Orientation.Horizontal };
            rowUrl.Children.Add(MakeLabel("认证网址"));

            // 已保存的网址，下拉选。像账号下拉那样 ——
            // 学校认证页不止一个（宿舍/教学楼入口不同、运营商跳转目标不同），
            // 存下来就不用每次手打完整 IP。
            cmbUrl = new ComboBox
            {
                Width = 168,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                ToolTip = "选一个存过的认证网址。选完会自动填进右边的框并打开"
            };
            cmbUrl.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (_loadingUrlList) return;      // 装载下拉项时不当作"用户选了"
                var item = cmbUrl.SelectedItem as ComboBoxItem;
                if (item == null) return;
                string u = item.Tag as string;
                if (string.IsNullOrEmpty(u)) return;

                txtUrl.Text = u;
                Navigate(u, true);
            };
            rowUrl.Children.Add(cmbUrl);

            txtUrl = new TextBox
            {
                // 不写死宽度，让它把这一行剩下的空间都吃掉 ——
                // 窗口拉宽，输入框跟着变宽，不会留一块空白。
                MinWidth = 200,
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
            btnOpen.Margin = new Thickness(0, 0, 8, 0);
            rowUrl.Children.Add(btnOpen);

            // 「存到列表」：把当前框里的地址记下来，下次下拉就能直接选。
            // 单独一个按钮而不是"自动存" —— 用户可能只是临时试试某个地址，
            // 不该让每次导航都往清单里塞一条。
            var btnSaveUrl = MainWindow.MakeGhostButton("存到列表", delegate() { SaveCurrentUrlToList(); });
            btnSaveUrl.FontSize = 12;
            btnSaveUrl.ToolTip = "把这个网址记下来，以后从左边下拉框直接选";
            rowUrl.Children.Add(btnSaveUrl);

            var btnDelUrl = MainWindow.MakeGhostButton("删除", delegate() { DeleteCurrentUrlFromList(); });
            btnDelUrl.FontSize = 12;
            btnDelUrl.Margin = new Thickness(6, 0, 0, 0);
            btnDelUrl.ToolTip = "从下拉列表里删掉当前这个网址";
            rowUrl.Children.Add(btnDelUrl);

            Grid.SetRow(rowUrl, 0);
            root.Children.Add(rowUrl);

            // ---------- 第 1.5 行：功能按钮 ----------
            // 用 WrapPanel 而不是 StackPanel：窗口被拉窄时按钮会**自动折到下一行**，
            // 而不是被右边裁掉。这是"永远看得见所有功能键"的关键。
            var rowTools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            // 字段档案排在第一个 —— 它是这一屏里最常用、也最能让用户搞明白"程序在填什么"的按钮。
            var btnProfile = MainWindow.MakePrimaryButton("字段档案", OpenProfileEditor);
            btnProfile.FontSize = 12;
            btnProfile.Margin = new Thickness(0, 0, 8, 6);
            btnProfile.ToolTip = "教程序认框：指定页面上每个输入框该填什么，存下来。页面框多的时候用它";
            rowTools.Children.Add(btnProfile);

            var btnSys = MainWindow.MakeGhostButton("用系统浏览器打开", delegate()
            {
                OpenInSystemBrowser(txtUrl.Text);
            });
            btnSys.FontSize = 12;
            btnSys.Margin = new Thickness(0, 0, 8, 6);
            rowTools.Children.Add(btnSys);

            // 调试用：把内嵌浏览器"看到的"页面结构导出到日志。
            // 排查"明明有密码框，程序却说没找到"这类问题时，这是最直接的证据 ——
            // 能一次看清 IE 内核拿到的到底是哪个文档、里面有哪些元素。
            var btnDump = MainWindow.MakeGhostButton("导出页面结构", DumpPageStructure);
            btnDump.FontSize = 12;
            btnDump.Margin = new Thickness(0, 0, 8, 6);
            btnDump.ToolTip = "把当前页面里所有表单元素写进日志，用于排查填表失效";
            rowTools.Children.Add(btnDump);

            Grid.SetRow(rowTools, 1);
            root.Children.Add(rowTools);

            // ---------- 第 3 行：账号 / 密码 ----------
            // 也改成 WrapPanel：窗口窄的时候「填表并登录」按钮会折到下一行，
            // 不会被挤出去看不见。
            var rowAcc = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
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

            Grid.SetRow(rowAcc, 2);
            root.Children.Add(rowAcc);

            // ---------- 第 4 行：登录成功后自动关闭 ----------
            var rowAuto = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            chkAutoClose = new CheckBox
            {
                Content = "登录成功后自动关闭本窗口",
                IsChecked = false,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            chkAutoClose.Checked += delegate(object s, RoutedEventArgs e) { ApplyAutoCloseToggle(true); };
            chkAutoClose.Unchecked += delegate(object s, RoutedEventArgs e) { ApplyAutoCloseToggle(false); };
            rowAuto.Children.Add(chkAutoClose);

            var autoHint = MakeHint("（勾上后，检测到网络真的连通就自动关窗）");
            autoHint.Margin = new Thickness(12, 0, 0, 0);
            autoHint.VerticalAlignment = VerticalAlignment.Center;
            rowAuto.Children.Add(autoHint);

            Grid.SetRow(rowAuto, 3);
            root.Children.Add(rowAuto);

            // ---------- 第 5 行：状态 ----------
            var statusBox = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            lblFound = MakeHint("");
            lblStatus = MakeHint("");
            statusBox.Children.Add(lblFound);
            statusBox.Children.Add(lblStatus);
            Grid.SetRow(statusBox, 4);
            root.Children.Add(statusBox);

            // ---------- 第 6 行：验证码辅助（页面没有验证码时整行折叠） ----------
            // 为什么放在浏览器**上面**：验证码在网页里往往要滚动才看得见，
            // 抠出来放大贴在窗口上方，用户不用在页面里找它。
            rowCaptcha = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            rowCaptcha.Children.Add(MakeLabel("验证码"));

            // 背景固定白：验证码图本身就是白底，跟着主题变会把它衬得看不清。
            var shotWrap = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Width = CaptchaZoomWidth,
                Height = CaptchaZoomHeight
            };
            imgCaptcha = new Image
            {
                Width = CaptchaZoomWidth,
                Height = CaptchaZoomHeight,
                Stretch = Stretch.Fill,
                ToolTip = "这是页面上的验证码，放大 4 倍显示。看不清就点右边「换一张」"
            };
            // ⚠️ 必须用平滑（HighQuality）缩放，别改成 NearestNeighbor：
            //    原图只有 80×24、干扰线本身就是 1px，最近邻会把噪点一起放大成方块，
            //    实测比"页面自带的显示"更难认（2026-10-02 拿真图四种方案并排比过）。
            RenderOptions.SetBitmapScalingMode(imgCaptcha, BitmapScalingMode.HighQuality);
            shotWrap.Child = imgCaptcha;
            rowCaptcha.Children.Add(shotWrap);

            var btnNewCaptcha = MainWindow.MakeGhostButton("换一张", OnCaptchaRefresh);
            btnNewCaptcha.FontSize = 12;
            btnNewCaptcha.Margin = new Thickness(10, 0, 0, 0);
            btnNewCaptcha.VerticalAlignment = VerticalAlignment.Top;
            btnNewCaptcha.ToolTip = "让页面重新要一张验证码图（页面上的那格也会跟着变）";
            rowCaptcha.Children.Add(btnNewCaptcha);

            // C9 第二阶段：「自动填写」——识别出候选**填进框**，仅此而已。
            // ⚠️ 这个按钮绝不会点登录、也不会调用任何 submit。识别错一位就会在学校端
            //    记一次登录失败，而学校普遍对连续失败有次数限制，所以提交权只能在人手里。
            //    红线写在 CaptchaAssist 的类注释和 CaptchaOcr 的类注释里。
            btnOcrCaptcha = MainWindow.MakeGhostButton("自动填写", OnCaptchaOcr);
            btnOcrCaptcha.FontSize = 12;
            btnOcrCaptcha.Margin = new Thickness(8, 0, 0, 0);
            btnOcrCaptcha.VerticalAlignment = VerticalAlignment.Top;
            btnOcrCaptcha.ToolTip = "认出这 4 位并填进输入框（可能错，请核对后再自己点登录）";
            rowCaptcha.Children.Add(btnOcrCaptcha);

            var btnAgainCaptcha = MainWindow.MakeGhostButton("重新抠图", delegate()
            {
                _captchaPollKeepRow = true;
                StartCaptchaPoll(4);
            });
            btnAgainCaptcha.FontSize = 12;
            btnAgainCaptcha.Margin = new Thickness(8, 0, 0, 0);
            btnAgainCaptcha.VerticalAlignment = VerticalAlignment.Top;
            btnAgainCaptcha.ToolTip = "页面自己刷新过图就点它，重新取一次当前的图";
            rowCaptcha.Children.Add(btnAgainCaptcha);

            lblCaptchaHint = MakeHint("");
            lblCaptchaHint.Margin = new Thickness(12, 4, 0, 0);
            lblCaptchaHint.MaxWidth = 300;
            rowCaptcha.Children.Add(lblCaptchaHint);

            Grid.SetRow(rowCaptcha, 5);
            root.Children.Add(rowCaptcha);

            // ---------- 第 7 行：浏览器 ----------
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
            Grid.SetRow(frame, 6);
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
            // 升级上来时，先把旧版单独存的那个网址搬进清单 ——
            // 用户不该因为升级就发现自己填过的地址不见了。
            owner.MigrateLegacyUrl();

            string url = owner.WebAuthUrl();
            txtUrl.Text = url;
            RefreshUrlList(url);

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
                    + "地址填好后点「存到列表」，下次从左边下拉框直接选就行。");
            }
        }

        // ==================================================================
        // 认证网址清单
        // ==================================================================

        /// <summary>
        /// 重建网址下拉框。prefer 是要预选中的那条（一般是当前输入框里的网址）。
        ///
        /// ⚠️ 全程用 _loadingUrlList 屏蔽 SelectionChanged —— 否则"程序填下拉项"
        ///    会被当成"用户选了"，触发一次多余的导航（还会把用户刚打的字冲掉）。
        ///    这个坑在字段档案窗口里踩过一次，这里直接用同样的防法。
        /// </summary>
        private void RefreshUrlList(string prefer)
        {
            if (cmbUrl == null) return;

            _loadingUrlList = true;
            try
            {
                cmbUrl.Items.Clear();

                List<WebUrlStore.Entry> all = owner.WebUrlsSnapshot();
                if (all.Count == 0)
                {
                    cmbUrl.Items.Add(new ComboBoxItem
                    {
                        Content = "（还没有保存的网址）",
                        IsEnabled = false,
                        Tag = null
                    });
                    cmbUrl.SelectedIndex = 0;
                    cmbUrl.IsEnabled = false;
                    return;
                }

                cmbUrl.IsEnabled = true;

                string wantKey = FieldProfileStore.NormalizeUrl(prefer);
                ComboBoxItem selected = null;

                foreach (WebUrlStore.Entry e in all)
                {
                    var item = new ComboBoxItem();
                    item.Content = e.Display();
                    item.Tag = e.Url;
                    item.ToolTip = e.Url
                        + (string.IsNullOrEmpty(e.LastUsed) ? "" : "\n最后使用：" + e.LastUsed);
                    cmbUrl.Items.Add(item);

                    if (selected == null && wantKey.Length > 0
                        && string.Equals(FieldProfileStore.NormalizeUrl(e.Url), wantKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selected = item;
                    }
                }

                cmbUrl.SelectedItem = selected != null ? selected : cmbUrl.Items[0];
            }
            finally
            {
                _loadingUrlList = false;
            }
        }

        /// <summary>把当前输入框里的网址存进清单。</summary>
        private void SaveCurrentUrlToList()
        {
            string raw = (txtUrl.Text ?? "").Trim();
            string clean = WebUrlStore.Clean(raw);
            if (clean.Length == 0)
            {
                SetStatus("网址是空的，先填一个再存。");
                return;
            }

            // 没名字就问一个 —— 可选，不给也能存，只是下拉里显示裸网址。
            // 用输入框而不是弹窗：这个操作会反复做，弹窗太打断人。
            string name = AskUrlName(clean);
            if (name == null) return;   // 用户取消了

            string msg;
            if (!owner.SaveWebUrl(clean, name, out msg))
            {
                SetStatus(msg.Length > 0 ? msg : "保存失败。");
                return;
            }

            txtUrl.Text = clean;
            RefreshUrlList(clean);
            SetStatus(string.IsNullOrEmpty(name)
                ? "已存入网址列表（没起名字）。下次从左边下拉框直接选。"
                : "已存入网址列表，名字是「" + name + "」。下次从左边下拉框直接选。");
        }

        /// <summary>从清单里删掉当前输入框里的网址。</summary>
        private void DeleteCurrentUrlFromList()
        {
            string raw = (txtUrl.Text ?? "").Trim();
            string clean = WebUrlStore.Clean(raw);
            if (clean.Length == 0)
            {
                SetStatus("网址是空的，没得删。");
                return;
            }
            if (!WebUrlStore.Contains(clean))
            {
                SetStatus("列表里没有这个网址，不用删。");
                return;
            }

            MessageBoxResult r = MessageBox.Show(
                "从网址列表里删掉这条？\n\n" + clean
                + "\n\n（只影响列表，不影响你此刻正在用的这个地址）",
                "校园网助手", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            string msg;
            if (!owner.DeleteWebUrl(clean, out msg))
            {
                SetStatus(msg.Length > 0 ? msg : "删除失败。");
                return;
            }

            RefreshUrlList(clean);
            SetStatus("已从网址列表删掉：" + clean);
        }

        /// <summary>
        /// 问一个"给这条网址起个名"，可留空。返回 null 表示用户点了取消。
        ///
        /// ⚠️ 用自定义窗口而不是 Microsoft.VisualBasic.Interaction.InputBox：
        ///    后者要引 VisualBasic 程序集，而本项目是 csc 单文件编译，
        ///    为了一个改名框去加程序集引用不划算。
        /// </summary>
        private string AskUrlName(string url)
        {
            var dlg = new Window
            {
                Title = "给这个网址起个名字",
                Width = 420,
                Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Theme.WindowBg)
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tip = new TextBlock
            {
                Text = "起个名字方便认（比如「宿舍」「教学楼」）。\n留空也行，列表里就直接显示网址。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Theme.TextMuted)
            };
            Grid.SetRow(tip, 0);
            grid.Children.Add(tip);

            var box = new TextBox
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            string result = null;

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var btnOk = MainWindow.MakePrimaryButton("存", delegate()
            {
                result = (box.Text ?? "").Trim();
                dlg.DialogResult = true;
                dlg.Close();
            });
            btnOk.MinWidth = 76;
            var btnCancel = MainWindow.MakeGhostButton("取消", delegate()
            {
                dlg.DialogResult = false;
                dlg.Close();
            });
            btnCancel.MinWidth = 76;
            btnCancel.Margin = new Thickness(8, 0, 0, 0);
            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);
            Grid.SetRow(btnRow, 2);
            grid.Children.Add(btnRow);

            dlg.Content = grid;
            dlg.Loaded += delegate(object s, RoutedEventArgs e) { box.Focus(); };

            bool? ok = dlg.ShowDialog();
            return ok == true ? result : null;
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

        // ==================================================================
        // 验证码辅助：抠图 / 放大 / 换一张 / 采集样本
        //
        // 三条红线写在 CaptchaAssist.cs 的类注释里，这里只重复最要命的一条：
        // **绝不自己带 cookie 重新请求 /CheckCode** —— 门户把答案绑在 session 上，
        // 重请求会把页面正在显示的那张图作废，用户照图填必然错。
        // 所以这里所有取图动作都只针对"页面已经加载好的那张图"。
        // ==================================================================

        /// <summary>等图片下载完再抠图（每 0.7 秒试一次，最多 rounds 次）。</summary>
        private void StartCaptchaPoll(int rounds)
        {
            if (rowCaptcha == null) return;      // 窗口已经关掉了

            if (_captchaPoll == null)
            {
                _captchaPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _captchaPoll.Tick += delegate(object s, EventArgs e)
                {
                    _captchaPollLeft--;
                    if (TryCaptureCaptcha())
                    {
                        _captchaPoll.Stop();
                        return;
                    }
                    if (_captchaPollLeft <= 0)
                    {
                        _captchaPoll.Stop();
                        if (!_captchaPollKeepRow) HideCaptchaRow();
                    }
                };
            }
            _captchaPollLeft = rounds;
            _captchaPoll.Start();
        }

        /// <summary>
        /// 从页面里抠出验证码图并放大显示。页面没有验证码、或图还没下载完 → 返回 false。
        /// </summary>
        private bool TryCaptureCaptcha()
        {
            if (browser == null || imgCaptcha == null) return false;

            string raw;
            try
            {
                WinForms.HtmlDocument doc = browser.Document;
                if (doc == null) return false;

                doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildExtractJs() });
                object ret = doc.InvokeScript("__cnhCaptchaCap");
                raw = ret == null ? "" : ret.ToString();
            }
            catch (Exception ex)
            {
                // 只在最后一次尝试时留痕，否则这个轮询会自己把日志刷爆
                if (_captchaPollLeft <= 1) Log.Warn("验证码取图脚本调用失败: " + ex.Message);
                return false;
            }

            int w, h;
            string b64, err;
            if (!CaptchaAssist.TryParseShot(raw, out w, out h, out b64, out err))
            {
                // 「页面上本来就没有验证码」「图还没加载完」都是正常情况，不记日志
                bool normal = err.IndexOf("no-img", StringComparison.Ordinal) >= 0
                           || err.IndexOf("not-ready", StringComparison.Ordinal) >= 0;
                if (!normal && _captchaPollLeft <= 1) Log.Warn("验证码取图失败: " + err);
                return false;
            }

            try
            {
                byte[] png = Convert.FromBase64String(b64);
                _captchaPng = png;
                _captchaTyped = "";

                imgCaptcha.Source = BitmapFrame.Create(
                    new System.IO.MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                rowCaptcha.Visibility = Visibility.Visible;
                SetCaptchaHint("看不清就点「换一张」。");

                // 新的一张图 = 一次新的采集机会
                _sampleTriedThisLoad = false;
                StartCaptchaTypedWatch();

                Log.Info("验证码已抠出（原图 " + w + "×" + h + "）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("验证码图显示失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>每秒读一次"用户填了什么"——纯本地 DOM 读取，不发网络请求。</summary>
        private void StartCaptchaTypedWatch()
        {
            if (_captchaTypedTimer == null)
            {
                _captchaTypedTimer = new DispatcherTimer(DispatcherPriority.Background);
                _captchaTypedTimer.Interval = TimeSpan.FromSeconds(1);
                _captchaTypedTimer.Tick += delegate(object s, EventArgs e) { PollCaptchaTyped(); };
            }
            if (!_captchaTypedTimer.IsEnabled) _captchaTypedTimer.Start();
        }

        private void PollCaptchaTyped()
        {
            try
            {
                if (_captchaPng == null || browser == null) return;
                WinForms.HtmlDocument doc = browser.Document;
                if (doc == null) return;

                doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildTypedJs() });
                object ret = doc.InvokeScript("__cnhCaptchaTyped");
                string raw = ret == null ? "" : ret.ToString();

                string v;
                if (!CaptchaAssist.TryParseTyped(raw, out v)) return;
                if (v == _captchaTyped) return;
                _captchaTyped = v;

                if (CaptchaAssist.IsValidCaptchaText(v))
                    SetCaptchaHint("已填 4 位。登录成功后会把它记成一条样本。");
                else if (v.Length == 0)
                    SetCaptchaHint("看不清就点「换一张」。");
                else
                    SetCaptchaHint("已填 " + v.Length + " / 4 位。");
            }
            catch { }
        }

        /// <summary>
        /// 「换一张」：让**页面自己**重新加载验证码图（同时清空用户已填的值）。
        ///
        /// 为什么不是程序去请求：见 CaptchaAssist.BuildRefreshJs 的注释 ——
        /// 只有让页面自己请求，服务端换的答案和页面换的图才是同一个动作。
        /// </summary>
        private void OnCaptchaRefresh()
        {
            try
            {
                WinForms.HtmlDocument doc = browser == null ? null : browser.Document;
                if (doc == null) { SetCaptchaHint("页面还没加载好。"); return; }

                doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildRefreshJs() });
                object ret = doc.InvokeScript("__cnhCaptchaFresh");
                string s = ret == null ? "" : ret.ToString();
                if (s != "OK") { SetCaptchaHint("换图失败：" + s); return; }

                // 图换了 → 之前抠的那张作废（它对应的答案已经不是这个 session 的了）
                _captchaPng = null;
                _captchaTyped = "";
                SetCaptchaHint("已向服务器要了一张新的，正在重新取图…");

                _captchaPollKeepRow = true;   // 这次是用户主动要的，失败了也要把话说清楚
                StartCaptchaPoll(20);         // 新图要重新下载，给足 14 秒
                Log.Info("用户点了「换一张」，已让页面重新加载验证码");
            }
            catch (Exception ex)
            {
                SetCaptchaHint("换图失败：" + ex.Message);
            }
        }

        /// <summary>
        /// C9 第二阶段：「自动填写」。认出这 4 位，写进页面的验证码框，然后**停手**。
        ///
        /// ⚠️ 三条不能破的规矩（对应 CaptchaOcr 类注释里的红线）：
        ///   1. **绝不提交**。这个方法从头到尾没有任何 submit / InvokeMember("click") /
        ///      表单回传。填完就结束，点「登录」永远是用户的事。
        ///      验收时会专门审查"全项目有没有自动提交验证码的路径"，这里就是重点。
        ///   2. **绝不重新请求 /CheckCode**。认的是 `_captchaPng` ——
        ///      也就是页面**已经显示给用户的**那一张（TryCaptureCaptcha 里
        ///      用 canvas 从内存位图取的）。重新请求会把 session 里的答案换掉，
        ///      用户照图填必错。
        ///   3. **认不准就不填**。置信度不足时一位都不写，并明确告诉用户手动填；
        ///      绝不"填个大概"——填错一位就会在学校端记一次登录失败。
        /// </summary>
        private void OnCaptchaOcr()
        {
            try
            {
                if (_captchaPng == null || _captchaPng.Length == 0)
                {
                    SetCaptchaHint("还没取到验证码图，稍等一下再点。");
                    return;
                }

                CaptchaOcr.Result r = CaptchaOcr.RecognizePng(_captchaPng);

                if (r.Error.Length > 0)
                {
                    SetCaptchaHint("识别不可用（" + r.Error + "），请手动填写。");
                    Log.Warn("验证码识别失败: " + r.Error);
                    return;
                }

                // ⚠️ 要填什么**只能**问 FillCandidate()，不能直接用 r.Text。
                //    2026-10-03 的教训：直接用 Text 会把 "A?3?" 填进验证码框
                //    （逐位闸门放过 2 位，Text 里就留着 '?'）。用户看到问号一脸问号。
                //    现在策略是"总是填 4 位 top1"，只有某位连字符都没切出来才整体不填。
                string fill = CaptchaOcr.FillCandidate(r);
                if (fill.Length == 0)
                {
                    int unread = CaptchaOcr.UnreadableCount(r);

                    // 失败必须留证。2026-10-03 海辰说"我看得很清楚，它却说无法识别"，
                    // 而失败的那张图当时没留下来 —— 我只能靠日志猜，方向一开始就偏了。
                    // 这里补上：把原图存到 captcha-fails\（与带答案的样本目录分开，
                    // 免得没答案的图被当成带标签样本污染准确率统计）。
                    if (CaptchaAssist.SampleCollectEnabled(ConfigStore.LoadSettings()))
                    {
                        string ferr;
                        CaptchaAssist.SaveFailImage(_captchaPng, unread + "unread", out ferr);
                    }

                    SetCaptchaHint(CaptchaOcr.DescribeForUser(r));

                    // 日志要把"没切出字符"和"认出来但没把握"分开说 ——
                    // 旧版一律写"置信度不足"，害得排查的人（就是我）往错的方向找。
                    Log.Info("验证码识别：未填入 —— "
                             + (unread > 0 ? ("有 " + unread + " 位没切出字符")
                                           : "结果不是 4 位字母数字")
                             + "（识别到 \"" + r.Text + "\"，达标 " + r.AcceptedCount
                             + "/4，最小 margin=" + r.MinMargin.ToString("0.###") + "）");
                    return;
                }

                // 把候选写进页面输入框（只写值，不触发任何提交）
                if (!FillCaptchaBox(fill))
                {
                    SetCaptchaHint("识别出来了，但写进输入框失败 —— 请手动填写。");
                    return;
                }

                // 让"用户填了什么"的监听立刻刷新一次，免得提示语自相矛盾
                PollCaptchaTyped();
                SetCaptchaHint(CaptchaOcr.DescribeForUser(r));
                Log.Info("验证码识别：已填入候选 " + fill
                         + "（达标 " + r.AcceptedCount + "/4，最小 margin="
                         + r.MinMargin.ToString("0.###") + "）");
            }
            catch (Exception ex)
            {
                SetCaptchaHint("识别出错：" + ex.Message + "，请手动填写。");
                Log.Warn("验证码识别异常: " + ex.Message);
            }
        }

        /// <summary>把文字写进页面的验证码输入框。只赋值，不做任何提交动作。</summary>
        private bool FillCaptchaBox(string text)
        {
            if (browser == null || browser.Document == null) return false;
            try
            {
                object docObj = browser.Document;
                WinForms.HtmlDocument doc = docObj as WinForms.HtmlDocument;
                if (doc == null) return false;
                doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildFillJs(text) });
                object ret = doc.InvokeScript("__cnhCaptchaFill");
                return ret != null && ret.ToString() == "OK";
            }
            catch (Exception ex)
            {
                Log.Warn("写入验证码输入框失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 判定"上一次提交是不是真的登上去了"；是的话把「验证码图 + 用户填的 4 位」
        /// 存成一条**带标签**的样本。
        ///
        /// 为什么挂在"页面又跳转了"这个时刻：用户是在**页面上**点登录的，
        /// 程序没有提交动作（也不允许有），所以只能从"页面自己跳走了"这个现象入手。
        ///
        /// 三道闸门，缺一条就不记（**宁缺毋滥** —— 标签错了比没有样本更糟，
        /// 它会把以后做的模板库带歪）：
        ///   ① 程序在跳转**之前**认为"已经通着" → 这次跳转不是登录带来的，不算证据
        ///   ② 跳转后 4 秒，网络**真的**通了（用全局唯一那份判据 NetProbe.Online）
        ///   ③ 跳转后的页面上**已经没有验证码了**（说明真的离开了登录页，
        ///      而不是提交失败留在原地重刷了一张图）
        /// </summary>
        private void MaybeRecordSample()
        {
            try
            {
                if (_sampleTriedThisLoad) return;
                if (_captchaPng == null || !CaptchaAssist.IsValidCaptchaText(_captchaTyped)) return;
                if (!CaptchaAssist.SampleCollectEnabled(ConfigStore.LoadSettings())) return;
                if (NetProbe.OnlineStale()) return;      // 闸门 ①（UI 线程上只能用这个，见 NetProbe 注释）

                _sampleTriedThisLoad = true;

                byte[] png = _captchaPng;               // 快照：接下来页面会变，这两个值必须锁住
                string typed = _captchaTyped;

                Thread t = new Thread(delegate()
                {
                    try
                    {
                        Thread.Sleep(4000);             // 认证生效要一点时间
                        if (!NetProbe.Online(true)) return;          // 闸门 ②

                        bool stillCaptcha = false;
                        try
                        {
                            Dispatcher.Invoke(new Action(delegate()
                            {
                                stillCaptcha = PageHasCaptchaNow();  // 闸门 ③
                            }));
                        }
                        catch { }
                        if (stillCaptcha) return;

                        string err;
                        string path = CaptchaAssist.SaveSample(png, typed, out err);
                        if (path.Length == 0) return;

                        try
                        {
                            Dispatcher.BeginInvoke(new Action(delegate()
                            {
                                SetCaptchaHint("已记下一条样本（共 " + CaptchaAssist.CountSamples() + " 条）。");
                            }));
                        }
                        catch { }
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        /// <summary>当前页面上还有没有验证码图（用来判断"是不是还停在登录页"）。</summary>
        private bool PageHasCaptchaNow()
        {
            try
            {
                WinForms.HtmlDocument doc = browser == null ? null : browser.Document;
                if (doc == null) return false;

                WinForms.HtmlElementCollection imgs = doc.GetElementsByTagName("img");
                for (int i = 0; i < imgs.Count; i++)
                {
                    object src = imgs[i].GetAttribute("src");
                    if (src != null && src.ToString().IndexOf("CheckCode", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void HideCaptchaRow()
        {
            _captchaPng = null;
            _captchaTyped = "";
            if (_captchaTypedTimer != null) _captchaTypedTimer.Stop();
            if (rowCaptcha != null) rowCaptcha.Visibility = Visibility.Collapsed;
        }

        private void SetCaptchaHint(string text)
        {
            if (lblCaptchaHint != null) lblCaptchaHint.Text = text;
        }

        // ------------------------------------------------------------------
        // 自测探针（只有 UITest 用；产品逻辑不依赖它们）
        //
        // 为什么必须能**离线**自测这条链路：
        //   抠图是"JS 注入 → canvas → base64 → 解码 → 显示"五段接力，
        //   任何一段断了都只会表现为"图不出现"，光看代码看不出来；
        //   而依赖真机 + 校园网才能跑的自测，等于没自测（放假/没连网就跑不了）。
        //   所以 UITest 会造一张本地页面来驱动这个真实窗口。
        // ------------------------------------------------------------------

        internal void LoadUrlForTest(string url)
        {
            if (txtUrl != null) txtUrl.Text = url;
            Navigate(url, false);
        }

        /// <summary>自测用：把取图脚本的**原始返回**原样交出来（含 ERR:xxx 的真实原因）。
        /// 自测期间日志是静默的，失败原因只能这样带回报告里 —— 否则"图不出现"
        /// 就成了一句没法排查的话。</summary>
        internal string RawExtractForTest()
        {
            try
            {
                WinForms.HtmlDocument doc = (browser == null) ? null : browser.Document;
                if (doc == null) return "(没有 document)";

                object ret = null;
                string err = "";
                try
                {
                    doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildExtractJs() });
                    ret = doc.InvokeScript("__cnhCaptchaCap");
                }
                catch (Exception ex) { err = " 调用异常: " + ex.GetType().Name + ": " + ex.Message; }

                if (ret == null) return "(返回 null)" + err;
                string s = ret.ToString();
                int bar = s.IndexOf('|');
                return (bar > 0 ? s.Substring(0, bar) + "…" : s) + err;
            }
            catch (Exception ex) { return "(异常) " + ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>自测用：当前页面的地址。</summary>
        internal string PageUrlForTest()
        {
            try { return (browser == null || browser.Url == null) ? "" : browser.Url.ToString(); }
            catch { return ""; }
        }

        /// <summary>自测用：页面上有几个 img、它们的 src 长什么样（确认页面到底加载没有）。</summary>
        internal string PageProbeForTest()
        {
            try
            {
                WinForms.HtmlDocument doc = (browser == null) ? null : browser.Document;
                if (doc == null) return "(没有 document)";

                WinForms.HtmlElementCollection imgs = doc.GetElementsByTagName("img");
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("img 数 = " + imgs.Count);
                for (int i = 0; i < imgs.Count; i++)
                {
                    object src = imgs[i].GetAttribute("src");
                    string t = (src == null) ? "" : src.ToString();
                    string head = t.Length > 22 ? t.Substring(0, 22) : t;
                    sb.Append(" [").Append(i).Append("] id=").Append(imgs[i].GetAttribute("id"))
                      .Append(" srcHead=").Append(head).Append(" len=").Append(t.Length);
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(异常) " + ex.Message; }
        }

        /// <summary>自测用：直接触发一次「换一张」（省得靠模拟点按钮）。</summary>
        internal void RefreshCaptchaForTest()
        {
            OnCaptchaRefresh();
        }

        internal bool CaptchaRowVisibleForTest()
        {
            return rowCaptcha != null && rowCaptcha.Visibility == Visibility.Visible;
        }

        internal int CaptchaPixelWidthForTest()
        {
            BitmapSource b = (imgCaptcha == null) ? null : (imgCaptcha.Source as BitmapSource);
            return b == null ? 0 : b.PixelWidth;
        }

        internal int CaptchaPixelHeightForTest()
        {
            BitmapSource b = (imgCaptcha == null) ? null : (imgCaptcha.Source as BitmapSource);
            return b == null ? 0 : b.PixelHeight;
        }

        internal string CaptchaHintForTest()
        {
            return lblCaptchaHint == null ? "" : lblCaptchaHint.Text;
        }

        /// <summary>自测用：直接往页面的验证码框里塞一个值（模拟用户敲完 4 位），
        /// 然后立刻走一次"读用户填了什么"，省掉那 1 秒的等待。</summary>
        internal bool TypeCaptchaForTest(string text)
        {
            try
            {
                WinForms.HtmlDocument doc = (browser == null) ? null : browser.Document;
                if (doc == null) return false;

                string js = "var el = document.getElementById('MainContent_TextBoxCC');"
                          + " if (el) el.value = '" + (text ?? "").Replace("'", "") + "';";
                doc.InvokeScript("eval", new object[] { js });
                PollCaptchaTyped();
                return true;
            }
            catch { return false; }
        }

        // ==================================================================
        // 调试：导出内嵌浏览器看到的页面结构
        // ==================================================================

        /// <summary>
        /// 把当前页面（主文档 + 各 iframe）里所有元素的标签、type、name、id 写进日志。
        ///
        /// 为什么需要这个按钮：
        ///   现象是"页面上明明有密码框，程序却报没有密码框"。
        ///   这类问题光看截图分不清三种可能 ——
        ///     ① 表单在 iframe 里，主文档只是个空壳；
        ///     ② IE 内核把文档解析成了别的结构（比如 ASP.NET 的怪癖）；
        ///     ③ 元素是 JS 后插进来的，枚举时还没渲染。
        ///   把 IE 内核**实际看到的东西**原样倒出来，一眼就能分辨。
        /// </summary>
        private void DumpPageStructure()
        {
            try
            {
                Log.Info("========== 页面结构导出 开始 ==========");

                WinForms.HtmlDocument main = browser.Document;
                if (main == null)
                {
                    Log.Info("页面结构导出：browser.Document == null（页面可能还没开始加载）");
                    SetStatus("页面还没加载好，等页面出来再点「导出页面结构」。");
                    return;
                }

                Log.Info("主文档 URL = " + Safe(() => main.Url == null ? "(null)" : main.Url.ToString()));
                Log.Info("浏览器 ReadyState = " + browser.ReadyState);

                List<WinForms.HtmlDocument> docs = CollectDocuments();
                Log.Info("可访问文档数 = " + docs.Count);

                for (int di = 0; di < docs.Count; di++)
                {
                    WinForms.HtmlDocument doc = docs[di];
                    string docUrl = Safe(() => doc.Url == null ? "(null)" : doc.Url.ToString());
                    Log.Info("---- 文档 [" + di + "] " + docUrl + " ----");

                    List<WinForms.HtmlElement> all = All(doc);
                    Log.Info("文档 [" + di + "] 元素总数 = " + all.Count);

                    // iframe 情况单列 —— 跨域 iframe 的 Document 取不到，会体现在这里
                    string framesInfo = "";
                    try
                    {
                        int fc = 0;
                        foreach (WinForms.HtmlWindow w in doc.Window.Frames)
                        {
                            fc++;
                            string fu = "(取不到)";
                            try { if (w.Document != null && w.Document.Url != null) fu = w.Document.Url.ToString(); }
                            catch { }
                            framesInfo += "\n    frame[" + fc + "] " + fu;
                        }
                        if (fc == 0) framesInfo = " (无)";
                    }
                    catch (Exception ex) { framesInfo = " (枚举失败: " + ex.Message + ")"; }
                    Log.Info("文档 [" + di + "] frames:" + framesInfo);

                    // 逐个元素：只记表单相关的，避免日志爆炸
                    int idx = 0;
                    foreach (WinForms.HtmlElement el in all)
                    {
                        string tag = TagOf(el);
                        if (tag != "input" && tag != "select" && tag != "textarea"
                            && tag != "button" && tag != "form" && tag != "img") continue;
                        if (idx++ > 60) { Log.Info("  ...(元素过多，已截断)"); break; }

                        string type = AttrOf(el, "type");
                        string name = AttrOf(el, "name");
                        string id = AttrOf(el, "id");
                        string val = AttrOf(el, "value");
                        string src = AttrOf(el, "src");
                        string dis = AttrOf(el, "disabled");

                        Log.Info("    <" + tag
                            + (type.Length > 0 ? " type=" + type : "")
                            + (name.Length > 0 ? " name=" + name : "")
                            + (id.Length > 0 ? " id=" + id : "")
                            + " valueLen=" + val.Length
                            + (src.Length > 0 ? " src=" + src : "")
                            + (dis.Length > 0 ? " disabled=" + dis : "")
                            + ">");
                    }
                }

                Log.Info("========== 页面结构导出 结束 ==========");
                SetStatus("页面结构已导出到日志。把「配置与日志目录」里的日志发我看一下。");
            }
            catch (Exception ex)
            {
                Log.Warn("页面结构导出失败: " + ex.Message);
                SetStatus("导出失败：" + ex.Message);
            }
        }

        /// <summary>跑一段可能抛异常、但只想拿值的取值表达式。</summary>
        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch (Exception ex) { return "(异常: " + ex.Message + ")"; }
        }

        private static void OpenInSystemBrowser(string url)        {
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

        /// <summary>
        /// 让下拉框的选中项跟当前网址对上（只改选中，不触发导航）。
        /// 网址不在清单里的话，把选中清成"无"而不是随便选一个 ——
        /// 否则下拉框会显示一个跟输入框内容不符的条目，看着像 bug。
        /// </summary>
        private void SyncUrlComboSelection(string url)
        {
            if (cmbUrl == null) return;

            string want = FieldProfileStore.NormalizeUrl(url);
            if (want.Length == 0) return;

            _loadingUrlList = true;
            try
            {
                ComboBoxItem hit = null;
                foreach (object o in cmbUrl.Items)
                {
                    var item = o as ComboBoxItem;
                    if (item == null) continue;
                    string u = item.Tag as string;
                    if (u == null) continue;
                    if (string.Equals(FieldProfileStore.NormalizeUrl(u), want,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        hit = item;
                        break;
                    }
                }
                if (hit != null) cmbUrl.SelectedItem = hit;
            }
            finally
            {
                _loadingUrlList = false;
            }
        }

        /// <summary>
        /// 刷新"最近用过"的时间戳，供主页下次选默认值。
        /// 用 owner 的方法而不是直接写清单 —— 顺手也会同步旧设置项。
        /// </summary>
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

            // 下拉框如果开着且选的正不是这条，把它对齐过来 ——
            // 用户手打了一个列表里已有的地址时，下拉框不该还停在别处。
            SyncUrlComboSelection(url);

            _autoDoneThisLoad = false;
            _submittedThisLoad = false;
            _sampleTriedThisLoad = false;
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

                    // ⚠️ 只在页面**加载完成之后**才动手。
                    // IE 内核在导航过程中会存在一个"文档对象已经建好、但表单还没解析出来"
                    // 的中间态 —— 那时候枚举 DOM 拿到的是半成品（实测元素数 121，而加载完成后
                    // 是 90）。这份中间态里没有 password 框，于是每次重试都报"没找到密码框"，
                    // 白耗 6 次机会。等 ReadyState=Complete 才是真正的页面。
                    try
                    {
                        if (browser.ReadyState != System.Windows.Forms.WebBrowserReadyState.Complete) return;
                    }
                    catch { return; }

                    if (TryAutoFillAndSubmit(true)) _autoDoneThisLoad = true;
                };
            }
            _retryLeft = 12;   // 页面加载慢时给足机会（每次都会先检查 ReadyState，加载中不算数）
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

            // 每次文档加载完都刷一下页面上「登录成功自动关闭」那个勾选框的实际状态，
            // 让界面和配置保持一致。
            SyncAutoCloseCheckbox();

            // ⚠️ 顺序要紧：这里判定的是**上一轮**填的验证码（页面刚刚跳走了，
            //    说明用户点过登录）。必须排在"重新抠本页的图"之前 ——
            //    一旦开始抠新页面的图，上一轮的那张就被覆盖了，样本也就配不上对。
            MaybeRecordSample();

            // 页面换了，上一轮的图作废，重新抠一次（扣不到就说明本页没有验证码）
            _captchaPollKeepRow = false;
            StartCaptchaPoll(5);

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
        /// 开机自启场景专用的"登录成功即关闭"守护。
        ///
        /// 为什么需要单独一个：网页认证页有验证码，程序不会自动提交 ——
        /// 用户是自己点页面上的「登录」按钮提交的，那时候 AfterSubmit() 根本没被调用。
        /// 所以这里起一个轻量轮询，只要发现"网通了 + 本窗口还开着"就自己退出。
        ///
        /// ⚠️ 只在这个窗口是"开机自启弹出来的"（_startupAutoClose）时才启动，
        ///    手动打开的窗口绝不自作主张关闭。
        /// </summary>
        private void StartAutoCloseGuard()
        {
            // 开机自启 或 自动重连 —— 这两种都是"程序自己开的窗口"，
            // 连上之后都该自己消失。用户手动开的窗口绝不自作主张关闭。
            if (!_startupAutoClose && !_reauthMode) return;

            Thread t = new Thread(delegate()
            {
                // 最多看 5 分钟：太短会让"用户磨蹭着打验证码"的场景失效，
                // 太长又会变成一个常驻线程。5 分钟足够填个验证码了。
                int rounds = 100;   // 100 × 3s = 300s
                for (int i = 0; i < rounds; i++)
                {
                    Thread.Sleep(3000);

                    // 用严格判定：认证前网关返回的 302 劫持页不算"通"。
                    // 旧版用 ProbeInternet（收到任何响应都算通），没认证时也会误判成功。
                    // ⚠️ 2026-09-30：改成调 NetProbe（与主页共用一个判据）。
                    //    原先这里自己抄了一份 ProbeInternetStrict，两处判定一旦不同步，
                    //    就会出现"认证窗口说成功了，主页还显示尚未连接"这种互相打脸。
                    bool netOk = NetProbe.Online(true);   // 这里必须穿透缓存要一个实时答案
                    if (!netOk) continue;

                    Dispatcher.Invoke(new Action(delegate()
                    {
                        SetStatus("网络已连通 —— 认证成功，窗口即将自动关闭。");
                        NotifyOwnerOnline();
                    }));
                    Thread.Sleep(1200);   // 让上面那句话能被看见
                    try { Dispatcher.Invoke(new Action(delegate() { Close(); })); }
                    catch { }
                    return;
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ==================================================================
        // 字段档案：扫描页面 + 按档案填写
        // ==================================================================

        /// <summary>页面上扫到的字段（供字段档案窗口显示 / 编辑）。</summary>
        internal List<FieldProfileStore.FieldProfile> ScanFields()
        {
            List<WinForms.HtmlElement> all = AllOfMainDocument();
            return BuildScanList(all);
        }

        /// <summary>档案保存后调用：清掉本轮的填写标记，让下次重试按新档案来。</summary>
        internal void OnProfileSaved()
        {
            _autoDoneThisLoad = false;
            _submittedThisLoad = false;
            SetStatus("字段档案已保存 —— 点「填表并登录」按新档案填一次。");
            Log.Info("字段档案已保存，等待重新填表");
        }

        /// <summary>打开字段档案窗口。没扫到框就先提示。</summary>
        private void OpenProfileEditor()
        {
            List<WinForms.HtmlElement> all = AllOfMainDocument();
            List<FieldProfileStore.FieldProfile> scanned = BuildScanList(all);

            if (scanned.Count == 0)
            {
                MessageBox.Show(
                    "还没扫到输入框。\n\n先点「打开」把认证页显示出来，"
                    + "确认页面上能看到账号密码框了，再来点「字段档案」。",
                    "字段档案", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            owner.OpenFieldProfileWindow(NormalizedPageUrl(), scanned);
        }

        /// <summary>当前页面用于匹配档案的规范化网址。</summary>
        private string NormalizedPageUrl()
        {
            // 优先用浏览器里真实的地址（可能已经跳到 /login 之类），
            // 拿不到再退回输入框里那个。
            try
            {
                WinForms.HtmlDocument d = browser.Document;
                if (d != null && d.Url != null)
                {
                    string u = d.Url.ToString();
                    if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u;
                }
            }
            catch { }
            return txtUrl.Text;
        }

        /// <summary>
        /// 把页面上的输入控件整理成一份可编辑的字段清单。
        ///
        /// 标签文字怎么来的（按可靠性依次尝试）：
        ///   ① 同一个 &lt;label for="id"&gt; 的文字
        ///   ② 包着这个输入框的 &lt;td&gt; / &lt;div&gt; / &lt;p&gt; 里、除输入框之外的那点文字
        ///      （ASP.NET 那种 table 版式的登录页，标签就是同一格里的文字）
        ///   ③ 输入框自己的 placeholder / title
        ///   ④ 附近的文字节点
        /// 四个都拿不到就留空 —— 界面上会退化成显示 name/id，用户照样能认出来。
        /// </summary>
        private List<FieldProfileStore.FieldProfile> BuildScanList(List<WinForms.HtmlElement> all)
        {
            var result = new List<FieldProfileStore.FieldProfile>();
            if (all == null) return result;

            // 先把标签索引建好：id → 标签文字
            var labelById = new Dictionary<string, string>();
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "label") continue;
                string forId = AttrOf(el, "for");
                if (forId.Length == 0) continue;
                string t = CleanText(SafeInner(el));
                if (t.Length > 0 && !labelById.ContainsKey(forId.ToLowerInvariant()))
                    labelById[forId.ToLowerInvariant()] = t;
            }

            int idx = 0;
            foreach (WinForms.HtmlElement el in all)
            {
                string tag = TagOf(el);
                if (tag != "input" && tag != "select" && tag != "textarea") continue;

                string type = AttrOf(el, "type").ToLowerInvariant();
                // 隐藏域 / 提交按钮 / 文件框不是"要填的字段"
                if (type == "hidden" || type == "submit" || type == "button"
                    || type == "image" || type == "reset" || type == "file") continue;

                // ⚠️ disabled / readonly 的控件不参与编号（2026-10-01 审计确认）：
                //    MatchField 的候选列表本来就把它们排除在外，而这里以前照样计数，
                //    两边的口径不一致 —— 结果是那些靠"序号"兜底的字段会**整体错位一位**，
                //    填到隔壁框里去。
                if (IsDisabled(el)) continue;

                var f = new FieldProfileStore.FieldProfile();
                f.Name = AttrOf(el, "name");
                f.Id = AttrOf(el, "id");
                f.Index = idx++;

                // ---- 标签文字 ----
                string label = "";
                if (f.Id.Length > 0)
                {
                    string k = f.Id.ToLowerInvariant();
                    if (labelById.ContainsKey(k)) label = labelById[k];
                }
                if (label.Length == 0) label = NearestText(all, el);
                if (label.Length == 0) label = AttrOf(el, "placeholder");
                if (label.Length == 0) label = AttrOf(el, "title");

                f.Label = CleanText(label);
                if (f.Label.Length > 20) f.Label = f.Label.Substring(0, 20);

                // ---- 猜一个默认类型（用户可在档案窗口改） ----
                string blob = (f.Label + " " + f.Name + " " + f.Id + " " + AttrOf(el, "placeholder")).ToLowerInvariant();

                if (type == "password" || LooksLikePwd(blob))
                {
                    f.Kind = FieldProfileStore.KindAccount;
                    f.AccountField = "password";
                }
                else if (LooksLikeCaptcha(blob))
                {
                    f.Kind = FieldProfileStore.KindCaptcha;
                }
                else if (LooksLikeIdentity(blob))
                {
                    f.Kind = FieldProfileStore.KindAccount;
                    f.AccountField = "user2";
                }
                else if (LooksLikeIsp(blob))
                {
                    // 运营商/ISP 这类：默认交还给页面自己的默认值，别乱填
                    f.Kind = FieldProfileStore.KindIgnore;
                }
                else if (LooksLikeAccount(blob))
                {
                    f.Kind = FieldProfileStore.KindAccount;
                    f.AccountField = "user";
                }
                else
                {
                    // 认不出来的文本框：默认不管它 —— 宁可少填，也不要填错框
                    f.Kind = FieldProfileStore.KindIgnore;
                }

                if (tag != "input") f.Kind = FieldProfileStore.KindFixed;   // select / textarea 用固定值
                result.Add(f);
            }

            // 如果扫出来的框里**一个账号框都没有**，把第一个普通文本框兜底当账号框 ——
            // 否则用户打开档案会看到一个全"不填"的清单，不知道该改哪里。
            bool hasAccount = false;
            foreach (FieldProfileStore.FieldProfile f in result)
            {
                if (f.Kind == FieldProfileStore.KindAccount && f.AccountField == "user") hasAccount = true;
            }
            if (!hasAccount)
            {
                foreach (FieldProfileStore.FieldProfile f in result)
                {
                    if (f.Kind == FieldProfileStore.KindIgnore) { f.Kind = FieldProfileStore.KindAccount; f.AccountField = "user"; hasAccount = true; break; }
                }
            }
            if (!hasAccount && result.Count > 1)
            {
                // 连一个可当账号的都没有，就把第一个非密码框顶上
                foreach (FieldProfileStore.FieldProfile f in result)
                {
                    if (f.AccountField != "password" && f.Kind != FieldProfileStore.KindCaptcha)
                    {
                        f.Kind = FieldProfileStore.KindAccount; f.AccountField = "user"; break;
                    }
                }
            }
            return result;
        }

        private static string SafeInner(WinForms.HtmlElement el)
        {
            try { return el.InnerText ?? ""; } catch { return ""; }
        }

        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (c == '\r' || c == '\n' || c == '\t') { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(c);
            }
            string r = sb.ToString().Trim();
            while (r.IndexOf("  ", StringComparison.Ordinal) >= 0) r = r.Replace("  ", " ");
            return r;
        }

        /// <summary>
        /// 输入框"挨着的那点文字"。
        ///
        /// 做法：从输入框出发往上走最多 4 层，在每一层里找**文字节点**，
        /// 取第一个像标签的（不含输入框内容、长度合理）。
        /// ASP.NET 的 table 版式登录页，标签就是同一格里的文字，这样能捞到；
        /// 捞不到就返回空，不会硬编一个假标签出来。
        /// </summary>
        private static string NearestText(List<WinForms.HtmlElement> all, WinForms.HtmlElement el)
        {
            try
            {
                WinForms.HtmlElement cur = el;
                for (int depth = 0; depth < 4 && cur != null; depth++)
                {
                    WinForms.HtmlElement parent = null;
                    try { parent = cur.Parent; } catch { }
                    if (parent == null) break;

                    string t = FirstTextNode(parent, el);
                    if (t.Length > 0) return t;
                    cur = parent;
                }
            }
            catch { }
            return "";
        }

        /// <summary>在容器里找第一个像"标签文字"的直接文字节点（跳过输入框自身）。</summary>
        private static string FirstTextNode(WinForms.HtmlElement container, WinForms.HtmlElement skip)
        {
            try
            {
                WinForms.HtmlElementCollection kids = container.Children;
                if (kids == null) return "";
                // 只扫直接子节点里的文字节点；文本常挂在 span/label 上，也一并认
                foreach (WinForms.HtmlElement kid in kids)
                {
                    if (kid == skip) continue;
                    string tag = TagOf(kid);
                    if (tag == "input" || tag == "select" || tag == "textarea" || tag == "script" || tag == "style") continue;

                    string inner = SafeInner(kid);
                    string clean = CleanText(inner);
                    if (clean.Length == 0) continue;

                    // 一个容器里塞了太多字（多半是整段说明而不是标签）就跳过
                    if (clean.Length > 20) continue;
                    // 里面还有输入框的，说明是更大的容器，不算标签
                    bool hasInput = false;
                    try
                    {
                        foreach (WinForms.HtmlElement kk in kid.Children)
                        {
                            string kt = TagOf(kk);
                            if (kt == "input" || kt == "select" || kt == "textarea") { hasInput = true; break; }
                        }
                    }
                    catch { }
                    if (hasInput) continue;

                    return clean;
                }
            }
            catch { }
            return "";
        }

        private static bool LooksLikePwd(string blob)
        {
            return blob.IndexOf("pwd", StringComparison.Ordinal) >= 0
                || blob.IndexOf("pass", StringComparison.Ordinal) >= 0
                || blob.IndexOf("密码", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeCaptcha(string blob)
        {
            return blob.IndexOf("captcha", StringComparison.Ordinal) >= 0
                || blob.IndexOf("checkcode", StringComparison.Ordinal) >= 0
                || blob.IndexOf("verifycode", StringComparison.Ordinal) >= 0
                || blob.IndexOf("验证码", StringComparison.Ordinal) >= 0
                || blob.IndexOf("textboxcc", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeIdentity(string blob)
        {
            return blob.IndexOf("guitid", StringComparison.Ordinal) >= 0
                || blob.IndexOf("student", StringComparison.Ordinal) >= 0
                || blob.IndexOf("stuid", StringComparison.Ordinal) >= 0
                || blob.IndexOf("stuno", StringComparison.Ordinal) >= 0
                || blob.IndexOf("学工号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("学号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("工号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("identity", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeIsp(string blob)
        {
            return blob.IndexOf("isp", StringComparison.Ordinal) >= 0
                || blob.IndexOf("运营商", StringComparison.Ordinal) >= 0
                || blob.IndexOf("operator", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeAccount(string blob)
        {
            return blob.IndexOf("ssoacc", StringComparison.Ordinal) >= 0
                || blob.IndexOf("account", StringComparison.Ordinal) >= 0
                || blob.IndexOf("username", StringComparison.Ordinal) >= 0
                || blob.IndexOf("userid", StringComparison.Ordinal) >= 0
                || blob.IndexOf("user", StringComparison.Ordinal) >= 0
                || blob.IndexOf("账号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("用户名", StringComparison.Ordinal) >= 0;
        }

        /// <summary>只取主文档的元素（档案填写不走 iframe，避免填错区域）。</summary>
        private List<WinForms.HtmlElement> AllOfMainDocument()
        {
            try
            {
                WinForms.HtmlDocument d = browser.Document;
                if (d == null) return new List<WinForms.HtmlElement>();
                return All(d);
            }
            catch { return new List<WinForms.HtmlElement>(); }
        }

        /// <summary>页面上有没有图形验证码（按元素属性判断，供档案填写路径复用）。</summary>
        private static bool PageHasCaptcha(List<WinForms.HtmlElement> all)
        {
            return HasCaptcha(all);
        }

        /// <summary>
        /// 按字段档案填表。
        ///
        /// 返回 true = 档案命中并处理过（不管提交成没成功）。
        /// 返回 false = 这个网址没有档案（或档案里一个可用字段都没有）。
        ///
        /// 匹配顺序（属性 → 标签 → 顺序）见 FieldProfileStore 的注释。
        /// </summary>
        private bool FillByProfile(List<WinForms.HtmlElement> all, string url, bool auto)
        {
            FieldProfileStore.Profile profile = FieldProfileStore.GetForUrl(url);
            if (profile == null || profile.Fields.Count == 0) return false;

            ConfigStore.Account acc = SelectedAccount();
            int filled = 0;
            var done = new List<string>();

            foreach (FieldProfileStore.FieldProfile f in profile.Fields)
            {
                if (!f.Enabled) continue;

                // 验证码：只登记，不填
                if (f.Kind == FieldProfileStore.KindCaptcha) { done.Add("验证码（你填）"); continue; }
                if (f.Kind == FieldProfileStore.KindIgnore) continue;

                string value = FieldProfileStore.ResolveValue(f, acc);
                if (value == null || value.Length == 0) continue;

                WinForms.HtmlElement el = MatchField(all, f);
                if (el == null)
                {
                    Log.Info("字段档案：没在页面上找到字段「" + f.Label + "」（name=" + f.Name + " id=" + f.Id + "）");
                    continue;
                }

                // ⚠️ SetValue 现在返回 bool（写失败就不算"已填"）—— 2026-10-01 审计确认：
                //    以前它内部用空 catch 吞异常，外面照样 filled++，
                //    于是日志和界面都声称"已填 N 项"，页面上其实还是空的。
                if (SetValue(el, value))
                {
                    filled++;
                    done.Add((f.Label.Length > 0 ? f.Label : f.Id) + " = " + MaskForLog(f, value));
                }
            }

            if (filled == 0)
            {
                Log.Info("字段档案命中（" + profile.Url + "）但一个字段都没填上 —— 页面结构可能变了");
                SetStatus("这个网址有字段档案，但一个框都没对上 —— 页面可能改版了，重新扫一次档案吧。");

                // ⚠️ 这里**必须返回 false**（2026-10-01 审计确认）：
                //    以前返回 true，调用方就认为"档案这条路成功了"直接收工，
                //    于是下面那段"按 name/id 猜字段"的启发式兜底**永远不会执行** ——
                //    等于档案一旦对不上，整条自动填表就成了死路（页面改版后尤其明显）。
                return false;
            }

            Log.Info("字段档案填表：" + string.Join("、", done.ToArray()));
            ClearFieldErrors(all);

            // 页面上有验证码就"填好 + 提交一次"，让表单活起来（见 TryAutoFillAndSubmit 里的说明）
            bool hasCc = PageHasCaptcha(all);
            if (hasCc && !_submittedThisLoad)
            {
                _submittedThisLoad = true;
                SubmitFirstForm(all);
            }

            if (lblFound != null)
            {
                lblFound.Text = "档案命中：「" + profile.Url + "」已填 " + filled + " 项 —— "
                    + string.Join("、", done.ToArray());
            }

            if (hasCc)
            {
                SetStatus("档案已生效 —— 上面几项都填好了。验证码需要你自己看一眼填进去，然后点页面上的「登录」。");
                return true;
            }

            WinForms.HtmlElement submit = FindSubmit(all);
            if (submit == null)
            {
                SetStatus("档案已生效，但没找到「登录」按钮 —— 请手动点一下页面上的登录。");
                return true;
            }

            SetStatus("档案已生效，正在提交…");
            ClickElement(submit);
            AfterSubmit();
            return true;
        }

        private static string MaskForLog(FieldProfileStore.FieldProfile f, string v)
        {
            if (string.Equals(f.AccountField, "password", StringComparison.OrdinalIgnoreCase)) return "••••••";

            // ⚠️ 账号和附加账号（学工号）也要掩码（2026-10-01 审计确认）。
            //    原来它们原样进日志，真实日志里已经出现了完整学号和手机号。
            //    而使用说明恰恰教用户"出问题就把日志发给懂电脑的人" —— 一份日志里
            //    带着学号和手机号在外部流转，这个泄露面实在没必要。
            if (string.Equals(f.AccountField, "user", StringComparison.OrdinalIgnoreCase)) return MaskAccount(v);
            if (string.Equals(f.AccountField, "user2", StringComparison.OrdinalIgnoreCase)) return MaskAccount(v);

            return v;
        }

        /// <summary>
        /// 账号半掩码：留头尾各 2 位，中间打点（如 18••••••47）。
        /// 不够 5 位就全掩 —— 太短的字符串露两头等于没掩。
        /// </summary>
        private static string MaskAccount(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            if (v.Length <= 4) return new string('•', v.Length);
            return v.Substring(0, 2) + new string('•', v.Length - 4) + v.Substring(v.Length - 2);
        }

        /// <summary>按档案把一个字段对到页面上的元素：属性 → 标签 → 顺序。</summary>
        private static WinForms.HtmlElement MatchField(List<WinForms.HtmlElement> all, FieldProfileStore.FieldProfile f)
        {
            var candidates = new List<WinForms.HtmlElement>();
            foreach (WinForms.HtmlElement el in all)
            {
                string tag = TagOf(el);
                if (tag != "input" && tag != "select" && tag != "textarea") continue;
                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type == "hidden" || type == "submit" || type == "button"
                    || type == "image" || type == "reset" || type == "file") continue;
                if (IsDisabled(el)) continue;
                candidates.Add(el);
            }

            // ① 按 name / id 精确匹配
            if (!string.IsNullOrEmpty(f.Id))
            {
                foreach (WinForms.HtmlElement el in candidates)
                {
                    if (!string.IsNullOrEmpty(AttrOf(el, "id"))
                        && string.Equals(AttrOf(el, "id"), f.Id, StringComparison.OrdinalIgnoreCase)) return el;
                }
            }
            if (!string.IsNullOrEmpty(f.Name))
            {
                foreach (WinForms.HtmlElement el in candidates)
                {
                    if (!string.IsNullOrEmpty(AttrOf(el, "name"))
                        && string.Equals(AttrOf(el, "name"), f.Name, StringComparison.OrdinalIgnoreCase)) return el;
                }
            }

            // ② 按扫描时记下的顺序（最可靠的兜底 —— 同一个页面顺序不会变）
            if (f.Index >= 0 && f.Index < candidates.Count)
            {
                WinForms.HtmlElement el = candidates[f.Index];
                string id = AttrOf(el, "id");
                string name = AttrOf(el, "name");
                // 顺序位置上的元素 name/id 要能对上（都为空也算"对得上"，有些页面就没有）
                bool ok = true;
                if (!string.IsNullOrEmpty(f.Id) && !string.IsNullOrEmpty(id)
                    && !string.Equals(id, f.Id, StringComparison.OrdinalIgnoreCase)) ok = false;
                if (!ok && !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(name)
                    && !string.Equals(name, f.Name, StringComparison.OrdinalIgnoreCase)) ok = false;
                if (ok) return el;
            }

            return null;
        }

        /// <summary>提交页面上的第一个表单（没指定具体控件时用）。</summary>
        private static void SubmitFirstForm(List<WinForms.HtmlElement> all)
        {
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "form") continue;
                SubmitForm(el);
                return;
            }
        }

        /// <summary>
        /// 通用启发式填表：不针对任何一所学校，靠页面结构推断。
        /// 找不到就明确告诉用户，不做危险操作。
        /// 返回 true = 这次真的找到了密码框并处理过（不管成没成功提交）。
        ///
        /// ⚠️ 有字段档案时**优先走档案** —— 用户亲手教过的，比任何启发式都准。
        /// </summary>
        /// <summary>
        /// 当前页面是否还停在认证地址的域上。
        ///
        /// ⚠️ 为什么必须有这道闸（2026-10-01 审计确认）：
        ///   填表前从来不检查"当前页面是不是认证页本身"。而认证页一旦重定向到运营商广告页，
        ///   或者有人在宿舍网里 ARP / DNS 劫持了认证地址，程序就会把**账号密码填进对方的表单，
        ///   还替用户把提交按钮点了** —— 更麻烦的是"断线自动重连"这条路整个过程零交互，
        ///   用户完全无感知（不是他自己点的，也没有窗口跳出来）。
        ///
        /// 放行规则：同域，或互为子域都放行
        ///   （认证页跳到同主域的 SSO 子域是常见且正常的，比如 sso.xxx 与 xxx）。
        ///   取不到当前地址 / 认证网址没填 / 解析失败时**一律放行** ——
        ///   宁可漏拦，也绝不能误拦正常用户（拦错了就是填不上，用户完全不知道该怎么办）。
        /// </summary>
        private bool CurrentPageIsTrusted()
        {
            try
            {
                string stored = (txtUrl.Text ?? "").Trim();
                if (stored.Length == 0) return true;

                string current = null;
                try
                {
                    if (browser != null && browser.Url != null) current = browser.Url.ToString();
                }
                catch { }
                if (string.IsNullOrEmpty(current)) return true;

                Uri cur, want;
                if (!Uri.TryCreate(current, UriKind.Absolute, out cur)) return true;
                if (!Uri.TryCreate(stored, UriKind.Absolute, out want)) return true;

                string ch = cur.Host ?? "";
                string wh = want.Host ?? "";
                if (ch.Length == 0 || wh.Length == 0) return true;

                if (string.Equals(ch, wh, StringComparison.OrdinalIgnoreCase)) return true;
                if (ch.EndsWith("." + wh, StringComparison.OrdinalIgnoreCase)) return true;
                if (wh.EndsWith("." + ch, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return true; }
        }

        private bool TryAutoFillAndSubmit(bool auto)
        {
            // ⚠️ 这道检查必须放在**最开头** —— 在任何"取档案 / 猜字段"动作之前。
            //    放这里才能一处覆盖全部入口：页面加载后自动填、12 次补填定时器、
            //    断线自动重连、以及用户手动点「填表并登录」。
            //    拦住时不填值、不提交、不点任何按钮。
            if (!CurrentPageIsTrusted())
            {
                Log.Warn("自动填表已拦截：当前页面与认证地址不同域，防止凭据被填进陌生页面");
                SetStatus("当前页面与认证网址不是同一个网站，已停止自动填写"
                    + "（防止账号密码被填进陌生页面）。如果确认这个页面没问题，"
                    + "把上面的认证网址改成当前页面地址，再点一次。");
                return false;
            }

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
            // ⚠️ 这条也**每轮加载只记一次**。
            //    它比下面那条「没有密码框」刷得还凶 —— 补填定时器每 1.2 秒来一次、
            //    每轮最多 12 次，实测 2026-09-30 一天占了整个日志的 **27.2%（169/622 行）**，
            //    是全部日志里最吵的一条（同一秒里最多连着写 6 遍）。
            //    文档个数在一轮加载内不会变，记一遍足够。
            //
            // ⚠️⚠️ 2026-10-01 修：这里原先用的是 `_noPwdLoggedToken`，而且**漏了赋值**
            //     —— 判断了个寂寞，实际上一次都没节流掉。补赋值时又发现不能复用同一个
            //     token（会把下面「没有密码框」永久压掉），所以改成独立的 `_docsLoggedToken`。
            if (_docsLoggedToken != _loadToken)
            {
                _docsLoggedToken = _loadToken;
                Log.Info("认证页填表：可访问文档 " + docs.Count + " 个");
            }

            // ① 优先用字段档案（用户亲手教的，最准）
            try
            {
                if (FieldProfileStore.HasForUrl(NormalizedPageUrl()))
                {
                    if (FillByProfile(AllOfMainDocument(), NormalizedPageUrl(), auto))
                    {
                        _autoDoneThisLoad = true;
                        return true;
                    }
                    Log.Info("字段档案存在但对不上，回退到启发式识别");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("按档案填表失败，回退到启发式: " + ex.Message);
            }

            // ② 没有档案 / 档案没命中 → 老启发式
            foreach (WinForms.HtmlDocument doc in docs)
            {
                // ⚠️ 只枚举一次 DOM。WinForms.HtmlElement 是 COM 包装对象，每枚举一次都会新建一批，
                // 分两次枚举拿到的对象用 == 比较必然不等 —— 靠下标去定位账号框会失效。
                List<WinForms.HtmlElement> all = All(doc);

                WinForms.HtmlElement pw = FindPasswordInput(all);
                if (pw == null)
                {
                    // 记下来 —— 用户报"填不上"时，这条日志能直接分清是
                    // "页面还没渲染" / "结构不认识" / "在跨域 iframe 里"。
                    //
                    // ⚠️ 但**每轮加载只记一次**。
                    //    这条原先是无条件写的，而补填定时器每 1.2 秒重试一次、一轮最多 12 次，
                    //    加上 DocumentCompleted 也会调一次 —— 实测一天下来这条占了整个日志的
                    //    **17.9%（108/604 行）**，真正的信息被淹掉了（2026-09-30 冒烟测试发现）。
                    //    诊断信息重复 12 遍不会让它更有用，只会把别的线索挤没。
                    if (_noPwdLoggedToken != _loadToken)
                    {
                        _noPwdLoggedToken = _loadToken;
                        Log.Info("认证页填表：这个文档里没有密码框 —— " + DescribeDoc(all));
                    }
                    continue;
                }

                // 密码框是**按名字猜**出来的（页面上没有 type="password"）→ 记一笔。
                // 平时不写（能正常识别就没什么可说），只在真的用了降级路径时记，
                // 以后有人报"填到别的框里去了"，这条日志能直接指出问题出在哪。
                // 复用同一个节流标记，避免每轮加载都刷一遍。
                if (_noPwdLoggedToken != _loadToken)
                {
                    string pwType = AttrOf(pw, "type").ToLowerInvariant();
                    if (!"password".Equals(pwType, StringComparison.OrdinalIgnoreCase))
                    {
                        _noPwdLoggedToken = _loadToken;
                        Log.Info("认证页填表：密码框是按名字认出来的（页面上没有 type=password）"
                            + " —— type=\"" + pwType + "\" name=\"" + AttrOf(pw, "name")
                            + "\" id=\"" + AttrOf(pw, "id") + "\"");
                    }
                }

                WinForms.HtmlElement us = FindUserInput(all, pw);
                if (us == null)
                {
                    SetStatus("找到了密码框，但认不出账号框（这个页面的结构比较特别）。请在页面上手动填一次。");
                    return true;
                }

                SetValue(us, user);
                SetValue(pw, pass);
                // 页面给了提示（"请输入用户名/密码"这类）就清掉，免得看着像还没填
                ClearFieldErrors(all);

                // 附加账号（学工号）—— 只有页面上确实存在第二个"身份号"输入框，且有值时才填。
                // 这是后加的，老账号没填 User2，这里会跳过，不影响原有行为。
                string user2 = GetUser2();
                WinForms.HtmlElement us2 = null;
                if (user2.Length > 0)
                {
                    us2 = FindSecondUserInput(all, us, pw);
                    if (us2 != null) SetValue(us2, user2);
                }

                WinForms.HtmlElement submit = FindSubmit(all);

                // ⚠️ 有图形验证码就**只填不提交** —— 见 HasCaptcha 的注释。
                // 验证码那格是空的，硬提交必然失败；而学校普遍对"连续登录失败"有次数限制，
                // 反复失败可能直接把账号锁一段时间。宁可让用户自己点一下。
                if (HasCaptcha(all))
                {
                    // 光填进去不够 —— ASP.NET WebForms 的验证码是服务端生成的，
                    // 必须把表单**提交**给服务器才能校验。所以：填好 ➜ 按一下回车触发一次提交，
                    // 服务器返回"验证码错误!"。这样既不绕过验证码（还是得人来填），
                    // 又让用户填完验证码直接在页面上点「登录」就能过 —— 不会卡在"必须先提交一次"。
                    if (!_submittedThisLoad)
                    {
                        _submittedThisLoad = true;
                        SubmitForm(pw);
                    }

                    SetStatus("这个页面有图形验证码 —— 账号密码已经帮你填好，"
                        + "验证码需要你自己看一眼填进去，然后点页面上的「登录」按钮。");
                    if (lblFound != null)
                    {
                        lblFound.Text = "识别结果：账号框「" + Describe(us) + "」 密码框「" + Describe(pw) + "」"
                            + (us2 != null ? " 附加账号框「" + Describe(us2) + "」" : "")
                            + "；页面有图形验证码 → 已填好，但不自动提交";
                    }
                    return true;
                }

                if (submit == null)
                {
                    SetStatus("账号密码已填好，但没找到「登录」按钮 —— 请手动点一下页面上的登录。");
                    return true;
                }

                SetStatus("已识别登录框，正在提交…");
                if (lblFound != null)
                {
                    lblFound.Text = "识别结果：账号框「" + Describe(us) + "」 密码框「" + Describe(pw) + "」"
                        + (us2 != null ? " 附加账号框「" + Describe(us2) + "」" : "")
                        + " 提交按钮「" + Describe(submit) + "」";
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

        /// <summary>附加账号（学工号）。没填就返回空串 —— 调用方据此跳过第二个框。</summary>
        private string GetUser2()
        {
            ConfigStore.Account a = SelectedAccount();
            if (a != null && !string.IsNullOrEmpty(a.User2)) return a.User2;
            return "";
        }

        /// <summary>
        /// 提交之后隔几秒探一次网 + 读一次页面上的错误提示，判断这一步的结果。
        ///
        /// ⚠️ 这里**不能**再用 ProbeInternet() 判"成功"：
        ///    它只要收到任何响应就算通 —— 而认证前网关会返回一个 302 跳认证页，
        ///    也就是说"压根没登录成功"它也报 true，用户会看到"认证成功"但还是上不了网。
        ///    改用 ProbeInternetStrict()（只认 204 / 空响应体）。
        /// </summary>
        private void AfterSubmit()
        {
            int token = _loadToken;
            Thread t = new Thread(delegate()
            {
                try
                {
                    Thread.Sleep(5000);
                    if (token != _loadToken) return;   // 期间又导航过了，这次结果作废

                    bool netOk = NetProbe.Online(true);   // 实时确认，读缓存没意义

                    // 顺便读一下页面上服务器的原话（如"验证码错误!"）——
                    // 拿不到就空着，不影响主流程。
                    string pageErr = "";
                    try
                    {
                        Dispatcher.Invoke(new Action(delegate()
                        {
                            if (token != _loadToken) return;
                            try
                            {
                                List<WinForms.HtmlElement> all = All(browser.Document);
                                pageErr = ReadPageError(all);
                            }
                            catch { }
                        }));
                    }
                    catch { }

                    Dispatcher.Invoke(new Action(delegate()
                    {
                        if (token != _loadToken) return;

                        if (netOk)
                        {
                            SetStatus("已提交，网络已连通 —— 认证成功。");
                            // 开机自启 + 设置了"登录后自动关闭"：连通了就自己退出，
                            // 别让一个已经完成任务的窗口杵在桌面上。
                            if (_startupAutoClose || _reauthMode)
                            {
                                try { Close(); }
                                catch { }
                            }
                        }
                        else if (pageErr.Length > 0)
                        {
                            SetStatus("没登上去 —— 页面上写着：「" + pageErr + "」。"
                                + (pageErr.IndexOf("验证码", StringComparison.Ordinal) >= 0
                                    ? "把验证码填进去再点一次页面上的「登录」。"
                                    : "看一眼下面的网页，改完再登一次。"));
                            if (lblFound != null) lblFound.Text = "服务器返回：" + pageErr;
                        }
                        else
                        {
                            SetStatus("已提交，但还没连通。可能是账号密码不对，或者页面还停在登录页 —— 看一眼下面的网页。");
                        }
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 判断"网络是否真的通了"。
        ///
        /// ⚠️ 2026-09-30：本方法原本在这里自己实现了一遍严格探测，现已**废弃并移除**，
        ///    改为统一调用 NetProbe.Online()。原因：
        ///    · 主页也要判断"上没上网"，两处各写一份必然漂移；
        ///    · 一旦漂移就会出现"认证窗口报成功、主页仍显示尚未连接"这种自相矛盾的现象；
        ///    · NetProbe 额外带了 5 秒结果缓存，主页每秒轮询也不会疯狂发请求。
        ///    判据本身没变，仍然是它当年那套：204 / 200 且响应体极短才算真通。
        /// </summary>
        private static bool ProbeInternetStrict()
        {
            return NetProbe.Online(true);
        }

        /// <summary>
        /// 认证通了之后知会主窗口一声，让它立刻把状态条切到"已认证上网"。
        /// 不等主窗口自己那轮轮询（最多等 5 秒），用户关了认证窗回头就能看到正确结果。
        /// </summary>
        private void NotifyOwnerOnline()
        {
            try
            {
                MainWindow mw = Owner as MainWindow;
                if (mw == null) return;
                mw.OnWebAuthOnline();
            }
            catch { }
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

        /// <summary>
        /// 判断页面上有没有图形验证码。
        ///
        /// 为什么必须检测、而且检测到就**不能自动提交**：
        ///   验证码的设计目的就是"证明这一步是人在操作"。
        ///   程序把账号密码填好就提交 → 验证码那格是空的 → 提交必然失败。
        ///   而学校普遍对"连续登录失败"有次数限制，反复失败可能把账号锁一段时间。
        ///   所以宁可停手，让用户自己看一眼验证码、填进去、点登录。
        ///
        /// 验证码的识别：**做，但只做到"填候选"，绝不提交**（C9 第二阶段）。
        ///   ① 「自动填写」按钮认出的 4 位只写进输入框，点「登录」永远是用户的事。
        ///      本项目从未、也不会出现提交验证码的代码路径 —— 这是对用户的承诺，
        ///      也是验收时要专门审查的一条。
        ///   ② 识别本身不破坏"人在操作"这件事：认不准就一位都不填（见 CaptchaOcr.MinMargin），
        ///      认出来也明确提示"可能错，请核对"。真正的提交动作仍然只有人手能做。
        ///   ③ 注意区分：这里 HasCaptcha 是"填账号密码那条自动路径"的闸门 ——
        ///      那条路径**依然只填不提交**，与「自动填写」按钮是两码事。
        /// </summary>
        private static bool HasCaptcha(List<WinForms.HtmlElement> all)
        {
            string[] keys = new string[]
            {
                "captcha", "verifycode", "verify_code", "checkcode", "check_code",
                "validatecode", "validate_code", "vcode", "authcode", "imgcode",
                "randcode", "seccode", "verificationcode", "yzm", "yanzhengma",
                "captchaimg", "codeimg"
            };

            foreach (WinForms.HtmlElement el in all)
            {
                string tag = TagOf(el);
                if (tag != "img" && tag != "input" && tag != "canvas"
                    && tag != "span" && tag != "div" && tag != "label" && tag != "a") continue;

                string blob = (AttrOf(el, "id") + " " + AttrOf(el, "name") + " "
                    + AttrOf(el, "src") + " " + AttrOf(el, "alt") + " "
                    + AttrOf(el, "class") + " " + AttrOf(el, "title") + " "
                    + AttrOf(el, "onclick")).ToLowerInvariant();

                if (blob.IndexOf("验证码", StringComparison.Ordinal) >= 0) return true;

                foreach (string k in keys)
                {
                    if (blob.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
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

        /// <summary>
        /// 密码框的推断顺序：
        ///   ① 标准做法：type="password"（绝大多数认证页都是这样）
        ///   ② 退一步：name / id 里带 pwd / pass 的输入框
        ///
        /// ⚠️ 为什么必须有第 ② 层 ——
        ///     2026-09-30 学校换了新版认证页，它的密码框**不是** type="password"：
        ///     实测 `input=4 password=0`，整页一个 password 类型的框都没有。
        ///     只认 type 的话这里必然返回 null，而调用方拿到 null 会
        ///     **直接放弃整轮自动填表**（见 1612 行附近），
        ///     于是新认证页在"用户没配过字段档案"时**一个字都填不进去**。
        ///     实测 9-30 一整天记了 109 次"这个文档里没有密码框"。
        ///
        ///     但它的 name 是 `SSOPWD` —— 名字里明写着 PWD，程序只是没去认。
        ///
        /// 第 ② 层只在前一层完全找不到时才启用，所以**不会影响原本能正常识别的页面**。
        /// 万一猜错（把密码填进别的框），用户一眼能看出来，且可用「字段档案」手动纠正。
        /// </summary>
        private static WinForms.HtmlElement FindPasswordInput(List<WinForms.HtmlElement> all)
        {
            // ① 标准做法：type="password"
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                if (!"password".Equals(AttrOf(el, "type"), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsDisabled(el)) continue;
                return el;
            }

            // ② 降级：按 name / id 的名字猜
            string[] keys = new string[] { "pwd", "pass", "password", "mima" };
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                if (IsDisabled(el)) continue;

                // 排除明显不可能是密码的控件类型
                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type == "checkbox" || type == "radio" || type == "submit"
                    || type == "button" || type == "reset" || type == "file"
                    || type == "image" || type == "hidden") continue;

                string id = (AttrOf(el, "id") + " " + AttrOf(el, "name")).ToLowerInvariant();
                if (id.Length == 0) continue;
                foreach (string k in keys)
                {
                    if (id.IndexOf(k, StringComparison.Ordinal) >= 0) return el;
                }
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

        /// <summary>
        /// 找"第二个账号框"（学工号那类）。
        ///
        /// 背景：有些学校的网页认证页要**两个号** —— 比如桂电信科那个门户，表单里
        /// 同时有「学(工)号」和「上网账号」两个文本输入框，只填一个登不上去。
        ///
        /// 认法（依次尝试，命中即返回）：
        ///   ① name/id 里带学工号特征的（guitid / studentid / stuid / schoolid / jobid / xh / sno）
        ///   ② 页面上一共有 ≥2 个可用文本输入框时，取"除主账号框之外、离密码框最近的那个"
        ///
        /// ⚠️ 只在调用方确认**有第二个账号值**（User2 非空）时才会走到这里；
        ///    找不到就返回 null，绝不硬塞，避免把值填到错误的框里。
        /// </summary>
        private static WinForms.HtmlElement FindSecondUserInput(List<WinForms.HtmlElement> all,
            WinForms.HtmlElement mainUser, WinForms.HtmlElement pw)
        {
            string[] keys = new string[]
            {
                "guitid", "studentid", "studentno", "stuid", "stuno", "schoolid", "jobid",
                "xh", "sno", "xuehao", "gonghao", "idcard", "identity"
            };

            // ① 按特征词找
            foreach (WinForms.HtmlElement el in all)
            {
                if (el == mainUser) continue;
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

            // ② 兜底：找密码框之前、且不是主账号框的另一个文本输入框
            List<WinForms.HtmlElement> textInputs = new List<WinForms.HtmlElement>();
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                string type = AttrOf(el, "type").ToLowerInvariant();
                if (type != "" && type != "text" && type != "tel") continue;
                if (IsDisabled(el)) continue;
                if (el == mainUser) continue;
                textInputs.Add(el);
            }

            // 只有一个"其他"文本框时才敢认（多了就分不清，宁可不动手）
            if (textInputs.Count == 1) return textInputs[0];
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
        /// 给输入框赋值，返回**是否真的写进去了**。
        ///
        /// 注意必须**同时**改 value 属性和触发 change 事件 ——
        /// 有些页面靠 onchange 才把值同步到内部变量，只赋值不触发等于没填。
        ///
        /// ⚠️ 2026-10-01 起返回 bool：以前这里从头到尾都是空 catch，
        ///    写失败（元素已失效、页面已经导航走）也静默通过，调用方照样累加"已填 N 项"，
        ///    结果日志和界面都在说填好了，页面上却是空的。
        /// </summary>
        private static bool SetValue(WinForms.HtmlElement el, string value)
        {
            bool wrote = false;
            try { el.SetAttribute("value", value); wrote = true; }
            catch { }

            // 下面几个只是"通知页面值变了"，失败不影响"值有没有写进去"这个事实
            try { el.InvokeMember("focus"); } catch { }
            try { el.InvokeMember("onchange"); } catch { }
            try { el.InvokeMember("onkeyup"); } catch { }
            try { el.InvokeMember("blur"); } catch { }

            return wrote;
        }

        private static void ClickElement(WinForms.HtmlElement el)
        {
            try { el.InvokeMember("click"); return; }
            catch { }

            // click 点不动就退回"直接提交表单"
            SubmitForm(el);
        }

        /// <summary>
        /// 直接让元素所在的表单提交。
        ///
        /// 为什么需要单独一个：有些提交按钮（或二次校验的链接）在内嵌 IE 内核里
        /// InvokeMember("click") 会静默失败 —— 页面什么都不发生，用户就会觉得
        /// "点你们应用的按钮没反应"。直接提交表单是可靠的兜底。
        /// </summary>
        private static void SubmitForm(WinForms.HtmlElement el)
        {
            if (el == null) return;
            try
            {
                // ⚠️ 先从这个元素**向上**找它所属的 form（2026-10-01 审计确认）：
                //    以前不分青红皂白提交 Document 里的**第一个** form ——
                //    页面上只要还有别的表单（搜索框、友情链接、隐藏表单……），提交的就是错的，
                //    表现是"点登录没反应"或者"跳到了搜索页"。
                WinForms.HtmlElement cur = el;
                int guard = 0;
                while (cur != null && guard++ < 20)
                {
                    if (string.Equals(TagOf(cur), "form", StringComparison.OrdinalIgnoreCase))
                    {
                        cur.InvokeMember("submit");
                        return;
                    }
                    try { cur = cur.Parent; }
                    catch { cur = null; }
                }

                // 兜底：连所属 form 都找不到（结构太怪），还按老办法提交第一个
                WinForms.HtmlElementCollection forms = el.Document.GetElementsByTagName("form");
                if (forms != null && forms.Count > 0) forms[0].InvokeMember("submit");
            }
            catch (Exception ex)
            {
                Log.Warn("提交登录表单失败（请手动点页面上的登录）: " + ex.Message);
            }
        }

        /// <summary>
        /// 清掉页面上"请输入用户名/密码"这类校验提示。
        ///
        /// ⚠️ 只改**浏览器里看到的**文字，不代替服务端校验 ——
        /// 真正的账号密码对不对，仍然由服务器说了算。
        /// 做这件的唯一原因是：用户已经填好之后，页面上那句红字会让用户以为没填上。
        /// </summary>
        private static void ClearFieldErrors(List<WinForms.HtmlElement> all)
        {
            string[] keys = new string[] { "请输入用户名", "请输入密码", "请输入账号", "请输入验证码", "不能为空" };
            foreach (WinForms.HtmlElement el in all)
            {
                string t = TagOf(el);
                if (t != "span" && t != "div" && t != "p" && t != "label") continue;
                string txt = "";
                try { txt = (el.InnerText ?? "").Trim(); }
                catch { }
                if (txt.Length == 0 || txt.Length > 20) continue;
                foreach (string k in keys)
                {
                    if (txt.IndexOf(k, StringComparison.Ordinal) >= 0)
                    {
                        try { el.InnerText = ""; } catch { }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 找页面上的错误提示文本（ASP.NET 的 validation-summary-errors 那类）。
        ///
        /// 为什么要读它：光靠"网络通不通"判断登录结果有延迟、也不精确 ——
        /// 服务器其实已经明说了"验证码错误!"。读出来能立刻告
        /// 诉用户到底卡在哪一步，不用他自己猜。
        /// </summary>
        private static string ReadPageError(List<WinForms.HtmlElement> all)
        {
            string best = "";
            foreach (WinForms.HtmlElement el in all)
            {
                string cls = AttrOf(el, "class").ToLowerInvariant();
                bool looksLikeError =
                    cls.IndexOf("validation", StringComparison.Ordinal) >= 0
                    || cls.IndexOf("error", StringComparison.Ordinal) >= 0
                    || cls.IndexOf("message", StringComparison.Ordinal) >= 0;
                if (!looksLikeError) continue;

                string txt = "";
                try { txt = (el.InnerText ?? "").Trim().Replace("\r", " ").Replace("\n", " "); }
                catch { }
                if (txt.Length == 0 || txt.Length > 40) continue;
                if (best.Length == 0) best = txt;
            }
            return best;
        }
    }
}
