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
        // 「保存登录信息」提示行（页面有那个勾时才显示）。见 UpdateSaveLoginHint。
        private TextBlock lblSaveHint;
        // 学工号缺失警告行（页面有两个身份框、但我们没有第二个号时显示）。见 UpdateIdentityHint。
        private TextBlock lblIdentityHint;

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

        /// <summary>
        /// 当前这张验证码图在页面上的 **src**（就是它的"身份"）。
        ///
        /// ⚠️ 2026-10-13 新增。海辰报的 bug：「一键登录填的验证码是刷新前的」。
        ///    取图以前只带回像素、不带身份，所以程序无法回答一个关键问题：
        ///    **我现在手里这张，还是页面上正在显示的那张吗？**
        ///    从"认出一张图"到"真的点提交"之间，页面随时可能换图
        ///    （回传重载 / 用户点换一张），只要换了，提交上去的必然是旧答案。
        ///    有了这个 src，提交前对一次就能拦住（见 CaptchaIdentityChanged）。
        /// </summary>
        private string _captchaSrc = "";

        /// <summary>
        /// 当前这张验证码的**完整身份** = 图的 src + 图像内容的指纹。
        ///
        /// ⚠️ 2026-10-13 第二次修（海辰："填入验证码好了一些，但**有概率**会填入刷新前的"）。
        ///    上一版只用 src 当身份，有个漏洞：**回传后页面重载，img 的 src 常常一模一样
        ///    （门户的 &lt;img src="/CheckCode"&gt; 不带时间戳），可服务器返回的却是新图**。
        ///    于是"src 没变"被我错判成"还是那张图"，继续拿旧像素去认、去填 ——
        ///    填进去的就是上一张的答案。这就是那个"有概率"。
        ///    加上内容指纹之后，"src 相同但图变了"也能认出来。
        /// </summary>
        private string _captchaKey = "";

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

        // ------------------------------------------------------------------
        // 全自动登录（2026-10-12 海辰要求：一键登录要真的连上，不要人来点）
        //
        // 改之前的行为（他报的 bug）：页面上只要有验证码，填表路径填完账号密码就
        // **停手不提交**；而且 _submittedThisLoad 一旦为 true，再点也什么都不做
        // —— 表现就是"点了一键登录没反应，还得自己去点网页里的登录"。
        //
        // 现在：填账号密码 → 认验证码 → 填进框 → 点页面上的「登录」→ 判结果 →
        //       没过就换一张重试（最多 AutoAuthMaxAttempts 次）。
        // ------------------------------------------------------------------

        /// <summary>自动提交开关（settings.txt 的 PortalAutoSubmit，默认开）。关掉就退回"只填不提交"。</summary>
        private bool _autoSubmit = true;

        /// <summary>本次自动登录已经提交了几次。</summary>
        private int _autoAuthAttempts = 0;

        /// <summary>自动登录流程是否在进行中（决定"新图到手后要不要接着再试一次"）。</summary>
        private bool _autoAuthRunning = false;

        /// <summary>
        /// 本轮是不是已经"停手"过了（只喊一次，避免多个驱动者各喊一遍）。
        /// 开新一轮（用户点「一键登录」/ 重新认证）时才归零。
        /// </summary>
        private bool _autoAuthGaveUp = false;

        /// <summary>最近一次自动提交时填进验证码框的内容（提示语要用）。</summary>
        private string _autoAuthLastFill = "";

        /// <summary>上一次提交时的验证码身份（src+指纹），用来判"新验证码是不是已经在提交后回传到了"。</summary>
        private string _lastSubmittedCaptchaKey = "";

        /// <summary>提交后等结果的看门狗：万一这次点击没触发页面回传，不能就这么挂着。</summary>
        private DispatcherTimer _autoAuthWatchdog;

        /// <summary>
        /// 自动登录最多提交几次。
        ///
        /// ⚠️ 为什么是 3 而不是"一直试到成功"：每次失败学校端都记一次登录失败，
        ///    而学校普遍对"连续失败"有次数限制（可能直接把账号锁一段时间）。
        ///    3 次之内大概率能过（整图识别率约 1/3~1/2，3 次约七成把握），
        ///    剩下那三成让用户自己补 —— 比冒"账号被锁"的风险划算。
        /// </summary>
        private const int AutoAuthMaxAttempts = 3;

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

        /// <summary>
        /// 「自动提交开着但这一页没认出验证码框」记到第几轮了。
        ///
        /// ⚠️ 2026-10-13 新增。存在的理由：海辰日志里 10:47:25 那次"填完表之后
        ///    一个字都没有"，既没有提交日志、也没有任何报错 —— 排查时完全看不出
        ///    程序在哪个分支上停了。这条日志就是用来把那个分支照亮的：
        ///    如果 `_autoSubmit` 开着却打出这一行，说明**验证码框没被认出来**，
        ///    流程掉进了"只填不提交"的老路径（那正是他说的"只填信息不点登录"）。
        ///    和上面两条一样按轮节流，不会刷屏。
        /// </summary>
        private int _noCcLoggedToken = -1;

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

        /// <summary>
        /// 程序正在同步勾选框显示（不是用户的点击）—— 用来防止
        /// "设 IsChecked → 触发 Checked 事件 → 又去写盘"的回环。
        /// </summary>
        private bool _syncingAutoClose = false;

        /// <summary>
        /// 这个窗口是**程序自己开的**吗（开机自启 / 后台自动重连），而不是用户点开的。
        ///
        /// ⚠️ 2026-10-15 新增：这个区别决定"能不能自己关掉"。
        ///    用户手动打开认证页时通常本来就通着网，若允许自动关窗守护启动，
        ///    窗口会开出来几秒就被自己关掉 —— 他根本没来得及看。
        ///    （这个坑是本次改"记住那个勾"时新引入的，写在这里免得再踩。）
        /// </summary>
        private bool _programOpened = false;

        /// <summary>
        /// 用户在设置里明确选了「窗口保持打开」（PortalStartupBehavior=keep）。
        ///
        /// ⚠️ 单靠 _startupAutoClose=false 拦不住自动关窗守护 ——
        ///    "自动重连"拉起来的窗口是 _reauthMode=true，守护线程也认它。
        ///    开机那条路若复用了这种窗口，用户选"别关"就会被无视。见 SetStartupAutoClose。
        /// </summary>
        private bool _keepOpenBySetting = false;

        /// <summary>
        /// 自动关窗守护线程是不是已经起过了。
        /// ⚠️ 2026-10-15：复用"后台隐形窗口"那条新路径上，同一个窗口会被
        ///    SetReauthMode 与 SetStartupAutoClose 各调一次 StartAutoCloseGuard ——
        ///    不加这道闸就会起两个线程同时轮询、连上后重复 Close。
        /// </summary>
        private bool _autoCloseGuardRunning = false;

        /// <summary>由 MainWindow 在开机自启场景调用，设置"登录成功后自动关闭"。</summary>
        internal void SetStartupAutoClose(bool autoClose)
        {
            _startupAutoClose = autoClose;

            // ⚠️ 2026-10-15：走到这里说明窗口是**程序自己开的**（开机自启那条路），
            //    可以起"连上就自己消失"的守护。用户手动打开的窗口绝不能起 ——
            //    他很可能本来就通着网、只是想看一眼页面，一起守护就会开出来几秒被关掉。
            _programOpened = true;

            // ⚠️ 2026-10-15：用户在设置里选了「窗口保持打开」时，必须把**已经在跑**的
            //    自动关窗守护也按住。开机那条路上，窗口可能是从"后台隐形"复用来的
            //    （它带着 _reauthMode=true，守护线程早就起来了），光把 _startupAutoClose
            //    置 false 拦不住它 —— 它只认 _reauthMode。用户选了"别关"，就得真的别关。
            _keepOpenBySetting = !autoClose;

            _syncingAutoClose = true;
            try { if (chkAutoClose != null) chkAutoClose.IsChecked = autoClose; }
            finally { _syncingAutoClose = false; }

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
        /// <summary>
        /// 后台静默模式：窗口摆到**屏幕外**，用户完全看不见，但它仍然"在显示状态"——
        /// 这一点是关键：内嵌的 IE 控件只有在真正显示着的时候才会渲染和联网，
        /// 单纯隐藏窗口（Visibility=Hidden）会让页面加载不出来。
        /// 本项目的自测一直就是这么跑的（-4000,-4000），所以这条路径是验证过的。
        ///
        /// 2026-10-12 海辰要求：「我要打游戏，测试自己在后台跑，别弹窗」。
        /// </summary>
        internal void SetBackgroundSilent(bool on)
        {
            _backgroundSilent = on;
            if (!on) return;
            try
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -4000;
                Top = -4000;
                ShowInTaskbar = false;
                ShowActivated = false;
            }
            catch { }
        }

        /// <summary>
        /// 把"后台隐形"的窗口搬回屏幕内，交还给用户。
        ///
        /// ⚠️ 2026-10-15 新增，修海辰报的「开机静默登录不生效，得我自己打开认证页才动」：
        ///    开机时"静默自动重连"会先开一个摆到屏幕外的隐形窗口（用户看不见但 IE 在
        ///    正常联网），2 秒后开机自启那条路再要开窗时，发现 _webAuthWindow 已经
        ///    被占用了 —— 老代码只 Activate() 一下就返回，于是**用户屏幕上永远不出现
        ///    任何窗口**。v2.4.7 时隐形窗口在设 Owner 那行抛异常没开成，所以没事；
        ///    v2.4.8 修好那个异常之后它真的显示出来了，反而把可见窗口顶掉。
        ///
        ///    同一个坑还会让用户「手动点打开认证页却没反应」—— 那条路也是先看
        ///    _webAuthWindow 在不在。所以这里给一个"把它拉回来"的出口，
        ///    两条路都用它，而不是傻等或假装激活。
        /// </summary>
        internal void BringBackFromBackground()
        {
            try
            {
                _backgroundSilent = false;

                // 摆回主屏工作区正中 —— 不引用 owner（静默启动时主窗口是隐藏的，
                // 拿它当参照会算出屏幕外的坐标）。
                WindowStartupLocation = WindowStartupLocation.Manual;
                Rect wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Top + (wa.Height - Height) / 2;
                if (Left < wa.Left) Left = wa.Left;
                if (Top < wa.Top) Top = wa.Top;

                ShowInTaskbar = true;
                ShowActivated = true;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
            catch (Exception ex)
            {
                Log.Warn("认证窗口拉回前台失败: " + ex.Message);
            }
        }

        /// <summary>是不是"后台静默"跑着的（决定了收尾时要不要发托盘气泡告知）。</summary>
        private bool _backgroundSilent = false;
        internal bool IsBackgroundSilent() { return _backgroundSilent; }

        internal void SetReauthMode(bool reauth)
        {
            _reauthMode = reauth;
            if (reauth)
            {
                // 2026-10-12：文案跟着行为改 —— 现在会自动识别并提交，不再是"你填验证码再点登录"。
                SetStatus(_autoSubmit
                    ? "检测到掉线，已自动打开认证页 —— 正在自动登录（账号密码 + 验证码都会自动填并提交）。"
                    : "检测到掉线，已自动打开认证页。账号密码已帮你填好，填一下验证码再点登录即可。");
                // 连通后自己也关掉 —— 和开机自启同样的道理：
                // 任务完成了就不该留个窗口杵在那儿。用户在打游戏时尤其明显。
                StartAutoCloseGuard();
            }
        }

        /// <summary>
        /// 用户手动勾/取消「登录成功后自动关闭」时调用。
        ///
        /// ⚠️ 2026-10-15 修（海辰报的「第二次打开页面那个勾就取消了」）：
        ///    这里原先只改内存里的 _startupAutoClose，**从不写盘** —— 窗口一关，
        ///    用户的选择就没了，下次打开是全新窗口、勾选框回到出厂默认。
        ///    全项目搜过，chkAutoClose 从来没有落过盘。
        ///    现在把它写回 PortalStartupBehavior（和设置窗口里那个单选钮**同一个键**），
        ///    两个入口从此共用一个存盘值，不会再各说各话。
        /// </summary>
        private void ApplyAutoCloseToggle(bool on)
        {
            if (_syncingAutoClose) return;      // 程序自己在同步显示，不是用户的动作

            _startupAutoClose = on;
            _keepOpenBySetting = !on;

            // 写盘：勾 = auto（登录后自动关），不勾 = keep（保持打开）
            try { owner.SetPortalKeepOpen(!on); }
            catch (Exception ex) { Log.Warn("保存「登录后行为」失败: " + ex.Message); }

            if (on)
            {
                SetStatus("已开启：检测到网络真的连通后，本窗口会自动关闭。");
                StartAutoCloseGuard();
            }
            else
            {
                SetStatus("已关闭自动关闭 —— 登录成功后本窗口会留着。");
            }
        }

        /// <summary>
        /// 按存盘的设置初始化「登录成功后自动关闭」那个勾 —— 用户手动打开认证页时调。
        ///
        /// ⚠️ 2026-10-15 新增，正面回应海辰报的「我点过后第二次打开页面那个选项就会取消打勾」：
        ///    以前这个勾选框的初值恒为 false（写死），既不看存盘、也不写盘，
        ///    所以用户勾了它，关掉窗口再打开就"自己变回没勾"。
        ///    现在勾选框直接反映 PortalStartupBehavior（与设置窗口里那个单选钮同一个键）。
        ///
        /// ⚠️ 只能在**用户看得见**的那两条路（手动打开 / 开机自启）里调。
        ///    绝不能在构造函数里无条件调 —— 后台自动重连也会构造本窗口，
        ///    那条路的窗口必须"连上就自己消失"；若被设置了 keep，它就会一直赖在
        ///    屏幕外不走，还会把名字占住（下次重连会说"认证页已经开着"而不再重试）。
        /// </summary>
        internal void InitAutoCloseFromSetting()
        {
            bool autoClose;
            try { autoClose = owner.PortalStartupBehavior() != "keep"; }
            catch { autoClose = true; }

            _startupAutoClose = autoClose;
            _keepOpenBySetting = !autoClose;
            SyncAutoCloseCheckbox();

            // ⚠️ 这里**故意不起**自动关窗守护。
            //    那个守护是"程序自己开的窗口，连上就自己消失"用的；用户手动打开认证页时
            //    他很可能本来就通着网（只是想看一眼页面），一起守护就会**开出来 3 秒
            //    就被自己关掉** —— 那比不记住勾还糟。
            //    勾选框本身的语义（"登录成功后自动关闭本窗口"）由提交成功那条路负责
            //    （见 AfterSubmitAuto 里的 _startupAutoClose 判断），不靠这个轮询。
            //    _keepOpenBySetting 仍然要设：若接手的正是那个后台隐形窗口，
            //    它已经跑着的守护会因为这一位而停下（用户选了"别关"就该真的别关）。
        }

        /// <summary>
        /// 用户接管了这个窗口（它原本是后台自动重连开的隐形窗口，现在被拉回前台给人看）。
        ///
        /// ⚠️ 2026-10-15：必须把"这是程序自己开的窗口"这两个标记都清掉 ——
        ///    否则它身上那个**已经在跑**的自动关窗守护不会停，用户刚看到窗口
        ///    就会被它关掉（他现在多半本来就通着网，下一次轮询立刻命中）。
        ///    接管之后它就是一个"用户看着的窗口"，该由勾选框决定去留。
        /// </summary>
        internal void MarkUserTookOver()
        {
            _reauthMode = false;
            _programOpened = false;
        }

        /// <summary>
        /// 把界面上的勾选框状态同步成当前配置（防止界面与实际不一致）。
        ///
        /// ⚠️ 2026-10-15：赋值前必须置 _syncingAutoClose —— 直接设 IsChecked 会触发
        ///    Checked/Unchecked 事件，而 ApplyAutoCloseToggle 现在会**写盘**，
        ///    那条回环会把配置反复覆盖（用户的选择被"显示同步"这个动作抹掉）。
        /// </summary>
        private void SyncAutoCloseCheckbox()
        {
            if (chkAutoClose == null) return;
            try
            {
                if (chkAutoClose.IsChecked != _startupAutoClose)
                {
                    _syncingAutoClose = true;
                    try { chkAutoClose.IsChecked = _startupAutoClose; }
                    finally { _syncingAutoClose = false; }
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
                    if (_autoAuthWatchdog != null) _autoAuthWatchdog.Stop();
                    // 窗口没了就别再让后台线程去动 UI（那些回调都会检查这个标记）
                    _autoAuthRunning = false;
                    _autoAuthWaitNext = false;
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

            btnLogin = MainWindow.MakePrimaryButton("一键登录", OnLoginClick);
            btnLogin.FontSize = 12;
            btnLogin.Margin = new Thickness(16, 0, 0, 0);
            // 2026-10-12：这个按钮从"填表并登录"改名成"一键登录" —— 因为它现在真的会
            // 一条龙做完（填账号密码 → 认验证码 → 填 → 点登录 → 失败换一张重试）。
            // 名字得对得上行为，否则用户会以为还得自己点网页里的登录。
            btnLogin.ToolTip = "自动填账号密码 + 自动识别验证码 + 自动提交（失败会换一张重试，最多 3 次）";
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

            // ---------- 第 5.5 行：「保存登录信息」提示（页面没这个勾时整行折叠） ----------
            // ⚠️ 为什么要有它（2026-10-15）——
            //    学校认证页新加了「保存登录信息」勾选框（id=MainContent_CheckBoxSaveMe）。
            //    勾上并登录成功后，**学校服务器会下发一个 7 天有效的持久 cookie**
            //    （实测名字叫 GUITNETSSO2026_Cookie），之后学校就会自己把账号密码回填进页面。
            //
            // ⚠️⚠️ 关键：那个 cookie 是**按浏览器**存的，不是全校通用的。
            //    实测：海辰在 Edge 里勾过 -> Edge 有这条 cookie；
            //          我们的窗口用的是 IE 内核 -> 一条都没有（err=259）。
            //    所以"学校已经帮我们保存了"这句话**对我们窗口不成立** ——
            //    用户必须在**我们这个窗口里**勾一次，这张卡才会进我们的卡包。
            //
            // ⚠️ 程序**绝不替用户勾**（2026-10-15 海辰拍板选的方案 B）——
            //    勾它等于把上网账号信息存到学校服务器上，该由用户自己知情决定。
            //    这里只做"告诉他有这么个东西"。
            lblSaveHint = MakeHint("");
            lblSaveHint.Foreground = new SolidColorBrush(Theme.Accent);
            lblSaveHint.Visibility = Visibility.Collapsed;
            statusBox.Children.Add(lblSaveHint);

            // ---------- 第 5.6 行：学工号缺失警告（没问题时整行折叠） ----------
            // ⚠️ 为什么单独一个标签、而不是拼进 SetStatus ——
            //    SetStatus 在后面的分支里会被反复覆盖（"正在提交…"/"换一张重试…"），
            //    警告拼进去会被冲掉，用户就看不到了。这条必须**一直留着**。
            lblIdentityHint = MakeHint("");
            lblIdentityHint.Foreground = new SolidColorBrush(Theme.Warn);
            lblIdentityHint.Visibility = Visibility.Collapsed;
            statusBox.Children.Add(lblIdentityHint);

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
            // 全自动登录的开关（默认开，2026-10-12 海辰要求）。
            // ⚠️ 必须在这里读 —— SetReauthMode 会用 _autoSubmit 决定那句状态文案，
            //    而 MainWindow 是在构造完之后才调 SetReauthMode 的。
            try { _autoSubmit = CaptchaAssist.AutoSubmitEnabled(ConfigStore.LoadSettings()); }
            catch { _autoSubmit = true; }

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
        // 「保存登录信息」提示（2026-10-15）
        //
        // 背景（实测，见交接文档 2026-10-15 那节）：
        //   学校认证页新加了 <input id="MainContent_CheckBoxSaveMe" type="checkbox">。
        //   勾上并登录成功后，学校下发一个 **7 天有效的持久 cookie**
        //   （实测名 GUITNETSSO2026_Cookie，过期 10-22），之后学校自己就把
        //   账号密码回填进页面 —— 用户不用再手填，我们也就不用再操心学工号。
        //
        // ⚠️ 但它**按浏览器**存：Edge 里有，我们的 IE 内核窗口里没有（都实测过）。
        //    所以必须让用户**在我们这个窗口里**勾一次，卡才办到我们这边。
        //
        // 方案 B（海辰 2026-10-15 拍板）：**只提示，绝不替用户勾** ——
        //   勾它 = 把上网账号信息存到学校服务器上，该由用户知情后自己决定。
        // ==================================================================

        /// <summary>页面上有没有「保存登录信息」那种勾选框。只读，不改任何东西。</summary>
        internal static WinForms.HtmlElement FindSaveLoginCheckbox(List<WinForms.HtmlElement> all)
        {
            foreach (WinForms.HtmlElement el in all)
            {
                if (TagOf(el) != "input") continue;
                if (!"checkbox".Equals(AttrOf(el, "type"), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsDisabled(el)) continue;

                string blob = (AttrOf(el, "id") + " " + AttrOf(el, "name") + " " + NearestText(all, el))
                    .ToLowerInvariant();
                if (LooksLikeSaveLogin(blob)) return el;
            }
            return null;
        }

        /// <summary>
        /// 从 id/name/旁边的文字判断"这是不是保存登录信息那个勾"。
        ///
        /// ⚠️ 为什么不能只认硬编码的 CheckBoxSaveMe ——
        ///    别的学校可能用别的 id；而写死一个 id 属于"把某所学校的实现当通用规则"，
        ///    换一所学校就失效。这里按语义特征认，认不出就不显示提示（宁可不说）。
        /// </summary>
        internal static bool LooksLikeSaveLogin(string blob)
        {
            // 中文：保存登录信息 / 记住我 / 记住密码 / 自动登录 / 下次自动登录 …
            if (blob.IndexOf("保存", StringComparison.Ordinal) >= 0
                && blob.IndexOf("登录", StringComparison.Ordinal) >= 0) return true;
            if (blob.IndexOf("记住", StringComparison.Ordinal) >= 0) return true;

            // 英文：saveme / savepwd / rememberme / keepmealive / autologin …
            if (blob.IndexOf("saveme", StringComparison.Ordinal) >= 0) return true;
            if (blob.IndexOf("savepwd", StringComparison.Ordinal) >= 0) return true;
            if (blob.IndexOf("savelogin", StringComparison.Ordinal) >= 0) return true;
            if (blob.IndexOf("remember", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ==================================================================
        // 学工号缺失提醒（2026-10-15）
        //
        // 真实场景（海辰学校，实测）：
        //   认证页有两个身份框 —— 「学(工)号」SSOGUITID 和 「上网账号」SSOACC。
        //   而账号里「附加账号（学工号）」那格**是空的**，
        //   FindSecondUserInput 只在 User2 非空时才被调用 ——
        //   于是第二个框**一个字都不填**，界面却说"档案已生效/已填好"，
        //   提交必然被学校拒（两个号只填了一个）。用户完全不知道为什么。
        //
        // 所以这里：认出一个"看着是身份号、但现在空着、我们又没值可填"的框，
        // **明确告诉用户去哪填**。绝不自己瞎猜一个值填进去
        //（跟着本项目一贯的红线：宁可少填，也不要填错框）。
        // ==================================================================

        /// <summary>
        /// 第二个身份号输入框的特征词。
        /// ⚠️ 与 FindSecondUserInput 里那份列表**保持同步** ——
        ///    两处漂移会导致"程序认得出、却提醒不到"（本项目踩过判据分叉的坑）。
        /// </summary>
        internal static readonly string[] IdentityKeys = new string[]
        {
            "guitid", "studentid", "studentno", "stuid", "stuno", "schoolid", "jobid",
            "xh", "sno", "xuehao", "gonghao", "idcard", "identity"
        };

        /// <summary>
        /// 【纯判据】这个输入框是不是"该有值、现在却空着"的身份号框？
        ///
        /// ⚠️ 为什么抽成纯函数（只吃字符串，不碰 HtmlElement）——
        ///    判据留在"遍历真实 DOM"的循环里就没法被自测断言，
        ///    而"该提醒却没提醒"这种事恰恰只能靠断言发现（本项目
        ///    ButtonCand 的注释里写过同样的理由，这里沿用）。
        /// </summary>
        internal static bool IsBlankIdentityField(
            string tag, string type, string idOrName, string value, bool disabled)
        {
            if (disabled) return false;
            if (!"input".Equals(tag, StringComparison.OrdinalIgnoreCase)) return false;

            string t = (type ?? "").ToLowerInvariant();
            if (t != "" && t != "text" && t != "tel" && t != "search") return false;

            string blob = (idOrName ?? "").ToLowerInvariant();
            if (blob.Length == 0) return false;

            bool looksIdentity = false;
            foreach (string k in IdentityKeys)
            {
                if (blob.IndexOf(k, StringComparison.Ordinal) >= 0) { looksIdentity = true; break; }
            }
            if (!looksIdentity) return false;

            // 已经有值了（学校凭 cookie 回填了、或用户自己填了）-> 不用提醒。
            // ⚠️ 只认 getAttribute("value")：学校回填是在服务端 HTML 里写 value 属性，
            //    所以这条路一定能看到；不需要（也不该）去读 DOM 的当前值。
            return (value ?? "").Trim().Length == 0;
        }

        /// <summary>
        /// 【纯判据】该不该弹"学工号没填"的警告？
        ///
        /// 两个条件同时成立才提醒：
        ///   ① 我们自己**没有**值可填（账号里「附加账号」是空的）
        ///   ② 页面上**确实有一个**空着的身份号框
        /// 只要有值可填（待会儿会填上），或页面上压根没这种框，都不该打扰用户。
        /// </summary>
        internal static bool ShouldWarnMissingIdentity(bool hasOurValue, bool pageHasBlankIdentityField)
        {
            return !hasOurValue && pageHasBlankIdentityField;
        }

        /// <summary>
        /// 找一个"像是第二个身份号、当前却是空的"输入框。
        /// 找不到（或它已经有值）就返回 null —— 调用方据此决定要不要提醒。
        /// </summary>
        internal static WinForms.HtmlElement FindEmptyIdentityInput(
            List<WinForms.HtmlElement> all, WinForms.HtmlElement mainUser, WinForms.HtmlElement pw)
        {
            foreach (WinForms.HtmlElement el in all)
            {
                if (el == mainUser || el == pw) continue;

                string idName = AttrOf(el, "id") + " " + AttrOf(el, "name");
                if (!IsBlankIdentityField(TagOf(el), AttrOf(el, "type"), idName,
                        AttrOf(el, "value"), IsDisabled(el)))
                {
                    continue;
                }
                return el;
            }
            return null;
        }

        /// <summary>按当前页面刷新「学工号缺失」警告。不需要提醒时整行折叠。</summary>
        private void UpdateIdentityHint(List<WinForms.HtmlElement> all, WinForms.HtmlElement mainUser, WinForms.HtmlElement pw)
        {
            if (lblIdentityHint == null) return;

            // 我们自己有值可填 -> 待会儿会填上，不用提醒
            string user2 = GetUser2();
            WinForms.HtmlElement missing = (user2.Length > 0)
                ? null
                : FindEmptyIdentityInput(all, mainUser, pw);

            if (!ShouldWarnMissingIdentity(user2.Length > 0, missing != null))
            {
                lblIdentityHint.Visibility = Visibility.Collapsed;
                return;
            }

            string label = NearestText(all, missing);
            if (label.Length == 0) label = AttrOf(missing, "id");
            if (label.Length > 16) label = label.Substring(0, 16);

            lblIdentityHint.Text = "⚠️ 页面上的「" + label + "」还空着，而账号里没存这一项 —— "
                + "两个号只填一个登不上。请点右边「管理账号」，把它填进「附加账号（可空）」，再回来登录。";
            lblIdentityHint.Visibility = Visibility.Visible;

            Log.Warn("学工号缺失：「" + label + "」（" + Describe(missing)
                     + "）现在是空的，而账号的「附加账号」没填 —— 已提示用户去管理账号里补。");
        }

        /// <summary>
        /// 按当前页面刷新「保存登录信息」提示。
        /// 页面上没这个勾 -> 整行折叠（绝大多数学校页面不会有，等于没加东西）。
        /// </summary>
        private void UpdateSaveLoginHint(List<WinForms.HtmlElement> all)
        {
            if (lblSaveHint == null) return;

            WinForms.HtmlElement cb = FindSaveLoginCheckbox(all);
            if (cb == null)
            {
                lblSaveHint.Visibility = Visibility.Collapsed;
                return;
            }

            // 已经勾着了 -> 改成"已生效"的说法，别继续劝他勾
            bool alreadyChecked = AttrTrue(cb, "checked");
            lblSaveHint.Text = alreadyChecked
                ? "已勾选「保存登录信息」—— 下次学校会自己把账号密码填好，你只要管验证码。"
                : "提示：页面上那个「保存登录信息」勾上再登录，以后学校会自己帮你记住账号密码"
                  + "（信息存在学校服务器上，是否勾选由你决定；本程序不会替你勾）。";
            lblSaveHint.Visibility = Visibility.Visible;
        }

        // ==================================================================
        // 验证码辅助：抠图 / 放大 / 换一张 / 采集样本
        //
        // 三条红线写在 CaptchaAssist.cs 的类注释里，这里只重复最要命的一条：
        // **绝不自己带 cookie 重新请求 /CheckCode** —— 门户把答案绑在 session 上，
        // 重请求会把页面正在显示的那张图作废，用户照图填必然错。
        // 所以这里所有取图动作都只针对"页面已经加载好的那张图"。
        // ==================================================================

        /// <summary>
        /// 图上"现在这张验证码"的完整身份（src + 内容指纹）。**只读**：不改 src、不发请求、不动状态。
        ///
        /// 为什么必须带内容：门户回传后 img 的 src 常常跟上一张**完全一样**，
        /// 但服务器已经换了图。只比 src 会把新图误判成"还是那张"，
        /// 于是继续用旧像素去认、去填 —— 填进去的就是上一张的答案。
        /// </summary>
        private string ProbeCaptchaKey()
        {
            try
            {
                WinForms.HtmlDocument doc = browser == null ? null : browser.Document;
                if (doc == null) return "";
                doc.InvokeScript("eval", new object[] { CaptchaAssist.BuildExtractJs() });
                object ret = doc.InvokeScript("__cnhCaptchaCap");
                string raw = ret == null ? "" : ret.ToString();

                int w, h;
                string b64, err, src;
                if (!CaptchaAssist.TryParseShot(raw, out w, out h, out b64, out err, out src)) return "";
                return MakeCaptchaKey(src, b64);
            }
            catch { return ""; }
        }

        /// <summary>拼"图的身份"：src + 内容指纹。</summary>
        private static string MakeCaptchaKey(string src, string b64)
        {
            return (src ?? "") + "#" + ContentHash(b64 ?? "");
        }

        /// <summary>
        /// 内容指纹（FNV-1a 64 位）。不用 MD5/SHA 是为了不引入 System.Security.Cryptography ——
        /// 这里只要"变了能看出来"，不需要抗碰撞。
        /// </summary>
        private static string ContentHash(string s)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        /// <summary>
        /// 手里这张验证码，是不是已经被页面换掉了（**按内容判**）。
        ///
        /// 判据故意取得"宁可放过、不可错杀"：
        ///   · 手上没有身份 → **不算变**（无从比较，别把流程卡死）
        ///   · 当前读不到（脚本异常 / 页面上暂时没图）→ **不算变**（同上）
        ///   · 两边都有值且不同 → 才算变了
        /// ⚠️ 千万别把"读不到"当成"变了" —— 那会把正常流程永久卡在"等新图"上。
        /// </summary>
        private bool CaptchaIdentityChanged()
        {
            if (_captchaKey.Length == 0) return false;
            string now = ProbeCaptchaKey();
            if (now.Length == 0) return false;
            return !string.Equals(now, _captchaKey, StringComparison.Ordinal);
        }

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
            string b64, err, src;
            if (!CaptchaAssist.TryParseShot(raw, out w, out h, out b64, out err, out src))
            {
                // 「页面上本来就没有验证码」「图还没加载完」都是正常情况，不记日志
                bool normal = err.IndexOf("no-img", StringComparison.Ordinal) >= 0
                           || err.IndexOf("not-ready", StringComparison.Ordinal) >= 0;
                if (!normal && _captchaPollLeft <= 1) Log.Warn("验证码取图失败: " + err);
                return false;
            }

            // ⚠️⚠️「同一张图」不等于「新图」（2026-10-13 修）：
            //    轮询每 0.7 秒来一次，同一张图会被反复抠出来。
            //    以前这里每一次都当成"新验证码到手" —— 于是：
            //      ① 日志里一串"验证码已抠出"，看起来就像"验证码在疯狂刷新"（海辰的原话）；
            //      ② 更糟的是每次都会把 `_autoAuthWaitNext` 清掉并接力重试，
            //         等于让重试流程拿**还没换的旧图**接着跑 → 填的是上一张的答案。
            //    ⚠️ 判"是不是同一张"必须用 **src + 内容指纹**，不能只用 src：
            //       门户回传后 img 的 src 常常跟上一张一模一样，但服务器换了图。
            //       只比 src 会把新图误判成"还是那张"，于是继续用旧像素去认
            //       —— 这就是海辰说的"**有概率**会填入刷新前的验证码"。
            string key = MakeCaptchaKey(src, b64);
            if (key == _captchaKey && _captchaPng != null) return true;

            try
            {
                byte[] png = Convert.FromBase64String(b64);
                _captchaPng = png;
                _captchaSrc = src;
                _captchaKey = key;
                _captchaTyped = "";

                imgCaptcha.Source = BitmapFrame.Create(
                    new System.IO.MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                rowCaptcha.Visibility = Visibility.Visible;
                SetCaptchaHint("看不清就点「换一张」。");

                // 新的一张图 = 一次新的采集机会
                _sampleTriedThisLoad = false;
                StartCaptchaTypedWatch();

                Log.Info("验证码已抠出（原图 " + w + "×" + h + "）");

                // 自动登录的重试流程在等新图 —— 图到手了就接着走下一次
                if (_autoAuthWaitNext)
                {
                    _autoAuthWaitNext = false;
                    ScheduleAutoAuthNext();
                }
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
                _captchaSrc = "";
                _captchaKey = "";
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

                string fill = RecognizeCaptchaCandidate();

                if (fill.Length == 0)
                {
                    SetCaptchaHint(_lastOcrResult != null
                        ? CaptchaOcr.DescribeForUser(_lastOcrResult)
                        : "识别不可用，请手动填写。");

                    // 日志要把"没切出字符"和"认出来但没把握"分开说 ——
                    // 旧版一律写"置信度不足"，害得排查的人（就是我）往错的方向找。
                    if (_lastOcrResult != null)
                    {
                        int unread = CaptchaOcr.UnreadableCount(_lastOcrResult);
                        Log.Info("验证码识别：未填入 —— "
                                 + (unread > 0 ? ("有 " + unread + " 位没切出字符")
                                               : "结果不是 4 位字母数字")
                                 + "（识别到 \"" + _lastOcrResult.Text + "\"，达标 "
                                 + _lastOcrResult.AcceptedCount + "/4，最小 margin="
                                 + _lastOcrResult.MinMargin.ToString("0.###") + "）");
                    }
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
                SetCaptchaHint(CaptchaOcr.DescribeForUser(_lastOcrResult));
                Log.Info("验证码识别：已填入候选 " + fill
                         + "（达标 " + _lastOcrResult.AcceptedCount + "/4，最小 margin="
                         + _lastOcrResult.MinMargin.ToString("0.###") + "）");
            }
            catch (Exception ex)
            {
                SetCaptchaHint("识别出错：" + ex.Message + "，请手动填写。");
                Log.Warn("验证码识别异常: " + ex.Message);
            }
        }

        /// <summary>最近一次识别的完整结果（给提示语和日志用）。</summary>
        private CaptchaOcr.Result _lastOcrResult;

        /// <summary>
        /// 识别当前这张验证码图，返回"该填进去的 4 位"（认不出返回空串）。
        ///
        /// ⚠️ 抽出来是给两条路径共用的（「自动填写」按钮 + 全自动登录）。
        ///    两处各写一份必然漂移 —— 这正是本项目反复踩过的'判据分叉'。
        ///    失败图留证也统一在这里做：2026-10-03 海辰说"我看得很清楚，它却说无法识别"，
        ///    而当时失败的那张图没留下来，只能靠日志猜，方向一开始就偏了。
        /// </summary>
        private string RecognizeCaptchaCandidate()
        {
            _lastOcrResult = null;
            if (_captchaPng == null || _captchaPng.Length == 0) return "";

            try
            {
                _lastOcrResult = CaptchaOcr.RecognizePng(_captchaPng);
            }
            catch (Exception ex)
            {
                Log.Warn("验证码识别异常: " + ex.Message);
                return "";
            }

            if (_lastOcrResult.Error.Length > 0)
            {
                Log.Warn("验证码识别失败: " + _lastOcrResult.Error);
                return "";
            }

            // ⚠️ 要填什么**只能**问 FillCandidate()，不能直接用 r.Text。
            //    2026-10-03 的教训：直接用 Text 会把 "A?3?" 填进验证码框
            //    （逐位闸门放过 2 位，Text 里就留着 '?'）。用户看到问号一脸问号。
            //    现在策略是"总是填 4 位 top1"，只有某位连字符都没切出来才整体不填。
            string fill = CaptchaOcr.FillCandidate(_lastOcrResult);
            if (fill.Length == 0)
            {
                // 失败必须留证：存到 captcha-fails\（与带答案的样本目录分开，
                // 免得没答案的图被当成带标签样本污染准确率统计）。
                if (CaptchaAssist.SampleCollectEnabled(ConfigStore.LoadSettings()))
                {
                    string ferr;
                    CaptchaAssist.SaveFailImage(_captchaPng,
                        CaptchaOcr.UnreadableCount(_lastOcrResult) + "unread", out ferr);
                }
                return "";
            }
            return fill;
        }

        // ==================================================================
        // 全自动登录：填账号密码 → 认验证码 → 填进框 → 点登录 → 判结果 → 有界重试
        //
        // 为什么要有这一块（2026-10-12 海辰报的 bug）：
        //   "我点击我们一键登录时没有反应（信息已填好的情况下），需要手动点击网页里的登录。"
        //   —— 改之前，只要页面上有验证码，填表路径填完账号密码就 return 了，
        //   一个字都不提交；而且 _submittedThisLoad 为 true 之后再点也什么都不做。
        //   那是当年刻意的设计（怕连续登录失败把账号锁了），但和"全自动"直接冲突。
        // ==================================================================

        /// <summary>
        /// 页面上有验证码时，接手这次提交（识别 → 填框 → 点「登录」→ 判结果 → 有界重试）。
        ///
        /// 返回 true = 已接手（调用方别再自己提交，也别再提示"请手动点登录"）。
        /// 返回 false = 自动提交关着（维持老行为：只填不提交，让用户自己来）。
        /// </summary>
        private bool TryAutoSubmitWithCaptcha(WinForms.HtmlElement submit, string accountDesc)
        {
            if (!_autoSubmit) return false;

            // 正在等"换一张"的新图 → 别在这期间又发起一次尝试（否则会变成重复提交）。
            // 新图到手后由 TryCaptureCaptcha 的钩子接着跑。
            // ⚠️ 这条**不是**死路（新图到手会接力），但必须留一行日志 ——
            //    2026-10-13 排查海辰那份日志时，10:47:25 那次"填完表之后什么都没发生"
            //    就是因为在这里静默 return，日志里一个字都没有，看不出死在哪。
            if (_autoAuthWaitNext)
            {
                Log.Info("自动登录：正在等新的验证码图，本次填表先不发起提交");
                return true;
            }

            // 本轮已经停手了 → 别再让别的驱动者（补填定时器 / 自动重连 / 页面回传）
            // 反复走进来。⚠️ 2026-10-13 海辰日志里同一句"自动登录停手：提交次数已用完"
            // 被喊了 6 遍，就是这些驱动者各自进来触发一次 AutoAuthGiveUp 造成的。
            if (_autoAuthGaveUp) return true;

            if (_autoAuthAttempts >= AutoAuthMaxAttempts)
            {
                // 次数用完了就不再点按钮 —— 把最后填进去的候选留着让人接手核对
                Log.Warn("自动登录：本轮已提交 " + _autoAuthAttempts + " 次（上限 "
                         + AutoAuthMaxAttempts + " 次），不再提交，交给人工");
                SetStatus("自动登录已试满 " + AutoAuthMaxAttempts + " 次还没过 —— 请核对下面的验证码"
                    + (_autoAuthLastFill.Length > 0 ? "（我最后填的是 " + _autoAuthLastFill + "）" : "")
                    + "，手动点页面上的「登录」。");
                return true;
            }

            // 从这里开始就算"自动流程在跑"了 —— 认不出要换图再认、提交后要判结果，
            // 都靠这个标记接力（TryCaptureCaptcha / ScheduleAutoAuthNext 都在看它）。
            // ⚠️ 必须在"认验证码"之前设上：下面那条"认不出 → 换一张 → 再认一次"的路
            //    要靠它接力，设晚了 ScheduleAutoAuthNext 会判定"不在跑"直接空转。
            _autoAuthRunning = true;

            // 手上没有这张图就先抠一张（页面可能刚加载完，图还没进过 TryCaptureCaptcha）
            if (_captchaPng == null || _captchaPng.Length == 0)
            {
                TryCaptureCaptcha();
                StartCaptchaPoll(3);
            }

            // 还是没有 → 说明页面刚回传、新图还没下载完。
            // ⚠️ 这时候**绝不能**退回用旧图（就是海辰遇到的那个"填上一次的验证码"），
            //    也不能算成"认不出"（那会白白多换一张图）。等抠到图，由钩子接着跑。
            if (_captchaPng == null || _captchaPng.Length == 0)
            {
                Log.Info("自动登录：新验证码还没下载完，先等图（不用旧图提交）");
                SetStatus("正在等新的验证码图…");
                _autoAuthWaitNext = true;
                return true;
            }

            string fill = RecognizeCaptchaCandidate();
            _autoAuthLastFill = fill;

            if (fill.Length == 0)
            {
                // ⚠️ 认不出就**不能提交**。
                //    提交必然被服务器打回，而每一次失败学校端都会记一笔登录失败 ——
                //    白送一次失败记录，什么也换不到。
                //    （2026-10-12 自测里发现的：原先写的是"认不出也提交一次看结果"，
                //     那是照搬了老代码"先提交一次把表单激活"的思路，在全自动场景下是有害的。）
                //    先换一张再试一次识别（有些图就是特别糊）；还认不出就老实交给用户。
                if (!_autoAuthTriedFresh)
                {
                    _autoAuthTriedFresh = true;
                    // 必须留日志：这一支以前是静默的，排查时看不出"认不出→换图"发生过。
                    Log.Info("自动登录：这张验证码认不出来，换一张重试一次");
                    SetStatus("这张验证码认不清，正在换一张再试…");
                    SetCaptchaHint("这张认不清，换一张再试…");
                    _autoAuthWaitNext = true;
                    OnCaptchaRefresh();
                    return true;
                }

                AutoAuthGiveUp("验证码认不出来（换过一张还是不行）");
                SetStatus("验证码我认不出来（换过一张还是不行）—— 请手动填一下，再点页面上的「登录」。");
                return true;
            }

            if (!FillCaptchaBox(fill))
            {
                AutoAuthGiveUp("识别结果写不进验证码框");
                SetStatus("验证码认出来了，但写不进输入框 —— 请手动填一下再点「登录」。");
                return true;
            }

            // ⚠️⚠️ 信心门控（2026-10-13 首版；**2026-10-15 换成"四位全过闸"**）：
            //    认出了 4 位 **不等于** 值得提交 —— 提交失败学校端会记一笔登录失败，
            //    还占掉 3 次额度里的一次。所以没把握时**不提交，改换一张**（换图免费）。
            //
            //    【为什么把判据从 MinMargin 换成 AcceptedCount】
            //    老代码看的是 `MinMargin < 0.05`。但 MinMargin 的算法是
            //    （CaptchaOcr.cs:387-395）**只在"已经过闸的那几位"里取最小值**，
            //    没过闸的位根本不参与 —— 于是它的语义是"过关的几位里最差的一位"，
            //    **不是**"这张图最可疑的一位"。拿它当整图信心用是语义错位。
            //
            //    实测后果（300 张真样本）：在 MinMargin>=0.05 被放行的 197 张里，
            //    有 111 张其实**有位没过闸**（accepted<4），那批的全对率只有 47.7%。
            //    也就是说这道闸放行了大量"某一位根本没把握"的图 —— 它拦的依据是错的。
            //
            //    【新判据】AcceptedCount == 4：四位**每一位**都过了逐位闸
            //    （每位 margin >= CaptchaOcr.MinMargin = 0.03）。这才是"整张图有把握"。
            //
            //    300 张真样本实测 + 8 万轮流程仿真（含"抽到不合格图也要花时间"的成本）：
            //      现在线上(MinMargin>=0.05)：放行 65.7%，一次就成 67.1%，失败 0.47 次/轮
            //      新判据(AcceptedCount==4)  ：放行 48.7%，一次就成 90.4%，失败 0.11 次/轮
            //      → 一次就成 +23.3 个百分点；学校端失败减少 78%；平均换图 2.19→2.27 张
            //        （只多 0.08 张，因为换图快而提交失败要重来）。
            //    详细数据与脚本：验证码质检目录，以及交接文档 2026-10-15 那一节。
            if (_lastOcrResult != null
                && _lastOcrResult.AcceptedCount < AutoAuthRequiredAcceptedSlots)
            {
                _autoAuthRefreshCount++;
                if (_autoAuthRefreshCount > AutoAuthMaxRefresh)
                {
                    AutoAuthGiveUp("换了 " + AutoAuthMaxRefresh
                                   + " 张验证码都没碰到四位全有把握的");
                    SetStatus("换了 " + AutoAuthMaxRefresh + " 张验证码都没认出有把握的 —— "
                        + "请手动填一下验证码，再点页面上的「登录」。");
                    return true;
                }
                Log.Info("自动登录：这次识别信心不足（四位里只有 "
                         + _lastOcrResult.AcceptedCount + "/4 位过了闸，最弱一位 margin="
                         + _lastOcrResult.WeakestMargin.ToString("0.###")
                         + "）—— 不提交，换一张再试（提交失败学校端要记一次，换图免费）");
                SetStatus("这张验证码没把握，换一张再试…");
                SetCaptchaHint("没把握，换一张重试…");
                _autoAuthWaitNext = true;
                OnCaptchaRefresh();
                return true;
            }

            if (submit == null)
            {
                // ⚠️ 挑不出按钮时**必须把候选全打出来**（2026-10-13 加）。
                //    否则下一次又是"看不见"：只知道没找到，不知道页面上到底有什么。
                //    只在挑不出时打，正常情况不刷屏。
                //    ⚠️ 这个方法里没有现成的元素列表（参数只有 submit 和说明文字），
                //       得自己从当前文档捞一份 —— 第一次写漏了，编译直接报 CS0103。
                string candDesc = "(读不到文档)";
                try
                {
                    WinForms.HtmlDocument d = (browser == null) ? null : browser.Document;
                    if (d != null) candDesc = DescribeButtonCands(All(d));
                }
                catch { }
                Log.Warn("自动登录：挑不出「登录」按钮，页面上的候选 = " + candDesc);
                AutoAuthGiveUp("页面上找不到「登录」按钮");
                SetStatus("这个页面有验证码，但找不到「登录」按钮 —— 请手动点一下页面上的登录。");
                return true;
            }

            // ⚠️⚠️ 提交前最后一道：**确认手里这张还是页面上那张**（2026-10-13 新增）。
            //    这是海辰报的「填的验证码是刷新前的」的直接防线 ——
            //    从"认完这张图"到"点下去"之间可能过了几百毫秒到几秒，
            //    期间页面完全可能已经换了图（回传重载 / 用户点了换一张）。
            //    只要 src 变了，这次提交必然是把上一张的答案交上去，
            //    白让学校端记一次失败。所以：**不用旧图提交**，改为等新图。
            if (CaptchaIdentityChanged())
            {
                Log.Warn("自动登录：认完之后验证码图已经被换掉了 —— 放弃这次提交，改用新图重新认"
                         + "（否则填进去的就是上一张的答案）");
                SetStatus("验证码刚换过，正在用新的那张重新认…");
                _autoAuthWaitNext = true;
                return true;
            }

            // ⚠️ 计数交给闸门（SubmitAllowed）去做 —— 它同时管住别的提交出口
            if (!SubmitAllowed())
            {
                // ⚠️ 闸门拒绝有**三种**原因，不能一律当成"次数用完了"（2026-10-12 自测抓到）：
                //    ① 离上次提交太近（速率闸）→ 等一会儿还能继续，**绝不能把流程判死**
                //       （早先这里一律 AutoAuthGiveUp，结果正常重试被误判成"试满 3 次"直接放弃）
                //    ② 10 分钟窗口用完 → 该停手
                //    ③ 本轮次数用完 → 该停手
                if (_autoAuthAttempts >= AutoAuthMaxAttempts || _submitWindowCount >= SubmitWindowMax)
                {
                    AutoAuthGiveUp("提交次数已用完");
                    return true;
                }

                // ⚠️⚠️ 2026-10-13 修：这里以前只写了一句"稍后由定时器自然重试"就 return ——
                //    而**根本没有那个定时器**。海辰日志现场：10:47:47.241 被速率闸拦下，
                //    之后一路静默到看门狗超时、直接停手，整个流程就断在这儿，
                //    表现正是他说的"点了像没反应 / 不顺畅"。
                //    现在真的安排一次延迟重试，等过完速率闸再走一遍完整流程。
                ScheduleAutoAuthRetryAfterGate();
                return true;
            }
            _autoAuthRunning = true;
            _submittedThisLoad = true;
            _autoAuthEntries++;      // 诊断用：到底进了几次这条路

            // 来源标签：2026-10-12 那次"一秒 4 次提交"就是靠它才定位到驱动者的
            //（日志里只写"第 N 次提交"看不出是谁在反复触发）。
            // ⚠️ 2026-10-13 追加"按钮="：一定要记下**点的是哪个元素** ——
            //    "点错按钮（点了换一张）"这个 bug 之所以能藏这么久，就是因为日志里
            //    只有"第 N 次提交"，看不出点的是谁。以后一眼可辨。
            Log.Info("自动登录：第 " + _autoAuthAttempts + "/" + AutoAuthMaxAttempts + " 次提交"
                     + "（验证码填 \"" + fill + "\"，来源=" + CallerTag()
                     + "，按钮=" + Describe(submit) + "）");
            SetStatus("正在自动登录（第 " + _autoAuthAttempts + "/" + AutoAuthMaxAttempts + " 次）"
                      + "，验证码填的是 " + fill + "…");
            if (lblFound != null && accountDesc.Length > 0) lblFound.Text = accountDesc;

            _submitCalls++;
            _lastSubmittedCaptchaKey = _captchaKey ?? "";
            ClickElement(submit);
            // ⚠️ 提交前先记下"当时通不通"（2026-10-13 假阳性修复用）：
            //    成功判定不能只看"提交后通不通" —— 提交前就通着的话，
            //    这条证据是空的（海辰 12:24:17 就这么误报过一次成功）。
            //    OnlineStale() 不阻塞 UI（缓存 5 秒，过期就返回上次已知值并后台预热）。
            AfterSubmitAuto(NetProbe.OnlineStale());
            StartAutoAuthWatchdog();
            return true;
        }

        /// <summary>认不出验证码时"已经换过一张了"的标记（每轮只换一次，避免无限换图）。</summary>
        private bool _autoAuthTriedFresh = false;

        /// <summary>
        /// 看门狗超时用的**合成原因**。
        ///
        /// ⚠️ 它**不是服务器报错**，必须当"可重试"处理 —— 2026-10-13 用海辰的真实日志定位到：
        ///    这句合成原因以前被 AutoAuthNextAttemptOrGiveUp 当成"服务器明确报错"，
        ///    于是页面回传只要慢一点就直接"提交一次就停手"，**一键登录永远走不到第 2 次**。
        ///    日志现场（10:47:53.888）：`自动登录停手：服务器说：「页面没有回传结果」`。
        /// </summary>
        internal const string AutoAuthNoResultWhy = "页面没有回传结果";

        /// <summary>
        /// 看门狗等多久算"这次点击没生效"。
        ///
        /// ⚠️ 12 秒而不是 9 秒（2026-10-13 实测）：结果判定那条路是
        ///    `Thread.Sleep(4000)` + `ProbeOnlineNow()`，而探测本身最长要 5 秒，
        ///    合计约 9 秒 —— 和 9 秒的看门狗**正好打平**。
        ///    海辰日志里看门狗（10:47:53.887）比判定线程早 2 毫秒触发，
        ///    "还没判就先判死"，白丢了一整轮重试。留 3 秒余量。
        /// </summary>
        private const int AutoAuthWatchdogSeconds = 12;

        /// <summary>
        /// 提交的门槛：**四位必须每一位都过了逐位闸**（AcceptedCount == 4）。
        ///
        /// ⚠️ 2026-10-15 换掉了原来那个 `AutoAuthSubmitMinMargin = 0.05` 常量。
        ///    老判据用的是 MinMargin，而它的算法只在"已过闸的位"里取最小值
        ///    （CaptchaOcr.cs:387-395），**不是**整张图最可疑的那一位 —— 语义错位。
        ///    实测：MinMargin>=0.05 放行的 197 张里，111 张其实有位没过闸，
        ///    那批全对率只有 47.7%。故改用 AcceptedCount。
        ///
        ///    为什么定"四位全过闸"而不是更松（如 >=3）：
        ///    300 张真样本 + 8 万轮仿真，
        ///      accepted>=3：一次就成 75.0%、学校端失败 0.33 次/轮
        ///      accepted==4：一次就成 90.4%、学校端失败 0.11 次/轮  ← 选它
        ///    代价只是平均多换 0.08 张图（换图快，而提交失败要整轮重来）。
        ///
        ///    逐位闸本身的门槛在 CaptchaOcr.MinMargin = 0.03，不在本文件里。
        ///    所以「四位全过闸」等价于「每一位的 margin 都 >= 0.03」。
        ///
        ///    ⚠️ 别改成判断 `MinMargin >= 0.03` —— 那不是同一个集合：
        ///       实测 300 张里 `MinMargin>=0.03` 恒为真（没过闸的位不参与计算），
        ///       那样写等于把门控整个关掉。
        /// </summary>
        private const int AutoAuthRequiredAcceptedSlots = 4;

        /// <summary>
        /// 一轮里「换一张验证码」的上限。3 → 10（2026-10-13，与门槛配套）。
        ///
        /// 为什么必须放宽：门槛卡严之后，单张图"能提交"的概率会明显下降，
        /// 换图上限还停在 3 的话，**大多数轮次会"换了 3 张都没碰到有把握的"就停手** ——
        /// 那反而是倒退。换图不产生登录失败，所以这个上限可以放宽。
        /// （2026-10-15 实测：accepted==4 时放行率 48.7%，连着 10 张都被拦的概率约 0.1%，
        ///   所以 10 这个上限仍然够用。）
        /// </summary>
        private const int AutoAuthMaxRefresh = 10;

        /// <summary>
        /// 提交后等结果的看门狗：万一这次点击没触发页面回传（点偏了 / 按钮被禁用），
        /// 不能就这么一直挂着"正在自动登录"。
        /// </summary>
        private void StartAutoAuthWatchdog()
        {
            if (_autoAuthWatchdog == null)
            {
                _autoAuthWatchdog = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(AutoAuthWatchdogSeconds)
                };
                _autoAuthWatchdog.Tick += delegate(object s, EventArgs e)
                {
                    _autoAuthWatchdog.Stop();
                    if (!_autoAuthRunning) return;

                    // ⚠️ 正在等新图却没等到 → 这才是**唯一该主动点「换一张」**的场合。
                    //    （提交失败那种情况，页面回传时自己就会带一张新图；
                    //      程序再点一次等于把答案凭空多转一轮 —— 就是海辰看到的
                    //      "点击后页面验证码会重复刷新"。）
                    if (_autoAuthWaitNext)
                    {
                        _autoAuthRefreshCount++;
                        if (_autoAuthRefreshCount > AutoAuthMaxRefresh)
                        {
                            AutoAuthGiveUp("连着等了几次都没等到可用的验证码图");
                            return;
                        }
                        Log.Warn("自动登录：等了 " + AutoAuthWatchdogSeconds
                                 + " 秒没等到新验证码图，主动让页面换一张（第 "
                                 + _autoAuthRefreshCount + " 次）");
                        OnCaptchaRefresh();
                        StartAutoAuthWatchdog();
                        return;
                    }

                    Log.Warn("自动登录：提交后 " + AutoAuthWatchdogSeconds
                             + " 秒没有任何结果（这次点击可能没生效），按失败处理");
                    AutoAuthNextAttemptOrGiveUp(AutoAuthNoResultWhy, true);
                };
            }
            _autoAuthWatchdog.Stop();
            _autoAuthWatchdog.Start();
        }

        /// <summary>
        /// 自动登录版的结果判定（对应老 AfterSubmit，但带重试决策）。
        ///
        /// ⚠️ 2026-10-13 修「提交成功」的假阳性：
        ///    原来只看 `netOk`（网络通不通）。可**提交之前网络就是通的**情况下，
        ///    "现在通着"证明不了这次登录成功 —— 海辰日志 12:24:17 就是这么误报了一次
        ///    「第 1 次提交成功」（当时网络全程是通的，验证码其实很可能是错的）。
        ///    更糟的形态是：13:26:04 提交、5.6 秒后又判定掉线 —— 一次错提交被算成成功，
        ///    重试就白白少了一次。
        ///    现在要求**两种证据之一**：
        ///      ① 提交前不通、提交后通了（真正的跃变）
        ///      ② 页面已经没有验证码了（离开了登录页 —— 项目里 MaybeRecordSample
        ///         早就把这一条当作"真的登进去了"的旁证）
        /// </summary>
        private void AfterSubmitAuto(bool wasOnlineBefore)
        {
            int attempt = _autoAuthAttempts;
            Thread t = new Thread(delegate()
            {
                try
                {
                    bool netOk = false;
                    string pageErr = "";
                    bool leftLogin = false;

                    // 渐进式探测（最多约 4.2 秒，每轮 300ms）：
                    // 若网络已连通，或服务端回传完成（明确报错 / 新验证码到达），
                    // 立即跳出循环进入结算，不再傻等 4 秒。
                    for (int step = 0; step < 14; step++)
                    {
                        Thread.Sleep(300);
                        if (!_autoAuthRunning) return;

                        netOk = ProbeOnlineNow();

                        try
                        {
                            Dispatcher.Invoke(new Action(delegate()
                            {
                                try
                                {
                                    WinForms.HtmlDocument d = (browser == null) ? null : browser.Document;
                                    if (d != null) pageErr = ReadPageError(All(d));
                                    leftLogin = LeftLoginPage();
                                }
                                catch { }
                            }));
                        }
                        catch { }

                        // 1. 如果已连通（跃变或离开登录页），立即结算
                        if (netOk && (!wasOnlineBefore || leftLogin))
                        {
                            break;
                        }

                        // 2. 如果网未通，且经过至少 600ms（避免提交事件未发出）：
                        //    如果页面报错已出，或回传的新验证码已到手，说明本次提交已被拒，提前结算！
                        if (step >= 2)
                        {
                            bool hasNewCaptcha = false;
                            try
                            {
                                Dispatcher.Invoke(new Action(delegate()
                                {
                                    if (_captchaPng != null && _captchaKey.Length > 0
                                        && !string.Equals(_captchaKey, _lastSubmittedCaptchaKey, StringComparison.Ordinal))
                                    {
                                        hasNewCaptcha = true;
                                    }
                                }));
                            }
                            catch { }

                            if (pageErr.Length > 0 || hasNewCaptcha)
                            {
                                break;
                            }
                        }
                    }

                    try
                    {
                        Dispatcher.Invoke(new Action(delegate()
                        {
                            if (!_autoAuthRunning) return;      // 已经被别处收尾（成功/停手/关窗）

                            // ⚠️ 假阳性防线（2026-10-13，详见方法注释）：
                            //    "网络通着"单独不足以证明这次登录成功 —— 提交前就可能已经通着。
                            //    要求"从不通到通"的跃变，或者"页面已经没有验证码了"（离开登录页）。
                            try { leftLogin = LeftLoginPage(); }
                            catch { }

                            if (netOk && (!wasOnlineBefore || leftLogin))
                            {
                                // ⚠️ 只让**最后一次提交**的检查者来报"成功"（2026-10-12 自测里发现）：
                                //    失败后页面会重载并（由重试流程）再提交一次，而上一轮的检查者
                                //    可能抢在新一轮前面报"成功（第 1 次提交）"，
                                //    数字对不上，日志也会误导排查的人。
                                if (attempt != _autoAuthAttempts) return;

                                _autoAuthRunning = false;
                                if (_autoAuthWatchdog != null) _autoAuthWatchdog.Stop();
                                SetStatus("自动登录成功 —— 已经连上网了（第 " + attempt + " 次提交）。");
                                Log.Info("自动登录：第 " + attempt + " 次提交成功"
                                         + "（跃变=" + (!wasOnlineBefore)
                                         + "，已离开登录页=" + leftLogin + "）");
                                NotifyOwnerOnline();
                                // ⚠️ 2026-10-15：用户选了「窗口保持打开」就不关
                                //    （_keepOpenBySetting 只在开机/主动打开那条路上被置位；
                                //      纯后台自动重连的窗口它恒为 false，照旧自己消失）。
                                if (!_keepOpenBySetting && (_startupAutoClose || _reauthMode))
                                {
                                    try { Close(); } catch { }
                                }
                                return;
                            }

                            // 网络通着但拿不出"这次登录生效"的证据 —— 不能算成功，
                            // 也不能直接当失败（可能是生效慢）。交给下面那条正常判定路径，
                            // 让它看页面报错再决定重试还是停手。
                            if (netOk)
                            {
                                Log.Info("自动登录：网络是通的，但提交前就已经通着、"
                                         + "页面也还在登录页 —— 不算这次成功，继续按页面结果判定");
                            }

                            // submitted = true：这条路本来就是"提交之后判结果"，
                            // 所以不该再主动点「换一张」（见 AutoAuthNextAttemptOrGiveUp 注释）。
                            AutoAuthNextAttemptOrGiveUp(pageErr, true);
                        }));
                    }
                    catch { }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 这一次没过：验证码错就换一张接着试，别的错立刻停手。
        ///
        /// ⚠️ 为什么必须区分这两类：账号密码错 / 被限速 / 被拉黑 ——
        ///    这些再试一百次也是白搭，而且每一次都在学校端累加失败次数。
        ///    只有"验证码错"才值得重试。
        ///
        /// <param name="submitted">
        /// 这一次是不是**真的提交过**（点击落到页面上）。
        /// ⚠️ 2026-10-13 新增这个参数，专门治海辰报的「页面验证码会重复刷新」：
        ///    提交过 → 页面回传时服务端已经换了答案、页面会自己重新下载新图
        ///             （实测每次提交后 0.7 秒内就有新图），程序**不必也不能**再点「换一张」，
        ///              多点一次等于凭空多转一轮答案，用户看到的就是验证码在疯狂刷新，
        ///              而且会让"认的那张"和"页面上的那张"错位（填上旧答案）。
        ///    没提交过（认不出 / 按钮点不动）→ 页面不会自己换图，这时**才**需要主动换一张。
        /// </param>
        /// </summary>
        /// <summary>
        /// 页面是不是已经**离开登录页**了。
        ///
        /// 判据：页面上已经没有验证码那一套。这是"真的登进去了"最直接的旁证 ——
        /// 项目里的 MaybeRecordSample 早就把这一条当成成功证据用了
        /// （"跳转后的页面上已经没有验证码了"）。失败回传时页面会重载并重新显示验证码。
        ///
        /// ⚠️ 2026-10-13 新增，用来补「提交成功」判据的假阳性：网络通着不等于这次登录成功。
        /// </summary>
        private bool LeftLoginPage()
        {
            try
            {
                if (browser == null) return false;
                WinForms.HtmlDocument d = browser.Document;
                if (d == null) return false;
                return !PageHasCaptchaNow();
            }
            catch { return false; }
        }

        /// <summary>
        /// 这一次没过：验证码错就换一张接着试，别的错立刻停手。
        /// </summary>
        private void AutoAuthNextAttemptOrGiveUp(string pageErr, bool submitted)
        {
            if (!_autoAuthRunning) return;
            if (_autoAuthWatchdog != null) _autoAuthWatchdog.Stop();

            // ⚠️⚠️ 2026-10-13 修（海辰真实日志定位到的核心 bug）：
            //    看门狗超时给的是**本地合成的**"页面没有回传结果" —— 它的意思是
            //    "我们没看到结果"，而**不是**服务器给了什么错误。
            //    以前它落到下面那句 `pageErr.Length > 0` 分支，被判成"服务器明确报错"，
            //    直接 AutoAuthGiveUp —— 于是**每次页面回传慢一点就只提交一次就停手**，
            //    3 次重试永远用不上，海辰看到的就是"点了登录没反应 / 不顺畅"。
            //    现场的 10:47:53.888 日志：`自动登录停手：服务器说：「页面没有回传结果」`。
            //    归零之后它会走"没有明确报错 → 换一张再试"这条正确路径。
            if (string.Equals(pageErr, AutoAuthNoResultWhy, StringComparison.Ordinal))
            {
                pageErr = "";
            }

            bool captchaWrong = pageErr.IndexOf("验证码", StringComparison.Ordinal) >= 0
                             || pageErr.IndexOf("校验码", StringComparison.Ordinal) >= 0
                             || pageErr.IndexOf("图形码", StringComparison.Ordinal) >= 0;

            if (!captchaWrong && pageErr.Length > 0)
            {
                AutoAuthGiveUp("服务器说：「" + pageErr + "」");
                return;
            }

            if (_autoAuthAttempts >= AutoAuthMaxAttempts)
            {
                AutoAuthGiveUp(pageErr.Length > 0 ? ("服务器说：「" + pageErr + "」")
                                                  : ("试了 " + AutoAuthMaxAttempts + " 次都没过"));
                return;
            }

            Log.Info("自动登录：第 " + _autoAuthAttempts + " 次没过（"
                     + (pageErr.Length > 0 ? pageErr : "没有明确报错") + "），换一张验证码再试");

            // 换图也要有上限：万一新图一直没等到（页面不响应"换一张"），
            // 不能就这么无限换下去 —— 那样会一直挂着"正在自动登录"。
            _autoAuthRefreshCount++;
            if (_autoAuthRefreshCount > AutoAuthMaxRefresh)
            {
                AutoAuthGiveUp("连着换了几张验证码都没等到可用的图");
                return;
            }

            SetStatus("这次没认对，正在准备重试…");
            SetCaptchaHint("这次没认对，马上重试…");

            // ⚠️⚠️ 2026-10-13 关键修正（海辰报的「页面验证码会重复刷新」+「填的是刷新前的」）：
            //    **提交过的时候绝不主动点「换一张」**。
            //    页面回传本身就是一次导航，服务端答案已换、页面会自己重新下载新图 ——
            //    实测每次提交后 0.7 秒内日志就有"验证码已抠出"。
            //    程序再点一次「换一张」= 又转一轮答案，于是：
            //      · 用户看到验证码图连着闪（重复刷新）；
            //      · 程序上一刻认的那张已经作废，填上去的就是"刷新前的"答案，必然失败。
            //    所以这里只**等**页面自己带来的那张新图（_autoAuthWaitNext 的钩子接力）；
            //    真等不到（看门狗到点）再主动换 —— 那条兜底写在 StartAutoAuthWatchdog 里。
            if (submitted)
            {
                // ⚠️ 2026-10-14 核心死锁修复（海辰真实日志现场）：
                //    页面回传极快时（~0.5s），新图可能在 AfterSubmitAuto 醒来之前就已经抠出存入 _captchaKey。
                //    若此时盲目设置 _autoAuthWaitNext = true，程序会傻等一张"已经到手"的新图，
                //    直到 12 秒看门狗超时才动，造成极其严重的卡顿感。
                //    因此若新图已在手，立即解除等待并无缝衔接下一次尝试！
                if (_captchaPng != null && _captchaKey.Length > 0
                    && !string.Equals(_captchaKey, _lastSubmittedCaptchaKey, StringComparison.Ordinal))
                {
                    Log.Info("自动登录：页面回传的新验证码已在手，立即继续下一次重试");
                    _autoAuthWaitNext = false;
                    ScheduleAutoAuthNext();
                    return;
                }

                _autoAuthWaitNext = true;
                Log.Info("自动登录：等页面回传时自己带来的那张新验证码（程序不主动换图）");
                StartCaptchaPoll(15);
            }
            else
            {
                _autoAuthWaitNext = true;
                Log.Info("自动登录：这次压根没提交成功，主动让页面换一张验证码");
                OnCaptchaRefresh();
            }
            // 再武装一次看门狗：上面把它停了。新图不来时它会兜底
            //（_autoAuthRefreshCount 已经加过，很快就到上限停手）。
            StartAutoAuthWatchdog();
        }

        /// <summary>本次自动登录里"换一张验证码"的次数（防止无限换图挂着不动）。</summary>
        private int _autoAuthRefreshCount = 0;

        /// <summary>自测用：进了几次自动提交这条路（诊断"提交次数对不上"用）。</summary>
        private int _autoAuthEntries = 0;

        /// <summary>自测用：Navigate 被调用了几次（排查"计数器被谁重置了"用）。</summary>
        private int _navCount = 0;

        /// <summary>自测用：老路径（只填不提交那条）实际提交了几次。</summary>
        private int _oldPathSubmits = 0;

        /// <summary>已经在状态里报过"次数用完了"，别每一轮重复喊。</summary>
        private bool _submitCapReported = false;

        /// <summary>自测用：客户端**真的调了几次提交动作**（和服务端收到的 POST 数对比，
        /// 能分清"程序提交了多次"和"浏览器把一次提交投递了多次"）。</summary>
        private int _submitCalls = 0;

        /// <summary>自测用：闸门放行了几次。</summary>
        private int _gateAllows = 0;

        /// <summary>上一次真正提交的时刻（速率闸用）。</summary>
        private DateTime _lastSubmitAt = DateTime.MinValue;

        /// <summary>滑动窗口的起点与已提交次数（**没有任何重置代码**，这是它能兜住的原因）。</summary>
        private DateTime _submitWindowStart = DateTime.MinValue;
        private int _submitWindowCount = 0;
        private bool _submitWindowReported = false;

        /// <summary>
        /// 两次提交之间的最短间隔（毫秒）。
        ///
        /// ⚠️ 这个值不能乱调：**正常重试的节奏是 4~5 秒**（提交 → 等 4 秒判结果 → 换一张 → 再提交），
        ///    所以间隔必须明显小于 4 秒，否则会把正常重试也挡掉。
        ///    而那次风暴是 **300 毫秒一次**，3 秒足够把它按住。
        ///    真正兜底的是下面那道"10 分钟最多 3 次"的窗口闸。
        /// </summary>
        private const int MinSubmitGapMs = 3000;

        /// <summary>
        /// 滑动窗口长度与上限：10 分钟最多 5 次。
        ///
        /// 为什么是 5 而不是 3：**一轮自动登录最多会用掉 3 次**（AutoAuthMaxAttempts），
        /// 留 2 次余量给"10 分钟内又掉一次线"。而那次风暴是 60 次/天、一秒 4 次 ——
        /// 5 这个量级既能兜住风暴，又不会把正常使用卡死。
        /// </summary>
        private const int SubmitWindowMinutes = 10;
        private const int SubmitWindowMax = 5;

        /// <summary>
        /// 提交闸门 —— 一个窗口里**所有**提交动作都必须从这里过。
        ///
        /// ⚠️ 为什么要有这个（2026-10-12 自测实测到的真缺陷）：
        ///    上限原来只判在"自动登录"那条路里，可填表路径还有别的提交出口
        ///    （老路径的 SubmitForm / SubmitFirstForm、以及启发式尾部的直接点按钮）。
        ///    假门户一直拒绝时，实测提交了 **4~10 次** —— 上限根本没兜住。
        ///    而每一次失败学校端都会记一次登录失败，这正是上限要防的事。
        ///    所以闸门必须装在"提交"这个动作**本身**上，而不是某一个调用方里。
        ///
        /// 保守模式（_autoSubmit=false，只填不提交）不受此限，行为与历史版本一致。
        /// </summary>
        private bool SubmitAllowed()
        {
            if (!_autoSubmit) return true;

            // ① 速率闸：两次提交至少隔 MinSubmitGapMs。
            //    ⚠️⚠️ 2026-10-12 血案（海辰真实日志）：一秒里提交了 4 次、一天 60 次，
            //    而且每次都打印"第 1/3 次" —— 说明**有某条路径把 _autoAuthAttempts 反复重置**，
            //    "3 次上限"形同虚设，学校端被记了一堆失败。
            //    结论：**上限不能只靠一个会被重置的计数器**，必须有不依赖任何状态的闸。
            //    这一道（时间间隔）和下一道（滑动窗口）都是这个作用。
            double sinceMs = (DateTime.Now - _lastSubmitAt).TotalMilliseconds;
            if (_lastSubmitAt != DateTime.MinValue && sinceMs < MinSubmitGapMs)
            {
                Log.Warn("提交闸门拦下：离上次提交只有 " + (int)sinceMs + " 毫秒（最短间隔 "
                         + MinSubmitGapMs + " 毫秒），疑似提交风暴");
                return false;
            }

            // ② 滑动窗口闸：SubmitWindowMinutes 分钟内最多 SubmitWindowMax 次。
            //    这两道闸**没有任何地方会重置它们**（不是"注意别重置"，是根本没写重置代码）。
            if (_submitWindowStart == DateTime.MinValue
                || (DateTime.Now - _submitWindowStart).TotalMinutes >= SubmitWindowMinutes)
            {
                _submitWindowStart = DateTime.Now;
                _submitWindowCount = 0;
            }
            if (_submitWindowCount >= SubmitWindowMax)
            {
                if (!_submitWindowReported)
                {
                    _submitWindowReported = true;
                    Log.Warn("提交闸门拦下：" + SubmitWindowMinutes + " 分钟内已提交 "
                             + _submitWindowCount + " 次，本轮不再自动提交");
                    SetStatus("最近 " + SubmitWindowMinutes + " 分钟内已经自动提交过 "
                        + SubmitWindowMax + " 次都没成功 —— 先停下（免得学校端把账号当成攻击）。"
                        + "请手动核对验证码后点页面上的「登录」，或过一会儿再试。");
                }
                return false;
            }
            _submitWindowCount++;

            if (_autoAuthAttempts >= AutoAuthMaxAttempts)
            {
                if (!_submitCapReported)
                {
                    _submitCapReported = true;
                    Log.Warn("提交闸门拦下：本次已经提交过 " + _autoAuthAttempts + " 次（上限 "
                             + AutoAuthMaxAttempts + " 次），不再继续，交给人工");
                    SetStatus("已经自动提交过 " + AutoAuthMaxAttempts + " 次都没成功 —— 不再继续试了"
                        + "（每次失败学校都会记一次登录失败，试太多可能把账号锁住）。"
                        + "请核对下面的验证码，手动点页面上的「登录」。");
                }
                return false;
            }

            _autoAuthAttempts++;
            _gateAllows++;
            _lastSubmitAt = DateTime.Now;
            return true;
        }

        private void AutoAuthGiveUp(string why)
        {
            // ⚠️ 只喊一次（2026-10-13 修）。海辰日志里同一句
            //    "自动登录停手：提交次数已用完" 在 20 秒内被喊了 6 遍 ——
            //    因为补填定时器 / 自动重连 / 页面回传都在各自驱动一遍填表+提交，
            //    每一遍都走到这里。重复喊既刷屏，又让人以为流程在反复重启。
            //    ⚠️ 注意：状态（_autoAuthRunning=false）**每次都做**，只把
            //    "喊话"这一步限制成一次 —— 否则状态会不一致（见下面的坑）。
            bool firstGiveUp = !_autoAuthGaveUp;
            _autoAuthGaveUp = true;

            _autoAuthRunning = false;
            if (_autoAuthWatchdog != null) _autoAuthWatchdog.Stop();

            if (!firstGiveUp) return;
            Log.Warn("自动登录停手：" + why);

            // 后台静默跑的时候窗口在屏幕外，用户看不到状态条 —— 失败必须用气泡告知，
            // 否则他会以为"网络自己好了"，其实一直没登上去。
            if (_backgroundSilent)
            {
                try
                {
                    MainWindow mw = (Owner as MainWindow) ?? this.owner;
                    if (mw != null)
                        mw.NotifyBackgroundAuthFailed(why, _autoAuthLastFill);
                }
                catch { }
            }
            // ⚠️ 2026-10-13 把话说清楚（海辰的疑问："验证码错误会把密码给刷新"）：
            //   提交失败后页面回传、密码框被清空，是**门户本身的行为**
            //   （ASP.NET WebForms 不会把密码送回浏览器 —— 所有网站都这样，属正常安全行为），
            //   不是我们的 bug。但用户看到的就是"我的密码被清了"，所以这里必须**明说**
            //   "账号密码我已经重新填回去了，你只需要补验证码"，否则他以为要全部重打。
            // ⚠️ 停手之前**再补填一次**账号密码（2026-10-13，对应海辰的疑问
            //    「验证码错误会把密码给刷新」）。
            //
            //    失败后页面回传、密码框被清空，是**门户本身的行为**：
            //    ASP.NET WebForms 不会把密码送回浏览器（所有网站都这样，属正常安全行为），
            //    不是我们的 bug。但用户看到的就是"我的密码被清了"，以为要全部重打。
            //    所以这里主动补一次，让他接手时**只需要填验证码**。
            //
            //    ⚠️ 顺序要紧：先补填（它内部也会 SetStatus），再写下面那段结论文案，
            //       否则补填的状态话会把结论盖掉。
            //    ⚠️ 不会递归：TryAutoSubmitWithCaptcha 见到 _autoAuthGaveUp 为真时直接返回，
            //       也不会走到 FillByProfile 尾部那条"老路径提交"。
            try { TryAutoFillAndSubmit(true); } catch { }

            SetStatus("自动登录没成功 —— " + why
                + (_autoAuthLastFill.Length > 0 ? "。我最后填的验证码是 " + _autoAuthLastFill + "。" : "。")
                + "账号密码我已经重新填好了（失败后页面重载会清空它们，这是网页本身的行为），"
                + "你只需要填一下验证码，再点页面上的「登录」就行。");
        }

        /// <summary>换一张之后，等新图到手了接着跑下一次（由 TryCaptureCaptcha 消费这个标记）。</summary>
        private bool _autoAuthWaitNext = false;

        /// <summary>
        /// 自测用：替换"这次提交成功了没有"的判据。
        ///
        /// 为什么必须有这个缝：真机上这个判据是 NetProbe.Online(true)，
        /// 而跑自测的机器**往往本来就是通的** —— 那样第一次提交就会被判成"成功"，
        /// "失败 → 换一张 → 再来一次"这条重试路径**永远测不到**，自测会假通过。
        /// 产品代码里它是 null，行为与以前完全一致。
        /// </summary>
        internal Func<bool> OnlineProbeForTest = null;

        private bool ProbeOnlineNow()
        {
            if (OnlineProbeForTest != null)
            {
                try { return OnlineProbeForTest(); }
                catch { return false; }
            }
            return NetProbe.Online(true);
        }

        // ---- 下面几个都是自测入口（产品逻辑不依赖） ----
        internal void SetAutoSubmitForTest(bool on) { _autoSubmit = on; }
        internal void ClickLoginForTest() { OnLoginClick(); }
        internal string StatusForTest() { return (lblStatus == null) ? "" : lblStatus.Text; }

        /// <summary>自测用：「保存登录信息」提示**实际显示出来的**文字（折叠时返回空串）。</summary>
        internal string SaveHintForTest()
        {
            if (lblSaveHint == null) return "";
            if (lblSaveHint.Visibility != Visibility.Visible) return "";
            return lblSaveHint.Text ?? "";
        }

        /// <summary>自测用：学工号缺失警告**实际显示出来的**文字（折叠时返回空串）。</summary>
        internal string IdentityHintForTest()
        {
            if (lblIdentityHint == null) return "";
            if (lblIdentityHint.Visibility != Visibility.Visible) return "";
            return lblIdentityHint.Text ?? "";
        }
        internal int AutoAuthAttemptsForTest() { return _autoAuthAttempts; }

        /// <summary>自测用：自动登录流程是不是还在跑（true = 没停手）。</summary>
        internal bool AutoAuthRunningForTest() { return _autoAuthRunning; }

        /// <summary>自测用：当前手里那张验证码图的身份（src）。空 = 没抓到身份。</summary>
        internal string CaptchaSrcForTest() { return _captchaSrc; }

        /// <summary>
        /// 自测用：验证"图的身份"能不能区分**同一个 src、不同内容**的两张图。
        ///
        /// ⚠️ 这正是海辰说的"**有概率**会填入刷新前的验证码"的根子：
        ///    门户回传后 img 的 src 常常一模一样，服务器却换了图。
        ///    只比 src 会把新图误判成旧图，于是拿旧像素去认、去填。
        ///    所以这里必须断言：**同 src + 内容不同 → 身份必须不同**；
        ///    同 src + 内容相同 → 身份必须相同（否则轮询会把同一张图当新图，重复触发）。
        /// </summary>
        internal static bool KeyDistinguishesContentForTest()
        {
            string a = MakeCaptchaKey("http://x/CheckCode", "AAAABBBB");
            string b = MakeCaptchaKey("http://x/CheckCode", "AAAABBBC");   // 同 src，内容差 1 个字符
            string c = MakeCaptchaKey("http://x/CheckCode", "AAAABBBB");   // 完全一样
            string d = MakeCaptchaKey("http://x/CheckCode?t=2", "AAAABBBB"); // 内容一样，src 不同
            return a != b && a == c && a != d;
        }

        /// <summary>
        /// 自测用：验证"验证码图的身份"这条判据（2026-10-13 新增）。
        /// 返回 "拦得住/不误伤" 才算健康。
        ///
        /// 它同时验两件相反的事，缺一不可：
        ///   ① 图真的换了（src 变了）→ **必须拦住**（否则就是把旧答案提交上去）
        ///   ② 图没换（src 一样）→ **绝不能拦**（否则流程会被永久卡在"等新图"上）
        /// ⚠️ 只验 ① 是最容易骗过自己的写法 —— 一个 `return true` 的假实现也能过。
        ///    改完立刻还原，不影响后续流程。
        /// </summary>
        internal string CaptchaIdentityProbeForTest()
        {
            if (_captchaKey.Length == 0) return "没抓到图身份";

            string keep = _captchaKey;
            _captchaKey = keep + "__poisoned__";
            bool caught = CaptchaIdentityChanged();
            _captchaKey = keep;
            bool noFalseAlarm = !CaptchaIdentityChanged();

            return (caught ? "拦得住" : "拦不住") + "/" + (noFalseAlarm ? "不误伤" : "误伤");
        }

        /// <summary>
        /// 自测用：**直接**模拟"看门狗超时"（页面一直没回传结果）。
        /// 返回 true = 程序选择继续重试；false = 它停手了。
        ///
        /// ⚠️ 这个入口是为 2026-10-13 那个真 bug 专门加的。为什么要单独一条用例、
        ///    而不是靠假门户跑出来：
        ///    原来的「一键登录会自动提交并重试」用例里假门户是**毫秒级响应**的，
        ///    12 秒的看门狗永远轮不到触发 —— 所以那个 bug 在自测里**一直是绿的**，
        ///    却在海辰的真机上（页面回传要好几秒）每次都踩。
        ///    教训：验证环境比真实环境"快"得多时，超时类的路径等于没被测过。
        /// </summary>
        internal bool SimulateWatchdogTimeoutForTest()
        {
            // submitted = false：自测里我们希望它走"主动换一张"那条路，
            // 这样探针能覆盖得更全（页面不响应时也有兜底）。
            AutoAuthNextAttemptOrGiveUp(AutoAuthNoResultWhy, false);
            return _autoAuthRunning;
        }

        /// <summary>
        /// 自测用：被速率闸拦下之后，程序**一共安排过几次**延迟重试（只增不减）。
        ///
        /// ⚠️ 对应 2026-10-13 修的另一个 bug：以前这里只打了一句
        ///    "稍后由定时器自然重试"就 return，而根本没有那个定时器 ——
        ///    海辰日志里 10:47:47 被拦下之后一路静默到停手，整个流程就断在那儿。
        ///
        /// ⚠️ 用"累计次数"而不是 `_autoAuthRetryTimer.IsEnabled`：
        ///    定时器一旦触发就 Stop 了，拿 IsEnabled 当判据会时真时假
        ///    （正是"判据必须不可能骗过你"那条）。
        /// </summary>
        internal int GateRetryScheduledCountForTest()
        {
            return _gateRetryScheduledCount;
        }

        /// <summary>速率闸拦下后累计安排过多少次延迟重试（诊断/自测用，只增不减）。</summary>
        private int _gateRetryScheduledCount = 0;

        /// <summary>自测用：滑动窗口闸的计数（验证"上限兜得住"用）。</summary>
        internal int SubmitWindowCountForTest() { return _submitWindowCount; }

        /// <summary>自测用：把自动登录的内部状态摊开（排查"提交了几次"这类问题用）。</summary>
        internal string AutoStateForTest()
        {
            return "autoSubmit=" + _autoSubmit
                 + " attempts=" + _autoAuthAttempts
                 + " 进入次数=" + _autoAuthEntries
                 + " running=" + _autoAuthRunning
                 + " waitNext=" + _autoAuthWaitNext
                 + " triedFresh=" + _autoAuthTriedFresh
                 + " refresh=" + _autoAuthRefreshCount
                 + " 有图=" + (_captchaPng != null)
                 + " nav=" + _navCount
                 + " 老路径提交=" + _oldPathSubmits
                 + " 闸门放行=" + _gateAllows
                 + " 客户端提交=" + _submitCalls
                 + " 点击登录=" + _loginClicks;
        }

        /// <summary>
        /// 新验证码图到手 → 如果是在自动登录的重试流程里，就接着走下一次。
        ///
        /// ⚠️ 这里必须重新走一遍完整的填表（TryAutoFillAndSubmit），不能只提交：
        ///    ASP.NET WebForms 回传之后**账号密码框会被清空**（服务端不保存文本框内容），
        ///    只提交的话第二次必然是"账号密码为空"的失败。
        /// </summary>
        private void ScheduleAutoAuthNext()
        {
            DispatcherTimer once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            once.Tick += delegate(object s, EventArgs e)
            {
                once.Stop();
                if (!_autoAuthRunning) return;
                if (_autoAuthAttempts >= AutoAuthMaxAttempts) { AutoAuthGiveUp("次数用完了"); return; }
                TryAutoFillAndSubmit(true);
            };
            once.Start();
        }

        /// <summary>
        /// 被**速率闸**拦下后的延迟重试。
        ///
        /// 为什么要单独一个：重试的节奏是"页面回传 → 重新填表 → 再提交"，
        /// 实测只有约 1.4~2.4 秒（海辰日志 10:47:47.241 = 2356ms、10:48:02.651 = 1438ms），
        /// 比 3 秒的速率闸**本身还快** —— 于是正常重试被自己的闸门拦下。
        /// 拦下之后必须**真的等一会儿再来**，而不是嘴上说"稍后由定时器自然重试"。
        /// （⚠️ 2026-10-13 之前那句话是空话，压根没有这个定时器。）
        /// </summary>
        private void ScheduleAutoAuthRetryAfterGate()
        {
            double sinceMs = _lastSubmitAt == DateTime.MinValue
                ? MinSubmitGapMs
                : (DateTime.Now - _lastSubmitAt).TotalMilliseconds;

            double wait = MinSubmitGapMs - sinceMs + 300;
            if (wait < 300) wait = 300;
            if (wait > MinSubmitGapMs + 800) wait = MinSubmitGapMs + 800;   // 兜底，别等太久

            Log.Info("自动登录：这次尝试被速率闸拦下（离上次提交太近），"
                     + (int)wait + " 毫秒后自动重试");
            SetStatus("正在自动登录 —— 等验证码换好，马上重试…");
            _gateRetryScheduledCount++;

            // ⚠️ 只保留**一个**定时器实例：重复进来就重启它，绝不叠加
            //    （叠加就等于把"防风暴"变成"造风暴"）。
            if (_autoAuthRetryTimer == null)
            {
                _autoAuthRetryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(wait)
                };
                _autoAuthRetryTimer.Tick += delegate(object s, EventArgs e)
                {
                    _autoAuthRetryTimer.Stop();
                    if (!_autoAuthRunning) return;
                    if (_autoAuthAttempts >= AutoAuthMaxAttempts) return;
                    TryAutoFillAndSubmit(true);
                };
            }
            _autoAuthRetryTimer.Stop();
            _autoAuthRetryTimer.Interval = TimeSpan.FromMilliseconds(wait);
            _autoAuthRetryTimer.Start();
        }

        /// <summary>速率闸拦下后的延迟重试定时器（全局只有一个实例，不会叠起来）。</summary>
        private DispatcherTimer _autoAuthRetryTimer;

        /// <summary>把文字写进页面的验证码输入框。只赋值，不做任何提交动作。</summary>
        private bool FillCaptchaBox(string text)        {
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
            _captchaSrc = "";
            _captchaKey = "";
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

            _navCount++;
            _autoDoneThisLoad = false;
            _submittedThisLoad = false;
            _sampleTriedThisLoad = false;

            // ⚠️⚠️ 这里**不能**重置 _autoAuthAttempts（2026-10-12 自测实测到的真缺陷）。
            //    提交失败后页面回传**本身就是一次导航**（Navigate 会被调用），
            //    一重置额度就又满了 —— 上限形同虚设：假门户一直拒绝时实测提交了
            //    4~11 次（上限写的是 3）。而每次失败学校端都会记一次登录失败，
            //    这正是上限要防的事。
            //    额度属于"这一次登录"，不属于"这一页"：只在用户重新点「一键登录」
            //    或窗口重新开一轮时才归零（见 OnLoginClick 和 SetReauthMode）。
            _autoAuthRunning = false;
            _autoAuthWaitNext = false;
            _autoAuthTriedFresh = false;
            // ⚠️ 换页也把"已经停手"的标记清掉：自动重连会重新打开认证页，
            //    如果标记一直留着，重连那一轮会被静默挡住（看起来像"重连了但没反应"）。
            //    重复喊话的风险由 AutoAuthGiveUp 的一次性守卫 + 两道闸门共同兜住。
            _autoAuthGaveUp = false;
            // ⚠️ 2026-10-13：`_autoAuthRefreshCount` 以前**只加不清**（原代码里没有任何
            //    重置点）。一旦某次会话里累计换图超过 3 次，之后每一轮都会一进门就判
            //    "连着换了几张都没等到可用的图" 直接停手 —— 也就是越用越不灵。
            //    换页算新的一轮尝试，归零。防风暴靠的是两道闸门，不靠这个计数。
            _autoAuthRefreshCount = 0;
            if (_autoAuthWatchdog != null) _autoAuthWatchdog.Stop();
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

            // 「保存登录信息」提示：页面有那个勾才显示（见 UpdateSaveLoginHint）。
            // ⚠️ 必须放在"抠图/填表"之前 —— 它只读页面、不动任何状态，
            //    放这里是为了让用户**在自动登录跑起来之前**就看见这句话。
            //    万一它抛异常也绝不能挡住后面的填表（这是纯提示，坏了不该影响功能）。
            try
            {
                WinForms.HtmlDocument d = browser.Document;
                if (d != null) UpdateSaveLoginHint(All(d));
            }
            catch (Exception ex)
            {
                Log.Warn("刷新「保存登录信息」提示失败（不影响登录）: " + ex.Message);
            }

            // ⚠️ 顺序要紧：这里判定的是**上一轮**填的验证码（页面刚刚跳走了，
            //    说明用户点过登录）。必须排在"重新抠本页的图"之前 ——
            //    一旦开始抠新页面的图，上一轮的那张就被覆盖了，样本也就配不上对。
            MaybeRecordSample();

            // ⚠️⚠️ 页面换了 → 手里那张验证码图**立刻作废**（2026-10-12 海辰报的 bug：
            //   "它帮我自动登录填的验证码是上一次刷新的，不是最新的，导致一直失败"）。
            //   页面回传之后服务器那边的答案已经换成新图的了（no-cache，浏览器会重新下载），
            //   拿旧图上的字去填必然对不上。所以在这里清掉 —— 后面的提交路径于是
            //   只能重新抠一张，不可能再拿到隔页的旧图。
            _captchaPng = null;
            _captchaSrc = "";
            _captchaKey = "";

            // 页面换了，重新抠一次（抠不到就说明本页没有验证码）
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

        /// <summary>
        /// 取"谁在调我"——只用于日志。日志里光写"第 N 次提交"是查不出循环驱动者的，
        /// 2026-10-12 那次提交风暴就是靠这个标签定位的。
        /// </summary>
        private static string CallerTag()
        {
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(false);
                if (st.FrameCount > 2 && st.GetFrame(2) != null)
                    return st.GetFrame(2).GetMethod().Name;
            }
            catch { }
            return "?";
        }

        /// <summary>自测用：OnLoginClick 被调了几次（排查"额度被谁重置"用）。</summary>
        private int _loginClicks = 0;

        private void OnLoginClick()
        {
            _loginClicks++;
            // 用户主动点「一键登录」= 开一轮新的自动登录，重试次数重新算。
            // （不重置的话，上一轮用掉的次数会累积，点第二次就直接"次数用完"了。）
            _autoAuthAttempts = 0;
            _autoAuthRunning = false;
            _autoAuthWaitNext = false;
            _autoAuthTriedFresh = false;
            _autoAuthGaveUp = false;      // 新一轮 → "已经停手"的标记也要归零
            _autoAuthRefreshCount = 0;
            if (TryAutoFillAndSubmit(false)) _autoDoneThisLoad = true;
        }

        /// <summary>
        /// 现在这一刻，这个窗口还该不该"连上就自己关掉"。
        ///
        /// ⚠️ 2026-10-15 新增：把判据收成**一个**函数，供守护线程启动时和每次
        ///    轮询命中时共用。散在两处写必然漂移（本项目反复踩过的"判据分叉"）。
        /// </summary>
        private bool ShouldAutoCloseNow()
        {
            if (_keepOpenBySetting) return false;             // 用户选了"保持打开"
            if (!_programOpened && !_reauthMode) return false; // 用户自己开的窗口，不自动关
            return true;
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
            // ⚠️ 2026-10-15：判据统一走 ShouldAutoCloseNow()（见它的注释）——
            //    它同时管住三件事：用户选了"保持打开"、窗口是用户自己开的、
            //    以及程序自己开的窗口才该连上就关。
            if (!ShouldAutoCloseNow()) return;

            // ⚠️ 2026-10-15：这个守护线程**只能有一个**。
            //    复用后台窗口这条新路径上，SetReauthMode 和 SetStartupAutoClose 会对
            //    **同一个**窗口各调一次本方法 —— 不加这道闸就会起两个线程同时轮询，
            //    连上后两个都去 Close()，状态文案也会互相打架。
            if (_autoCloseGuardRunning) return;
            _autoCloseGuardRunning = true;

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

                    // ⚠️ 2026-10-15：连上的那一刻再确认一次"还该不该关" ——
                    //    守护线程启动之后、连上之前，情况可能已经变了：
                    //      · 用户刚取消了那个勾（_keepOpenBySetting）
                    //      · 或者用户接管了这个窗口（MarkUserTookOver：
                    //        它从"后台自动重连的隐形窗口"变成了"用户正看着的窗口"）
                    //    光在启动时判断一次会漏掉这些（用户会看到"我明明取消了它还关"）。
                    if (!ShouldAutoCloseNow()) return;

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

            // 页面上有验证码：自动提交开着就一路做完（认 → 填 → 点登录 → 有界重试），
            // 关着才退回老行为（只填不提交）。
            bool hasCc = PageHasCaptcha(all);

            // ⚠️ 2026-10-13 新增的诊断（按轮节流）：自动提交开着、账号密码也填上了，
            //    却没认出验证码框 —— 这时流程会掉到下面"只填不提交"的老路径，
            //    表现就是用户说的"一键登录只填信息、不点网页里的登录"。
            //    以前这个分支**一个字都不打**，日志里看起来像凭空消失。
            if (_autoSubmit && !hasCc && _noCcLoggedToken != _loadToken)
            {
                _noCcLoggedToken = _loadToken;
                Log.Warn("自动提交：这一页没认出验证码框（hasCc=false）—— 会退回「只填不提交」，"
                         + "请把这一行连同前后几行日志发给开发者");
            }
            // ⚠️ 这里**不能**再写 `&& !_autoAuthRunning`（2026-10-12 自测里踩到）：
            //    重试路径最后也是回到 TryAutoFillAndSubmit 再走一遍，而那时
            //    _autoAuthRunning 正是 true —— 条件不成立就会掉进下面"只填不提交"的老分支，
            //    表现就是"该重试的时候反而提交了一次空验证码"。
            //    "要不要重试、还能试几次"由 TryAutoSubmitWithCaptcha 自己把关。
            if (hasCc && _autoSubmit)
            {
                WinForms.HtmlElement sb = FindSubmit(all);
                if (TryAutoSubmitWithCaptcha(sb,
                        "档案命中：「" + profile.Url + "」已填 " + filled + " 项 —— "
                        + string.Join("、", done.ToArray())))
                    return true;
            }
            // 老行为（自动提交关着时）：填好 + 提交一次空表单把表单"激活"，
            // 让用户自己填完验证码点登录就能过。
            if (hasCc && !_submittedThisLoad)
            {
                _submittedThisLoad = true;
                if (SubmitAllowed()) { _oldPathSubmits++; _submitCalls++; SubmitFirstForm(all); }
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

                // ⚠️ 没有值可填、而页面上那个身份框还空着 -> 明确警告（2026-10-15）。
                //    真实血案：账号里「附加账号（学工号）」是空的，于是这个框一个字都不填，
                //    界面却说"已填好"，提交被学校拒 —— 用户完全不知道为什么。
                //    这里绝不自己猜一个值填进去（宁可少填，也不要填错框），只负责说清楚。
                UpdateIdentityHint(all, us, pw);

                WinForms.HtmlElement submit = FindSubmit(all);

                // ⚠️ 有图形验证码就**只填不提交** —— 见 HasCaptcha 的注释。
                // 验证码那格是空的，硬提交必然失败；而学校普遍对"连续登录失败"有次数限制，
                // 反复失败可能直接把账号锁一段时间。宁可让用户自己点一下。
                if (HasCaptcha(all))
                {
                    // 自动提交开着：认验证码 → 填进框 → 点「登录」，失败换一张重试（最多 3 次）。
                    // 这就是"一键登录点了没反应"的修复点 —— 老代码在这里直接 return，
                    // 一个字都不提交，用户只能自己去点网页里的登录。
                    if (_autoSubmit)
                    {
                        if (TryAutoSubmitWithCaptcha(submit,
                                "识别结果：账号框「" + Describe(us) + "」 密码框「" + Describe(pw) + "」"
                                + (us2 != null ? " 附加账号框「" + Describe(us2) + "」" : "")
                                + "；验证码 → 自动识别并提交"))
                            return true;
                    }

                    // 自动提交关着（用户选了保守行为）：维持"只填不提交"。
                    // 光填进去不够 —— ASP.NET WebForms 的验证码是服务端生成的，
                    // 必须把表单**提交**给服务器才能校验。所以：填好 ➜ 触发一次提交，
                    // 服务器返回"验证码错误!"。这样既不绕过验证码（还是得人来填），
                    // 又让用户填完验证码直接在页面上点「登录」就能过。
                    if (!_submittedThisLoad)
                    {
                        _submittedThisLoad = true;
                        if (SubmitAllowed()) { _oldPathSubmits++; _submitCalls++; SubmitForm(pw); }
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

                if (!SubmitAllowed())
                {
                    SetStatus("已经自动提交过 " + AutoAuthMaxAttempts + " 次都没成功 —— 不再继续试了，"
                        + "请手动点页面上的「登录」。");
                    return true;
                }
                SetStatus("已识别登录框，正在提交…");
                if (lblFound != null)
                {
                    lblFound.Text = "识别结果：账号框「" + Describe(us) + "」 密码框「" + Describe(pw) + "」"
                        + (us2 != null ? " 附加账号框「" + Describe(us2) + "」" : "")
                        + " 提交按钮「" + Describe(submit) + "」";
                }

                _submitCalls++;
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

        /// <summary>按钮上"明确不是登录"的词 —— 命中直接排除，绝不许点。</summary>
        private static readonly string[] NotSubmitWords = new string[] {
            "换一张", "换一换", "刷新", "看不清", "重置", "清空", "取消", "返回", "上一",
            "注册", "忘记", "找回", "发送", "获取", "重发", "关闭", "退出",
            "reset", "refresh", "cancel", "back", "register", "reload", "clear", "close"
        };

        /// <summary>按钮上"明确是登录"的词。</summary>
        private static readonly string[] SubmitWords = new string[] {
            "登录", "登陆", "认证", "上线", "连接", "确定", "提交", "进入",
            "login", "logon", "signin", "sign in", "connect", "submit"
        };

        /// <summary>取按钮的"字面"：input 看 value，其它看 InnerText。</summary>
        private static string ButtonLabel(WinForms.HtmlElement el)
        {
            string tag = TagOf(el);
            if (tag == "input")
            {
                string v = AttrOf(el, "value");
                if (v.Length > 0) return v.Trim();
            }
            try { return (el.InnerText ?? "").Trim(); }
            catch { return ""; }
        }

        private static bool ContainsAny(string s, string[] words)
        {
            string low = s.ToLowerInvariant();
            foreach (string w in words)
            {
                if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 一个按钮候选的**抽象描述**。
        ///
        /// ⚠️ 把决策从 HtmlElement 里抽出来，是为了让自测能**直接断言**这个决策 ——
        ///    否则"挑错按钮"这种事只能靠真机碰运气复现（这个 bug 已经藏了两轮了）。
        /// </summary>
        internal struct ButtonCand
        {
            public int Index;      // 原始下标（返回给调用方）
            public string Tag;     // input / button / a / …
            public string Type;    // submit / button / reset / …
            public string Label;   // 按钮上的字（input 取 value，其它取文字）
            public bool Disabled;
            public string Href;    // 只对 <a> 有意义：区分"导航链接"和"回传链接"
        }

        /// <summary>
        /// 这个 href 是不是"会导航走"的链接。
        ///
        /// ⚠️⚠️ 2026-10-13 这是真正那个 bug 的分界线（见 PickSubmitIndex 注释）：
        ///    · 空 / `./` / `login` / `#` / `http://…`  → **导航链接**，点它 = 整页重载，
        ///      已经填好的账号密码全被清空；
        ///    · `javascript:__doPostBack(...)`        → **页面内回传**（WebForms 的
        ///      LinkButton 就是这样渲染的），点它是提交表单，不是导航。
        /// </summary>
        private static bool IsNavigationHref(string href)
        {
            string h = (href ?? "").Trim();
            if (h.Length == 0) return true;      // ⚠️ <a href=""> 也会导航到当前地址
            return !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从候选里挑「登录按钮」的**纯决策**（不碰 DOM，可被自测直接调用）。
        /// 返回下标；**-1 = 分不清，宁可不点**。
        ///
        /// ⚠️⚠️ 2026-10-13 第二次重写 —— 上一版（"先认按钮文字"）**认错了人**。
        ///    真机页面（`http://10.6.6.6/login` 抓下来的真实 HTML）长这样：
        ///        &lt;a href="login" class="nav-link"&gt;连网登录&lt;/a&gt;      ← 导航栏链接
        ///        &lt;input type="submit" value="登录" id="MainContent_LoginBtn"&gt; ← 真按钮
        ///    导航栏排在文档**前面**，文字又含「登录」两个字，于是"按文字找"第一个撞上的
        ///    就是那个**导航链接**。点 `<a href="login">` 是**跳转**、不是提交表单 ——
        ///    结果就是海辰看到的"点一下，整个页面刷新、信息全没了"。
        ///    （上一版我以为凶是「换一张」，错了；是**导航链接**。日志里的 `按钮=a` 是铁证。）
        ///
        /// 现在的判定顺序（**可靠性从高到低，绝不拿导航链接当提交**）：
        ///   ① `input`/`button` + `type=submit` —— 真·提交按钮，唯一可靠的一类；
        ///      先挑文字明确的，退一步"只有一个候选"也认。
        ///   ② `input`/`button` + `type=button` —— 有些门户把登录做成普通按钮 + onclick。
        ///   ③ `<a>`：**只有 href 是 `javascript:`**（LinkButton 回传）才可能是一次提交；
        ///      `href="login"` / `"./"` / `"#"` 这类**一律不点**。
        ///   ④ 都不满足 → -1（交给用户手动点）。
        /// </summary>
        internal static int PickSubmitIndex(List<ButtonCand> cands)
        {
            // ① 真·提交按钮
            int onlySubmit = -1;
            int submitCnt = 0;
            foreach (ButtonCand c in cands)
            {
                if (c.Tag != "input" && c.Tag != "button") continue;
                if (c.Disabled) continue;
                if ((c.Type ?? "").ToLowerInvariant() != "submit") continue;

                string lb = (c.Label ?? "").Trim();
                if (ContainsAny(lb, NotSubmitWords)) continue;   // 「换一张」这类直接剔除

                submitCnt++;
                onlySubmit = c.Index;
                if (lb.Length > 0 && lb.Length <= 12 && ContainsAny(lb, SubmitWords)) return c.Index;
            }
            if (submitCnt == 1) return onlySubmit;

            // ② type=button（普通按钮 + onclick 提交的写法）
            int onlyBtn = -1;
            int btnCnt = 0;
            foreach (ButtonCand c in cands)
            {
                if (c.Tag != "input" && c.Tag != "button") continue;
                if (c.Disabled) continue;
                if ((c.Type ?? "").ToLowerInvariant() != "button") continue;

                string lb = (c.Label ?? "").Trim();
                if (lb.Length == 0 || lb.Length > 12) continue;
                if (ContainsAny(lb, NotSubmitWords)) continue;
                if (!ContainsAny(lb, SubmitWords)) continue;

                btnCnt++;
                onlyBtn = c.Index;
            }
            if (btnCnt == 1) return onlyBtn;

            // ③ <a>：只认"回传链接"（javascript:），导航链接一律不点
            foreach (ButtonCand c in cands)
            {
                if (c.Tag != "a") continue;
                if (c.Disabled) continue;
                if (IsNavigationHref(c.Href)) continue;          // ← 这条拦住的是那个导航链接

                string lb = (c.Label ?? "").Trim();
                if (lb.Length == 0 || lb.Length > 12) continue;
                if (ContainsAny(lb, NotSubmitWords)) continue;
                if (!ContainsAny(lb, SubmitWords)) continue;
                return c.Index;
            }

            // ④ 分不清就别动
            return -1;
        }

        /// <summary>
        /// 找页面上的「登录」按钮。
        ///
        /// ⚠️⚠️ 2026-10-13 重写 —— 这是海辰报的
        ///    「点了一键登录，信息都填对了，但一点登录**整个页面刷新、信息全被清空**」
        ///    的根因。
        ///
        /// 老实现第二行就是：
        ///     `if (type == "submit" || type == "button") return el;`
        /// 它按**文档顺序**返回遇到的第一个 submit/button，**完全不看这个按钮是干什么的**。
        /// 而这个门户是 ASP.NET WebForms —— **页面上每个服务器按钮都是 type=submit**，
        /// 「换一张」也是。于是一旦「换一张」在文档里排在前面，
        /// 程序点下去点的是"刷新验证码的 PostBack"：
        ///   · 页面整个回传重载 → 账号密码框被清空（WebForms 不保存文本框的值）；
        ///   · 用户看到的就是"点一下，页面刷了、信息全没了"；
        ///   · 而他**手动**点真正的「登录」是好的 —— 因为他点对了按钮。
        ///
        /// 决策本身在 PickSubmitIndex（纯函数，有自测钉住）。
        /// </summary>
        private static WinForms.HtmlElement FindSubmit(List<WinForms.HtmlElement> all)
        {
            List<ButtonCand> cands = new List<ButtonCand>();
            List<WinForms.HtmlElement> picked = new List<WinForms.HtmlElement>();

            for (int i = 0; i < all.Count; i++)
            {
                WinForms.HtmlElement el = all[i];
                string tag = TagOf(el);
                if (tag != "input" && tag != "button" && tag != "a") continue;

                ButtonCand c = new ButtonCand();
                c.Index = picked.Count;
                c.Tag = tag;
                c.Type = AttrOf(el, "type");
                c.Label = ButtonLabel(el);
                c.Disabled = IsDisabled(el);
                c.Href = (tag == "a") ? AttrOf(el, "href") : "";

                cands.Add(c);
                picked.Add(el);
            }

            int idx = PickSubmitIndex(cands);
            return (idx >= 0 && idx < picked.Count) ? picked[idx] : null;
        }

        /// <summary>
        /// 把页面上所有"像按钮"的元素列一遍（诊断用，**只在挑不出登录按钮时**调用）。
        ///
        /// 目的：让"挑不出按钮"这件事下次能一眼定位 —— 直接看到页面上有什么、
        /// 每个按钮写的什么字、是什么 type。没有它，日志里只有一句"找不到"，
        /// 等于白瞎一轮。
        /// </summary>
        private static string DescribeButtonCands(List<WinForms.HtmlElement> all)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (WinForms.HtmlElement el in all)
            {
                string tag = TagOf(el);
                if (tag != "input" && tag != "button" && tag != "a") continue;

                string type = AttrOf(el, "type");
                // input 里只关心按钮类的（text/password/hidden 都不是按钮）
                if (tag == "input")
                {
                    string t = type.ToLowerInvariant();
                    if (t != "submit" && t != "button" && t != "reset" && t != "image") continue;
                }

                if (n > 0) sb.Append(" ｜ ");
                sb.Append(tag);
                if (type.Length > 0) sb.Append("[type=").Append(type).Append("]");
                string id = AttrOf(el, "id");
                if (id.Length > 0) sb.Append("#").Append(id);
                string lbl = ButtonLabel(el);
                sb.Append(" 文字=「").Append(lbl.Length > 20 ? lbl.Substring(0, 20) : lbl).Append("」");
                if (IsDisabled(el)) sb.Append("(已禁用)");

                n++;
                if (n >= 15) { sb.Append(" ｜ …"); break; }
            }
            return (n == 0) ? "(一个按钮都没有)" : sb.ToString();
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

            // ⚠️ 2026-10-13 加 href：日志里光写 `按钮=a` **分不出是"导航链接"还是
            //    "回传链接"** —— 而这两者天差地别（前者一点整页重载、信息清空）。
            //    真凶就是这么从日志里溜过去的，必须让它在日志里现形。
            if (s.StartsWith("a", StringComparison.Ordinal))
            {
                string href = AttrOf(el, "href");
                if (href.Length > 40) href = href.Substring(0, 40) + "…";
                s = s + "(href=" + href + ")";
            }
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
