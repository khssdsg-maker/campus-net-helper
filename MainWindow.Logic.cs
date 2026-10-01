using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 主窗口 —— 业务逻辑部分。
    /// 职责：连接状态机、拨号/断开、网速采样、参数刷新、连接历史、托盘交互、配置读写。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 连接状态。WaitingLink = 网线没插好时的"待机"——
        /// 它和 Idle 的区别在于：待机时程序**不会去尝试拨号**，只在等网线接回来。
        /// （2026-09-29 海辰的需求："拔掉网线后程序别一直拉连接，插上网线再自动识别"）
        /// </summary>
        internal enum ConnState { Idle, Connecting, Connected, Disconnecting, Error, WaitingLink, Quiet }

        private ConnState _state = ConnState.Idle;

        private DispatcherTimer _tickTimer;
        private DispatcherTimer _speedTimer;
        private DateTime _connectedAt = DateTime.MinValue;

        private long _lastRx = 0;
        private long _lastTx = 0;
        private DateTime _lastSampleAt = DateTime.MinValue;
        private readonly List<double> _speedHistory = new List<double>();
        private const int SpeedHistoryMax = 90;

        private List<ConfigStore.Account> _accounts = new List<ConfigStore.Account>();
        private Dictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _history = new List<string>();
        private ConfigStore.Account _current = null;
        private bool _suppressAccountEvent = false;
        private bool _silentStart = false;
        private DateTime _lastAutoTry = DateTime.MinValue;

        /// <summary>
        /// 本次掉线后已经自动重试了几次。连上（或用户手动断开）就归零。
        /// 用来做重试退避：第一次别傻等 30 秒，先快速试 —— 见 NextRetryDelay()。
        /// </summary>
        private int _autoReconnectAttempts = 0;

        /// <summary>
        /// 自动重连是否已"武装"。只有满足下面任一条才允许自动重连：
        ///   · 用户主动点过「立即连接」并成功过（之后断了才自动接回来）
        ///   · 程序是被开机自启（静默模式）拉起来的
        /// 用户一旦手动断开就立刻解除武装 —— 否则会出现
        /// "我明明点了断开，半分钟后它自己又连上了"这种莫名其妙的行为。
        /// </summary>
        private bool _autoReconnectArmed = false;
        private HealthWindow _healthWindow = null;
        private SettingsWindow _settingsWindow = null;
        private SpeedTestWindow _speedTestWindow = null;
        private WebAuthWindow _webAuthWindow = null;

        /// <summary>连接质量监测（丢包率 / 延迟）。后台线程采样，实现见 QualityMonitor.cs。</summary>
        private readonly QualityMonitor _quality = new QualityMonitor();

        /// <summary>心跳保活（防学校设备"空闲下线"）。后台线程，实现见 KeepAlive.cs。</summary>
        private readonly KeepAlive _keepAlive = new KeepAlive();
        /// <summary>
        /// 退出流程已开始。
        ///
        /// ⚠️ 它是给**后台线程**看的：拨号 / 断开这类工作线程完成时会 Dispatcher.Invoke 回来，
        ///    而退出时 Dispatcher 已经关了，那个 Invoke 会抛 TaskCanceledException ——
        ///    如果它的 catch 里还再 Invoke 一次，就成了后台线程未捕获异常，直接崩进程。
        ///
        /// volatile：UI 线程写、后台线程读，不加可能读到旧值（JIT 会把它缓存在寄存器里）。
        /// </summary>
        private volatile bool _closing = false;

        /// <summary>系统正在关机 / 注销（见 OnClosing 里的说明，这个标志是保命用的）。</summary>
        private bool _sessionEnding = false;

        /// <summary>「第二个实例叫我出来」用的自定义窗口消息（由 App 注册并投递）。</summary>
        private uint _activateMsg;

        // ==================================================================
        // 构造
        // ==================================================================

        public MainWindow() : this(false) { }

        public MainWindow(bool silent)
        {
            _silentStart = silent;

            BuildUi();

            // 窗口圆角要等 hwnd 建好之后才能设
            // 顺便装窗口消息钩子：静默启动时 hwnd 是 App 用 EnsureHandle() 建的，
            // 这个事件同样会触发，钩子不会漏装。
            SourceInitialized += delegate(object s, EventArgs e)
            {
                ApplyRoundedCorners();
                HookActivateMessage();
            };

            // 最大化/还原时同步顶栏那个按钮的图标
            StateChanged += delegate(object s, EventArgs e) { UpdateMaximizeGlyph(); };

            // 从托盘点开主窗口时立刻核对一次联网状态 —— 用户切过来就是为了看结果，
            // 不该让他等下一轮轮询（最多 5 秒）才看到正确显示。
            Activated += delegate(object s, EventArgs e)
            {
                // 切回来先按挂钟把时长对齐（拖动/点击期间可能漏过 tick）
                if (_state == ConnState.Connected || IsPortalMode()) RefreshOnlineTime();
                if (IsPortalMode()) RefreshPortalOnlineNow();
            };

            // 用户一动窗口就补刷时长。
            // 拖动窗口、点标题栏这些动作会让 Windows 进入模态循环，DispatcherTimer 被压住不发 ——
            // 用户看到的正是"我盯着它，时间却不动"。这些事件触发的时刻刚好补上。
            PreviewMouseDown += OnUserActivity;
            PreviewMouseMove += OnUserActivity;
            PreviewKeyDown += OnUserActivity;
            LoadAll();
            ApplyTheme();
            InitTray();
            InitTimers();
            _quality.Updated += OnQualityUpdated;

            Visibility = silent ? Visibility.Hidden : Visibility.Visible;
            ShowInTaskbar = !silent;
            Closing += OnClosing;

            // 关机/注销时先记一笔（这个事件一定发生在窗口关闭之前），
            // OnClosing 才能分清"用户关窗口"和"系统要关机"——两者处理方式必须不同。
            if (Application.Current != null)
            {
                Application.Current.SessionEnding +=
                    delegate(object s, System.Windows.SessionEndingCancelEventArgs ev)
                    {
                        _sessionEnding = true;
                    };
            }

            RefreshAccountList();
            RefreshParamCards();
            RefreshHistoryUi();

            // 网页认证模式：启动时先探一次（后台），这样窗口一显示就带着真实结果，
            // 而不是先给人看一秒「尚未连接」再跳成「已认证上网」。
            if (IsPortalMode()) NetProbe.WarmUpAsync();

            ApplyState(ConnState.Idle, "尚未连接", "选择账号后点击「立即连接」");

            // 启动时如果已经存在拨号连接（例如用户先用学校客户端拨上了），
            // 要把状态同步过来 —— 否则界面显示"尚未连接"，与实际不符。
            string existing = DialEngine.GetConnectedDialName();
            if (!string.IsNullOrEmpty(existing))
            {
                _autoReconnectArmed = true;
                _connectedAt = DateTime.Now;
                ApplyState(ConnState.Connected, "已连接",
                    "检测到已建立的宽带连接（" + existing + "）");
                Log.Info("启动时检测到已存在的拨号连接: " + existing);
            }

            if (silent)
            {
                Log.Info("静默启动（开机自启）：进入托盘守护模式");
                _autoReconnectArmed = true;

                // ⚠️ 没有账号就必须说出来。
                //    2026-09-29 的真实教训：账号文件损坏后读到 0 个账号，
                //    程序依旧"静默启动"，不拨号也不提示，用户第二天才发现没网。
                if (_accounts.Count == 0)
                {
                    Log.Warn("静默启动：没有任何已保存的账号，无法自动连接");
                    ShowBalloon("没有可用账号，没能自动连接",
                        "开机自启已经启动了，但程序里没有任何已保存的账号。\n"
                        + "双击托盘图标打开主界面 →「管理账号」添加一个即可。");
                }
                else if (AutoReconnectEnabled() && string.IsNullOrEmpty(existing))
                {
                    // 开机时先确认网线接着再拨 —— 否则刚开机就白试一次，
                    // 网线没插好时应该直接待机等它
                    if (CheckLinkNow()) TryAutoReconnect();
                    else EnterWaitingLink();
                }
            }
        }

        // ==================================================================
        // 配置
        // ==================================================================

        // ==================================================================
        // 自测出口（仅供 UITest 使用，界面代码不用它们）
        // ==================================================================

        /// <summary>当前状态条标题的文案。</summary>
        internal string DebugStatusTitle()
        {
            return lblStatusTitle == null ? "" : (lblStatusTitle.Text ?? "");
        }

        /// <summary>当前在线时长的文案。</summary>
        internal string DebugOnlineTime()
        {
            return lblOnlineTime == null ? "" : (lblOnlineTime.Text ?? "");
        }

        /// <summary>
        /// 把"在线起点"往前挪一段，模拟"已经在线 N 秒"。
        ///
        /// 为什么需要它：自测里没有真实时间流逝（DispatcherTimer 不会自己跳），
        /// 而"时长确实在走"这件事必须验证 —— 之前的 bug 正是"看着不动"。
        /// 直接改 _connectedAt 比等待 1 秒真实时间可靠得多。
        /// </summary>
        /// <summary>
        /// 把"在线起点"往前挪一段，模拟"已经在线 N 秒"。
        ///
        /// 为什么需要它：自测里没有真实时间流逝（DispatcherTimer 不会自己跳），
        /// 而"时长确实在走"这件事必须验证 —— 之前的 bug 正是"看着不动"。
        /// 直接改 _connectedAt 比等待 1 秒真实时间可靠得多。
        ///
        /// ⚠️ 语义是**绝对**的：调用后显示值必然等于 N 秒。
        ///    （相对累加会让自测算错 —— 第一版就踩了：
        ///     先挪 125 秒、再挪 3645 秒，起点一共被推了 3770 秒，
        ///     显示成 01:02:50，白白让用例红了。）
        /// </summary>
        internal void DebugSetOnlineSeconds(int seconds)
        {
            _connectedAt = DateTime.Now.AddSeconds(-seconds);
        }

        /// <summary>
        /// 模拟"用户动了一下窗口"这条路径，触发和真实鼠标/键盘事件完全相同的处理。
        ///
        /// 自测必须走这条路径而不是直接调 RefreshOnlineTime ——
        /// 要测的正是"用户操作时界面会不会补刷"，绕过 OnUserActivity 就测不到了。
        /// </summary>
        internal void DebugPokeUserActivity()
        {
            OnUserActivity(this, EventArgs.Empty);
        }

        /// <summary>清掉补刷节流，让自测能连续触发两次。</summary>
        internal void DebugResetActivityThrottle()
        {
            _lastActivityRefresh = DateTime.MinValue;
        }

        /// <summary>当前网速卡片上显示的上下行文案（自测用来确认曲线确实是活的）。</summary>
        internal string DebugSpeedDown()
        {
            return lblSpeedDown == null ? "" : (lblSpeedDown.Text ?? "");
        }

        internal string DebugSpeedUp()
        {
            return lblSpeedUp == null ? "" : (lblSpeedUp.Text ?? "");
        }

        internal string DebugSpeedTotal()
        {
            return valSpeed == null ? "" : (valSpeed.Text ?? "");
        }

        /// <summary>曲线上的采样点数。</summary>
        internal int DebugSpeedPointCount()
        {
            return _speedHistory.Count;
        }

        /// <summary>当前网速采样盯的是哪块网卡（网络接口 Id）。</summary>
        internal string DebugSpeedAdapterId()
        {
            return _speedAdapterId ?? "";
        }

        /// <summary>手工触发一次采样（自测用 —— 不依赖 DispatcherTimer 真的跳）。</summary>
        internal void DebugSampleSpeedNow()
        {
            SampleSpeed();
        }

        /// <summary>当前「在线起点」是否已设（自测用来判断状态机有没有真的记上起点）。</summary>
        internal bool DebugHasOnlineStart()
        {
            return _connectedAt != DateTime.MinValue;
        }

        /// <summary>质量卡片旁那行"看的是哪块网卡"的文字。</summary>
        internal string DebugAdapterLabel()
        {
            return _primaryAdapterLabel ?? "";
        }

        /// <summary>
        /// 把主页强制切到"认证成功"的样子，供自测检查文案分支。
        /// 不碰真实探测，纯展示逻辑。
        /// </summary>
        internal void SimulatePortalState(bool online)
        {
            if (online)
            {
                _portalOnline = true;
                if (_connectedAt == DateTime.MinValue) _connectedAt = DateTime.Now;
                ApplyState(ConnState.Connected, "已认证上网",
                    "网页认证已生效，可以正常上网");
                RefreshParamCards();
            }
            else
            {
                _portalOnline = false;
                _connectedAt = DateTime.MinValue;
                ApplyState(ConnState.Idle, "未联网",
                    "还没通过网页认证，点「打开认证页」登录一下");
            }
        }

        private void LoadAll()
        {
            _accounts = ConfigStore.LoadAccounts();
            _settings = ConfigStore.LoadSettings();

            string histRaw = ConfigStore.GetString(_settings, "History", "");
            if (!string.IsNullOrEmpty(histRaw))
            {
                foreach (string s in histRaw.Split('\u0001'))
                {
                    if (!string.IsNullOrEmpty(s) && _history.Count < 30) _history.Add(s);
                }
            }
        }

        private void SaveAll()
        {
            string msg;
            ConfigStore.SaveAccounts(_accounts, out msg);
            if (!string.IsNullOrEmpty(msg)) Log.Warn(msg);

            ConfigStore.SetBool(_settings, "AutoReconnect", AutoReconnectEnabled());
            ConfigStore.SetBool(_settings, "Silent", SilentEnabled());
            _settings["ReconnectInterval"] = ReconnectInterval().ToString();
            _settings["KeepAlive"] = KeepAliveEnabled() ? "1" : "0";
            _settings["KeepAliveInterval"] = KeepAliveIntervalMinutes().ToString();
            _settings["LastAccount"] = _current != null ? _current.Name : "";

            var hb = new System.Text.StringBuilder();
            foreach (string h in _history)
            {
                if (hb.Length > 0) hb.Append('\u0001');
                hb.Append(h);
            }
            _settings["History"] = hb.ToString();

            ConfigStore.SaveSettings(_settings, out msg);
            if (!string.IsNullOrEmpty(msg)) Log.Warn(msg);
        }

        internal Dictionary<string, string> SettingsSnapshot()
        {
            if (_settings == null) _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, string>(_settings, StringComparer.OrdinalIgnoreCase);
        }

        internal bool AutoReconnectEnabled()
        {
            return ConfigStore.GetBool(_settings, "AutoReconnect", true);
        }

        /// <summary>
        /// 「开机自动启动（登录后静默运行）」是否生效。
        ///
        /// ⚠️ 判定依据是**「启动」文件夹里到底有没有副本**，不是配置里的布尔值。
        /// 2026-09-28 发现：这个勾选框此前只往配置写了个 bool，从未真正创建任何自启项 ——
        /// 用户勾了也白勾（配置是 true、开机并不启动），而 v1.1.0 的说明书还明确写了
        /// "勾上开机就自动连上"，属于对外承诺与实现不符。改为读实际状态后，
        /// 用户手动删掉副本、或装了计划任务（安装时会移除副本）这两种情况也能如实反映。
        /// </summary>
        internal bool SilentEnabled()
        {
            return AutostartHelper.StartupCopyExists();
        }

        internal int ReconnectInterval()
        {
            string raw = ConfigStore.GetString(_settings, "ReconnectInterval", "30");
            int iv;
            if (int.TryParse((raw ?? "").Trim(), out iv))
            {
                if (iv < 5) iv = 5;
                if (iv > 600) iv = 600;
                return iv;
            }
            return 30;
        }

        internal bool KeepAliveEnabled()
        {
            return ConfigStore.GetBool(_settings, "KeepAlive", true);
        }

        internal int KeepAliveIntervalMinutes()
        {
            string raw = ConfigStore.GetString(_settings, "KeepAliveInterval", "3");
            int v;
            if (int.TryParse((raw ?? "").Trim(), out v))
            {
                if (v < 1) v = 1;
                if (v > 60) v = 60;
                return v;
            }
            return 3;
        }

        /// <summary>
        /// 掉线后等多久再重试。
        ///
        /// 原来是一上来就等 30 秒 —— 用户会实打实断半分钟，体验很差。
        /// 改成先快速试两次：绝大多数掉线是瞬时抖动（交换机闪断、学校设备重载），
        /// 3 秒内就能接回来。
        ///
        /// 但**不能一直快** —— 连续失败通常意味着不是抖动而是硬故障
        /// （691 密码错、678 网线没插、或者拨号过于频繁被限速），
        /// 这时候高频重试只会让情况更糟（学校的限速是越试越久的）。
        /// 所以两次之后就退回用户设定的间隔。
        /// </summary>
        private int NextRetryDelay()
        {
            if (_autoReconnectAttempts == 0) return 3;
            if (_autoReconnectAttempts == 1) return 10;
            return ReconnectInterval();
        }

        /// <summary>
        /// 设置窗口保存后回调。
        /// 返回值是需要额外告知用户的一句话（一切正常则返回空串），由调用方贴到"设置已保存"后面。
        /// </summary>
        internal string ApplySettingsFromWindow(bool autoReconnect, bool silent, bool closeToTray,
            int interval, bool keepAlive, int keepAliveMinutes,
            bool nightQuiet, string quietStart, string quietEnd,
            string authMode, string portalStartupBehavior)
        {
            ApplySettingsCore(autoReconnect, closeToTray, interval, keepAlive, keepAliveMinutes,
                nightQuiet, quietStart, quietEnd, authMode, portalStartupBehavior);

            string note = ReconcileStartupCopy(silent);

            Log.Info("设置已更新: 自动重连=" + autoReconnect + " 开机自启=" + silent
                + " 关闭到托盘=" + closeToTray + " 间隔=" + interval
                + " 保活=" + keepAlive + " 保活间隔=" + keepAliveMinutes + "分钟"
                + " 免打扰=" + (nightQuiet ? (quietStart + "-" + quietEnd) : "关")
                + " 认证方式=" + AuthMode() + " 网页自启行为=" + PortalStartupBehavior());
            return note;
        }

        /// <summary>
        /// 自动保存用的静默版本：只落盘 + 让开关立刻生效，**不碰开机自启的启动副本**。
        ///
        /// 为什么不走 ApplySettingsFromWindow：那个函数会在"开机自启"变化时创建/删除
        /// 「启动」文件夹里的副本，还可能因为文件被占用而返回一段要弹给用户看的说明。
        /// 自动保存是用户随手改个开关触发的，不适合做这种有副作用、还可能弹框的操作 ——
        /// 那种事留给底部那个明确的「保存设置」按钮。
        /// </summary>
        internal void ApplySettingsFromWindowSilent(bool autoReconnect, bool silent, bool closeToTray,
            int interval, bool keepAlive, int keepAliveMinutes,
            bool nightQuiet, string quietStart, string quietEnd,
            string authMode, string portalStartupBehavior)
        {
            ApplySettingsCore(autoReconnect, closeToTray, interval, keepAlive, keepAliveMinutes,
                nightQuiet, quietStart, quietEnd, authMode, portalStartupBehavior);
        }

        /// <summary>设置落盘 + 让"立刻生效"的开关动起来（不含开机自启副本的处理）。</summary>
        private void ApplySettingsCore(bool autoReconnect, bool closeToTray,
            int interval, bool keepAlive, int keepAliveMinutes,
            bool nightQuiet, string quietStart, string quietEnd,
            string authMode, string portalStartupBehavior)
        {
            ConfigStore.SetBool(_settings, "AutoReconnect", autoReconnect);
            ConfigStore.SetBool(_settings, "CloseToTray", closeToTray);
            _settings["ReconnectInterval"] = interval.ToString();
            ConfigStore.SetBool(_settings, "KeepAlive", keepAlive);
            _settings["KeepAliveInterval"] = keepAliveMinutes.ToString();
            ConfigStore.SetBool(_settings, "NightQuiet", nightQuiet);
            _settings["NightQuietStart"] = quietStart ?? "23:30";
            _settings["NightQuietEnd"] = quietEnd ?? "07:00";

            // 认证方式：只接受 dial / portal 两个值，其余一律回落 dial（防手改配置写坏）
            _settings["AuthMode"] = string.Equals(authMode, "portal", StringComparison.OrdinalIgnoreCase)
                ? "portal" : "dial";
            _settings["PortalStartupBehavior"] = string.Equals(portalStartupBehavior, "keep", StringComparison.OrdinalIgnoreCase)
                ? "keep" : "auto";

            SaveAll();

            // 开关可能刚被改掉，立刻生效，不用等下次连接状态变化
            _keepAlive.SetActive(_state == ConnState.Connected && keepAlive, keepAliveMinutes);
        }

        /// <summary>
        /// 按勾选状态落实（或撤销）「启动」文件夹里的自启副本，返回给用户看的说明（无需说明则返回空串）。
        ///
        /// 这么做是因为勾选框原先只是个空开关。撤销时若程序正从那份副本运行，文件被占用删不掉 ——
        /// 必须如实报出来，否则用户以为关了、下次登录照样弹窗。
        ///
        /// ⚠️ 这里**不手写** Silent 标志，只在文件操作**之后**调 SaveAll()：
        ///    SaveAll() 会用 SilentEnabled()（=「启动」文件夹里到底有没有副本）反推该标志。
        ///    曾经写成"先 SetBool 再 SaveAll"，结果 SaveAll 立刻用旧的实际情况把它覆盖回去 ——
        ///    取消自启时留下 Silent=1 却没副本的假状态。
        /// </summary>
        private string ReconcileStartupCopy(bool want)
        {
            string msg;
            bool hasTask = AutostartHelper.IsTaskInstalled();

            if (want)
            {
                if (hasTask)
                {
                    // 计划任务已在管开机自启。再放一份启动副本 = 登录时拉起两个实例、弹两次框，
                    // 所以拒绝创建，并让 SaveAll 把标志同步成实际情况，不留"看着像开了"的假状态。
                    SaveAll();
                    Log.Warn("开机自启：已安装计划任务，拒绝再创建启动副本（避免登录时双实例）");
                    return "「开机自启（管理员权限）」的计划任务已经装好了，开机自启本来就是生效的，"
                        + "所以这次没有重复加到「启动」文件夹（两个一起会在登录时启动两次）。";
                }

                if (!AutostartHelper.EnsureStartupCopy(out msg))
                {
                    SaveAll();
                    Log.Warn("开机自启：创建启动副本失败 — " + msg);
                    return "开机自动启动没能设置成功：" + msg;
                }

                SaveAll();
                Log.Info("开机自启：启动副本已就位 — " + msg);
                return "";
            }

            if (!AutostartHelper.StartupCopyExists())
            {
                SaveAll();
                return "";
            }

            if (AutostartHelper.RemoveStartupCopy(out msg))
            {
                SaveAll();
                Log.Info("开机自启：启动副本已移除 — " + msg);
                return "";
            }

            SaveAll();
            Log.Warn("开机自启：移除启动副本失败 — " + msg);
            return "开机自动启动没能取消：程序现在正从「启动」文件夹里那份副本运行，文件被占用删不掉。"
                + "请右键托盘图标退出程序，改从桌面快捷方式（或安装目录里的 exe）打开后再取消一次。";
        }

        internal bool CloseToTrayEnabled()
        {
            return ConfigStore.GetBool(_settings, "CloseToTray", true);
        }

        // ==================================================================
        // 认证方式（拨号 / 网页认证）
        // ==================================================================

        /// <summary>
        /// 认证方式。
        ///   "dial"   = 系统拨号（PPPoE），可静默自启、自动重连
        ///   "portal" = 网页认证，需要人填验证码，无法静默
        ///
        /// 默认 dial —— 保持老用户的原行为，升级后不会因为选错模式而上不了网。
        /// </summary>
        internal string AuthMode()
        {
            string v = ConfigStore.GetString(_settings, "AuthMode", "dial");
            if (string.Equals(v, "portal", StringComparison.OrdinalIgnoreCase)) return "portal";
            return "dial";
        }

        internal bool IsPortalMode()
        {
            return AuthMode() == "portal";
        }

        /// <summary>
        /// 网页认证模式下，开机自启时的行为：
        ///   "auto"   = 弹出认证窗口，用户登完自己关（默认）
        ///   "keep"   = 弹出认证窗口并保持打开（可反复用）
        /// </summary>
        internal string PortalStartupBehavior()
        {
            string v = ConfigStore.GetString(_settings, "PortalStartupBehavior", "auto");
            if (string.Equals(v, "keep", StringComparison.OrdinalIgnoreCase)) return "keep";
            return "auto";
        }

        internal string WebAuthUrl()
        {
            // 三级兜底，顺序不能乱：
            //   ① 网址清单里"最近用过"的那条（用户存过就用他的）
            //   ② 旧版本的 WebAuthUrl 设置项（升级上来的老配置，别丢）
            //   ③ 编译时的默认值（本校专属，放在 SiteConfig，不进公开仓库）
            string fromList = WebUrlStore.MostRecent();
            if (!string.IsNullOrEmpty(fromList)) return fromList;

            string v = ConfigStore.GetString(_settings, "WebAuthUrl", "");
            if (!string.IsNullOrEmpty(v)) return v;

            return SiteConfig.DefaultWebAuthUrl;
        }

        internal void SaveWebAuthUrl(string url)
        {
            string clean = WebUrlStore.Clean(url);
            if (clean.Length == 0) return;

            // 同时写两处：
            //   · 清单 —— 新版的数据源，能存多条
            //   · 旧设置项 —— 万一用户回退到旧版本，还认得出他填的地址
            // 两处都写一遍的代价可以忽略，换来的是"降级不丢配置"。
            string msg;
            WebUrlStore.Touch(clean);
            if (!WebUrlStore.Contains(clean)) WebUrlStore.Save(clean, "", out msg);

            _settings["WebAuthUrl"] = clean;
            SaveAll();
        }

        /// <summary>
        /// 把旧版单条网址搬进清单（只在清单为空时搬一次）。
        /// 升级用户第一次打开认证窗口时调用 —— 他会发现自己填过的地址还在。
        /// </summary>
        internal void MigrateLegacyUrl()
        {
            try
            {
                if (WebUrlStore.LoadAll().Count > 0) return;   // 清单已有内容，不插手

                string legacy = ConfigStore.GetString(_settings, "WebAuthUrl", "");
                if (string.IsNullOrEmpty(legacy)) return;

                string msg;
                if (WebUrlStore.Save(legacy, "原来的地址", out msg))
                {
                    Log.Info("已把旧版设置的认证网址搬进网址清单: " + legacy);
                }
            }
            catch { }
        }

        /// <summary>网址清单快照（给认证窗口填下拉框用）。</summary>
        internal List<WebUrlStore.Entry> WebUrlsSnapshot()
        {
            return WebUrlStore.LoadAll();
        }

        /// <summary>存一条网址进清单。</summary>
        internal bool SaveWebUrl(string url, string name, out string message)
        {
            bool ok = WebUrlStore.Save(url, name, out message);
            if (ok)
            {
                // 存了就等于"要用它"，顺手同步到旧设置项
                _settings["WebAuthUrl"] = WebUrlStore.Clean(url);
                SaveAll();
            }
            return ok;
        }

        /// <summary>从清单删一条网址。</summary>
        internal bool DeleteWebUrl(string url, out string message)
        {
            return WebUrlStore.Delete(url, out message);
        }

        // ==================================================================
        // 字段档案（网页认证：把"哪个框填什么"教给程序）
        // ==================================================================

        /// <summary>
        /// 让当前打开的认证窗口扫一遍页面，返回扫到的字段（带推断标签）。
        /// 没有打开认证窗口时返回空列表。
        /// </summary>
        internal List<FieldProfileStore.FieldProfile> ScanWebAuthFields()
        {
            try
            {
                if (_webAuthWindow == null || !_webAuthWindow.IsLoaded) return new List<FieldProfileStore.FieldProfile>();
                return _webAuthWindow.ScanFields();
            }
            catch (Exception ex)
            {
                Log.Warn("扫描认证页字段失败: " + ex.Message);
                return new List<FieldProfileStore.FieldProfile>();
            }
        }

        /// <summary>打开字段档案窗口（供认证窗口调用）。</summary>
        internal void OpenFieldProfileWindow(string url, List<FieldProfileStore.FieldProfile> scanned)
        {
            var w = new FieldProfileWindow(this, url, scanned);
            w.ShowDialog();
            if (w.Saved && _webAuthWindow != null && _webAuthWindow.IsLoaded)
            {
                _webAuthWindow.OnProfileSaved();
            }
        }

        /// <summary>账号列表的快照 —— 给网页认证窗口填下拉框用（不直接把内部列表交出去）。</summary>
        internal List<ConfigStore.Account> AccountsSnapshot()
        {
            return new List<ConfigStore.Account>(_accounts);
        }

        internal void OpenWebAuthWindow()
        {
            if (_webAuthWindow != null && _webAuthWindow.IsLoaded)
            {
                _webAuthWindow.Activate();
                return;
            }

            _webAuthWindow = new WebAuthWindow(this);
            _webAuthWindow.Owner = this;
            _webAuthWindow.Closed += delegate(object s, EventArgs a)
            {
                _webAuthWindow = null;
                RefreshPortalOnlineNow();
            };
            _webAuthWindow.Show();
        }

        /// <summary>
        /// 开机自启走网页认证时调用的入口。
        ///
        /// 与 OpenWebAuthWindow 的区别：
        ///   · 主窗口当前是隐藏的（静默启动），窗口不能挂 Owner = 隐藏的主窗口，
        ///     否则激活时会被一起带出来 / 抢焦点。所以这里**不设 Owner**。
        ///   · 会把"登录完成后自动关闭"的意图传下去（对应设置里的开机行为选项）。
        /// </summary>
        internal void OpenWebAuthWindowOnStartup()
        {
            if (_webAuthWindow != null && _webAuthWindow.IsLoaded)
            {
                _webAuthWindow.Activate();
                return;
            }

            bool autoClose = PortalStartupBehavior() != "keep";

            _webAuthWindow = new WebAuthWindow(this);
            _webAuthWindow.SetStartupAutoClose(autoClose);
            _webAuthWindow.Closed += delegate(object s, EventArgs a)
            {
                _webAuthWindow = null;
                // 认证窗口一关就立刻核对一次 —— 用户刚登完，主页该马上反映结果
                RefreshPortalOnlineNow();
            };
            _webAuthWindow.Show();
            _webAuthWindow.Activate();
        }

        // ==================================================================
        // 定时器
        // ==================================================================

        private void InitTimers()
        {
            // ⚠️ 在线时长（以及其它每秒的界面刷新）**不能只靠定时器**。
            //
            // 用户报的问题（2026-09-30）：
            //   "呼出主页时在线时长不动，放在后台时时间才同步。"
            // 原因：
            //   DispatcherTimer 是在消息泵空闲时才派发的。用户拖动窗口 / 按住标题栏 /
            //   点击时，Windows 进入"模态消息循环"，WM_TIMER 被压着不发 ——
            //   Dispatcher 优先级 4（Background）甚至 5（Normal）的定时器回调都可能被
            //   一拖好几秒。而后台时窗口不接收这类消息，反而跑得正常。
            //
            // 修法（两层，缺一不可）：
            //   ① 时长按"挂钟差"算 —— 起点是 _connectedAt，显示值永远 = 现在 - 起点。
            //      即使中途漏掉了 N 次 tick，只要派发过一次就立刻跳到正确的值，
            //      **自动补齐，不会累积偏差**。（见 RefreshOnlineTime）
            //   ② 把刷新的优先级提到 Render，并额外挂 Keyboard/Mouse 的 UI 事件驱动，
            //      让用户一动窗口就顺手刷一次，不用等下一个 tick。
            _tickTimer = new DispatcherTimer(DispatcherPriority.Render);
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.Tick += (s, e) => OnTick();
            _tickTimer.Start();

            _speedTimer = new DispatcherTimer(DispatcherPriority.Render);
            _speedTimer.Interval = TimeSpan.FromSeconds(1);
            _speedTimer.Tick += (s, e) => SampleSpeed();
            _speedTimer.Start();
        }

        /// <summary>
        /// "用户正在操作窗口"时顺手刷一次界面。
        ///
        /// 不和定时器重复劳动：这些事件只在真的有输入时触发，
        /// 而输入恰好就是会让消息泵卡住的那种场景 —— 正是最需要补刷的时刻。
        /// 加了节流：MouseMove 触发极密，没必要每个像素都算一遍时长（一秒刷一次足够）。
        /// </summary>
        private DateTime _lastActivityRefresh = DateTime.MinValue;

        private void OnUserActivity(object sender, EventArgs e)
        {
            if (_closing) return;
            if (_state != ConnState.Connected && !IsPortalMode()) return;
            if (_connectedAt == DateTime.MinValue) return;

            // 节流：同一秒内只刷一次
            DateTime now = DateTime.Now;
            if ((now - _lastActivityRefresh).TotalMilliseconds < 900) return;
            _lastActivityRefresh = now;

            RefreshOnlineTime();
        }

        private void OnTick()
        {
            bool quiet = InNightQuiet();

            // 网页认证模式走一条完全不同的路 —— 它没有拨号会话可看，
            // 唯一可靠的判据就是"能不能真的上网"。
            if (IsPortalMode())
            {
                TickPortal(quiet);
                return;
            }

            // 免打扰时段的进出：只在状态变化时处理一次（否则每秒都要动检测开关）
            if (quiet != _quietActive)
            {
                _quietActive = quiet;
                if (quiet)
                {
                    // 暂停质量检测（不再 ping）—— 进入的日志由 EnterQuiet 写，这里不重复
                    _quality.SetActive(false);
                }
                else
                {
                    Log.Info("夜间免打扰时段结束，恢复检测与自动连接");
                    if (_state == ConnState.Quiet)
                    {
                        ApplyState(ConnState.Idle, "尚未连接", "免打扰时段已结束，正在恢复…");
                    }
                    _quality.SetActive(_state == ConnState.Connected);
                }
            }

            if (_state == ConnState.Connected)
            {
                if (_connectedAt != DateTime.MinValue) RefreshOnlineTime();

                if (!DialEngine.IsDialConnected())
                {
                    Log.Warn("检测到拨号连接已断开" + (quiet ? "（夜间免打扰中，暂不重连）" : ""));
                    AddHistory("连接断开");
                    _connectedAt = DateTime.MinValue;
                    if (lblOnlineTime != null) lblOnlineTime.Text = "";
                    if (quiet)
                    {
                        // 免打扰时段：不重连，安静等着时段结束
                        EnterQuiet();
                    }
                    else if (CheckLinkNow())
                    {
                        // 顺便看一眼是不是网线被拔了 —— 是的话直接进待机，
                        // 别在"连接已断开 → 重试 → 又断开"之间来回跳
                        ApplyState(ConnState.Idle, "连接已断开", "拨号连接已中断");
                    }
                    else
                    {
                        EnterWaitingLink();
                    }
                    RefreshParamCards();
                    return;
                }

                RefreshParamCards();
            }

            // 免打扰时段：不检测、不重试、不弹提示（已经连着的网络保持不断）
            if (quiet)
            {
                if (_state != ConnState.Connected) EnterQuiet();
                return;
            }

            if (_autoReconnectArmed
                && (_state == ConnState.Idle || _state == ConnState.Error
                    || _state == ConnState.WaitingLink)
                && AutoReconnectEnabled() && _current != null)
            {
                if (!DialEngine.IsDialConnected())
                {
                    RefreshLinkState();   // 后台刷（有 2 秒缓存，不会每秒都查 WMI）

                    // 网线没插好：不重试。重试必然失败，还平白给学校攒失败次数。
                    if (!_linkOk)
                    {
                        EnterWaitingLink();
                        return;
                    }

                    // 刚从"等网线"恢复过来：不等退避，立刻连
                    if (_state == ConnState.WaitingLink)
                    {
                        _waitingSince = DateTime.MinValue;
                        Log.Info("检测到网线已接入，开始自动连接");
                        _autoReconnectAttempts = 0;
                        _lastAutoTry = DateTime.MinValue;
                    }

                    TryAutoReconnect();
                }
            }
        }

        // ==================================================================
        // 网页认证模式的主页状态
        // ==================================================================

        /// <summary>连续探测失败多少次才判定掉线。探测本身有抖动，一次失败不能作数。</summary>
        private const int PortalOfflineStrikes = 2;

        private int _portalFailStreak = 0;
        private bool _portalOnline = false;

        /// <summary>
        /// 网页认证模式的主循环。
        ///
        /// 为什么不能用拨号模式那套（2026-09-30 海辰报的问题）：
        ///   拨号模式靠 DialEngine.IsDialConnected() 判断 —— 看系统里有没有已连接的 PPP 接口。
        ///   但网页认证**根本不建立拨号会话**，认证成功后系统里还是只有那张物理网卡，
        ///   于是 _state 永远停在 Idle，主页显示「尚未连接」、在线时长不启动、
        ///   质量卡片不采样、参数卡片也不刷新 —— 用户的感受就是"主页没有任何网络信息"。
        ///
        ///   真相是：网页认证模式下"上没上网"只有一个答案 —— **发个请求看能不能通**。
        /// </summary>
        private void TickPortal(bool quiet)
        {
            // 免打扰时段不动探测（夜里没人看界面，省点电也省得刷日志）
            if (quiet)
            {
                if (_state != ConnState.Connected && _state != ConnState.Quiet) EnterQuiet();
                return;
            }
            if (_state == ConnState.Quiet)
            {
                // 免打扰结束，回到正常评估
                ApplyState(ConnState.Idle, "尚未连接", "等待认证上网");
            }

            // ⚠️ 必须用 OnlineStale()，不能用 Online()。
            //    本方法由 DispatcherTimer 回调，跑在 **UI 线程**上；
            //    而 Online() 在缓存过期时会同步发一个最长 6 秒的请求 ——
            //    网页认证模式下一旦断网，就变成"每 5 秒（缓存过期）冻结界面 6 秒"的死循环，
            //    窗口拖不动、按钮点不了，而且会一直循环下去（2026-10-01 审计确认）。
            //    OnlineStale 只读缓存、过期就让后台去探，代价是状态文字晚几秒，但界面不卡。
            bool online = NetProbe.OnlineStale();

            if (online)
            {
                _portalFailStreak = 0;

                if (!_portalOnline)
                {
                    // 刚检测到能上网 —— 记一笔在线起点。
                    //   · 用户刚点了认证并成功 → 这里有值
                    //   · 程序启动时其实早就认证过了 → 也走这里，起点=现在（时长从此刻算）
                    _portalOnline = true;
                    if (_connectedAt == DateTime.MinValue) _connectedAt = DateTime.Now;
                    Log.Info("网页认证模式：检测到可以正常上网");
                    AddHistory("认证有效 · 可以上网");
                }

                if (_state != ConnState.Connected)
                {
                    ApplyState(ConnState.Connected, "已认证上网",
                        "网页认证已生效，可以正常上网");
                }

                // 拨号那套后台重连在认证模式下毫无意义，别让它空转。
                // （认证模式的重连不走它，见 StartPortalAutoReauth 的说明）
                _autoReconnectArmed = false;

                // 网络恢复了 → 自动重连的计数清零，下次掉线重新开始
                if (_portalReauthAttempts > 0)
                {
                    Log.Info("网页认证：网络已恢复，自动重连计数归零（此前试了 "
                        + _portalReauthAttempts + " 次）");
                    _portalReauthAttempts = 0;
                    _autoReconnectAttempts = 0;
                    _portalExhaustedReported = false;   // 连上了，闸门复位，下次掉线重新能报
                }
                _pendingPortalReauth = false;
            }
            else
            {
                // 探测抖动保护：连续失败才认，避免网络一卡就弹"掉线"
                _portalFailStreak++;
                if (_portalFailStreak < PortalOfflineStrikes) return;

                if (_portalOnline)
                {
                    _portalOnline = false;
                    _connectedAt = DateTime.MinValue;
                    if (lblOnlineTime != null) lblOnlineTime.Text = "";
                    Log.Warn("网页认证模式：连续 " + _portalFailStreak + " 次探测不通，判定已掉线");
                    AddHistory("认证可能已失效 · 需要重新认证");

                    // ⭐ 判定掉线的那一刻，把自动重连武装起来。
                    //    拨号模式下 _autoReconnectArmed 是由"用户点过立即连接"置位的；
                    //    认证模式没有那个动作，所以在这里置位 —— 语义相同：
                    //    "用户是想上网的，断了就帮他接回来"。
                    if (AutoReconnectEnabled() && !_quietActive)
                    {
                        _autoReconnectArmed = true;
                        _lastAutoTry = DateTime.MinValue;   // 不等退避，立刻试第一次
                        Log.Info("网页认证模式：已武装自动重连，准备自动重新认证");
                    }
                }

                if (_state != ConnState.Connected && _state != ConnState.Error
                    && _state != ConnState.Connecting)
                {
                    ApplyState(ConnState.Idle, "未联网",
                        "还没通过网页认证，点「打开认证页」登录一下");
                }

                // ⭐ 掉线且已武装 → 走认证模式自己的自动重连
                if (_autoReconnectArmed && AutoReconnectEnabled() && !_quietActive)
                {
                    TryAutoReconnect();
                }
            }

            RefreshParamCards();
        }

        /// <summary>
        /// 认证窗口确认"网络已连通"时回调（由 WebAuthWindow 调用）。
        /// 立刻把主页切到已认证状态，不等下一轮轮询。
        /// </summary>
        internal void OnWebAuthOnline()
        {
            if (!IsPortalMode()) return;

            try
            {
                _portalFailStreak = 0;
                if (!_portalOnline)
                {
                    _portalOnline = true;
                    if (_connectedAt == DateTime.MinValue) _connectedAt = DateTime.Now;
                    AddHistory("认证有效 · 可以上网");
                }
                ApplyState(ConnState.Connected, "已认证上网",
                    "网页认证已生效，可以正常上网");
                RefreshParamCards();
            }
            catch { }
        }

        /// <summary>
        /// 网页认证刚提交完（或用户点「立即连接」）时调一次：
        /// 丢弃探测缓存并同步确认一次，让主页立刻反映"到底通没通"，
        /// 不用干等下一轮轮询。
        /// </summary>
        internal void RefreshPortalOnlineNow()
        {
            if (!IsPortalMode()) return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = NetProbe.Online(true);
                try
                {
                    Dispatcher.Invoke(delegate()
                    {
                        if (ok)
                        {
                            _portalFailStreak = 0;
                            if (!_portalOnline)
                            {
                                _portalOnline = true;
                                _connectedAt = DateTime.Now;
                                AddHistory("认证有效 · 可以上网");
                            }
                            ApplyState(ConnState.Connected, "已认证上网",
                                "网页认证已生效，可以正常上网");
                        }
                        RefreshParamCards();
                    });
                }
                catch { }
            });
        }

        private void TryAutoReconnect()
        {
            if ((DateTime.Now - _lastAutoTry).TotalSeconds < NextRetryDelay()) return;
            _lastAutoTry = DateTime.Now;
            _autoReconnectAttempts++;

            // ⚠️ 两种模式的"恢复连接"是完全不同的动作，必须分流。
            //
            //   拨号模式   → 重新拨号（StartDial）
            //   网页认证模式 → 根本不存在拨号会话，重拨必然得到 628。
            //                  正确动作是**把认证页重新打开并自动提交**。
            //
            // 2026-09-30 的 bug 实证：海辰 17:15 把认证方式切成 portal 之后，
            // 18:03 断网时程序仍然去调了 StartDial()，日志留下
            // `拨号结果: exit=628 success=False` —— 白试一次，还平白给学校
            // 攒了一次失败记录（学校对频繁拨号会限速）。根因就是这里没分流。
            if (IsPortalMode())
            {
                Log.Info("自动重连（网页认证）：第 " + _autoReconnectAttempts + " 次尝试重开认证页");
                StartPortalAutoReauth();
                return;
            }

            Log.Info("自动重连：第 " + _autoReconnectAttempts + " 次尝试恢复连接 "
                + (_current != null ? _current.Name : "(无)"));
            // 第二参数 true = 这次是自动重连发起的：失败时不弹窗，只发一次气泡
            StartDial(false, true);
        }

        // ==================================================================
        // 网页认证模式的自动重连（2026-09-30 第十一批补）
        //
        // 背景 / 为什么需要单独一套：
        //   认证模式下断了网，程序此前**什么都不做** —— 不重开认证页、不重拨，
        //   只能用户手动点「打开认证页」重新登录。这不是优化，是功能缺失。
        //
        // 认证模式下"恢复连接"的正确动作是什么：
        //   把认证页**重新打开并自动提交**（账号密码程序有，验证码要用户填）。
        //   它和拨号是两回事，不能复用 StartDial。
        //
        // ⚠️ 三条安全边界（想清楚了才敢自动做）：
        //
        //   ① 绝不绕过验证码。程序只做"填账号密码 + 点登录"，
        //      验证码永远等用户亲眼看着敲。这是硬底线，没有例外。
        //
        //   ② 不能偷偷摸摸弹窗。用户在打游戏、在看视频，桌面突然蹦出一个窗口
        //      是最讨厌的行为。所以自动重开时：
        //        · 窗口不抢焦点（ShowActivated = false）
        //        · 静默模式（开机自启 / 后台待着）下**只发托盘气泡**，
        //          让用户自己点气泡里的动作来开；绝不主动弹到前台。
        //      ——海辰的明确要求："尤其游戏场景，反对任何弹窗与侵入性闪烁"。
        //
        //   ③ 要能停下来。连续失败到上限就放弃并把状态标成 Error，
        //      由用户介入。认证连续失败通常意味着密码改了 / 学号变了，
        //      再重试也没用，反而可能触发学校的风控。
        //
        // 状态标记 _portalReauthRunning 是防重入的：认证页本身有 5 分钟
        // 自动重试窗口，这段时间内不该再叠一个。
        // ==================================================================

        /// <summary>认证页自动重开的进行中标记（防重入）。</summary>
        private bool _portalReauthRunning = false;

        /// <summary>本次掉线已经自动重开过几次认证页。连上或用户手动操作后归零。</summary>
        private int _portalReauthAttempts = 0;

        /// <summary>
        /// "自动重连次数已用尽"这件事，是否已经如实报告过一次了。
        ///
        /// ⚠️ 为什么需要这个闸门（2026-10-01 审计确认）：
        ///   达到上限之后，每 30 秒退避到期都会重新进 StartPortalAutoReauth，
        ///   那段"报告失败"的代码就会再跑一遍 —— 往历史面板塞重复记录、重写 settings.txt，
        ///   一直刷下去。状态条上的"需要重新认证"本来就不会消失，喊一次就够了。
        ///   网络恢复或计数归零时复位。
        /// </summary>
        private bool _portalExhaustedReported = false;

        /// <summary>
        /// 拨号模式的"自动重连"连续失败了几次。
        ///
        /// ⚠️ 只用来决定**要不要发提示气泡**：只有本次连败的第一次发，之后静默。
        ///   自动重连一旦失败往往是"密码失效 / 欠费"这类不会自愈的原因，
        ///   每 30 秒吵一次毫无意义，只会让人把程序静音。拨号成功时归零。
        /// </summary>
        private int _autoDialFailStreak = 0;

        /// <summary>连续重试到这里就不试了，交给用户。3 次足够覆盖瞬时抖动。</summary>
        private const int PortalReauthMaxAttempts = 3;

        private void StartPortalAutoReauth()
        {
            // 免打扰时段不动手（和拨号模式一致：夜里不折腾）
            if (_quietActive) return;

            if (!AutoReconnectEnabled()) return;

            // 没有账号就没法自动填表 —— 直接说清楚，别假装在努力
            if (_accounts.Count == 0)
            {
                Log.Warn("网页认证自动重连：一个账号都没有，无法自动认证");
                ApplyState(ConnState.Error, "需要重新认证",
                    "认证已失效，但程序里没有任何已保存的账号。点「管理账号」加一个。");
                return;
            }

            // 认证窗口已经开着（用户自己开的，或上一轮还没结束）→ 不重复开
            if (_webAuthWindow != null && _webAuthWindow.IsLoaded)
            {
                Log.Info("网页认证自动重连：认证页已经开着，交给它自己处理");
                return;
            }

            if (_portalReauthRunning) return;

            if (_portalReauthAttempts >= PortalReauthMaxAttempts)
            {
                // ⚠️ 闸门：这一段以前每 30 秒（退避到期重进本方法）就重跑一次 ——
                //    往历史面板塞一条一模一样的记录、重写一次 settings.txt，直到天荒地老
                //    （2026-10-01 审计确认，当时日志里能看到它被反复触发）。
                //    报告过一次就停手：状态条上那句"需要重新认证"不会自己消失，
                //    用户随时看得到，没必要每 30 秒再喊一遍。
                if (_portalExhaustedReported) return;
                _portalExhaustedReported = true;

                Log.Warn("网页认证自动重连：已连续失败 " + _portalReauthAttempts + " 次，停止自动尝试");
                ApplyState(ConnState.Error, "需要重新认证",
                    "自动重新认证试了 " + _portalReauthAttempts + " 次都没成功。"
                    + "可能是密码改了或账号变了，点「打开认证页」手动登一次。");
                AddHistory("自动重新认证失败 · 请手动登录");
                return;
            }

            _portalReauthRunning = true;

            // ⚠️ 计数**不能**在这里加（2026-10-01 审计确认的老问题）。
            //    此处还没确定要走哪条路：是"真开认证页"，还是"只发个托盘气泡"。
            //    静默场景下每轮退避都会走到这里、白白扣掉一次配额，
            //    而真正的动作是挂在"用户点气泡"上的 —— 用户没注意到气泡，
            //    3 次配额就被空耗光了，之后即使点气泡也只剩手动登录这一条路。
            //    所以计数后移到真正要开认证页的两处：前台分支 + 气泡点击回调。

            ApplyState(ConnState.Connecting, "正在重新认证…",
                "认证已失效，正在自动打开认证页（第 " + (_portalReauthAttempts + 1) + " 次）");

            bool silent = _silentStart || !IsVisible;

            if (silent)
            {
                // 静默场景（开机自启 / 缩在托盘里）：**不发窗口、不抢焦点**，
                // 只发一个托盘气泡。用户点了才开认证页。
                // 这样绝不会在他打游戏时蹦出一个窗口来。
                Log.Info("网页认证自动重连：当前是后台/静默状态，改为发托盘提示而不弹窗");
                ShowBalloon("认证已失效，需要重新登录",
                    "网络断了。点这里打开认证页登录（账号密码已帮你填好，只需填验证码）。");

                // 把"重开认证页"挂到气泡点击上 —— 见 ShowBalloon 的 clickAction 重载
                _pendingPortalReauth = true;
                _portalReauthRunning = false;
                AddHistory("认证失效 · 已提示（未弹窗，避免打扰）");
                return;
            }

            // 前台场景：用户正开着主界面，直接把认证页拉起来。
            // ⚠️ 不抢焦点（ShowActivated=false）：用户可能正在别的地方打字，
            //    弹出窗口抢走焦点会让半截输入丢失。
            _portalReauthAttempts++;   // 真开了才计数（理由见上面"计数后移"那段）
            OpenWebAuthWindowForReauth();
        }

        /// <summary>气泡被点过之后，真正把认证页打开（由 ShowBalloon 的回调触发）。</summary>
        private bool _pendingPortalReauth = false;

        internal void OpenPendingPortalReauthIfAny()
        {
            if (!_pendingPortalReauth) return;
            _pendingPortalReauth = false;

            // ⚠️ 到这一刻才是"真的要开认证页"—— 计数也该在这一刻才算数。
            //    配额是给"开认证页"用的，不是给"发气泡"用的（见 StartPortalAutoReauth 的说明）。
            if (_portalReauthAttempts < PortalReauthMaxAttempts) _portalReauthAttempts++;

            OpenWebAuthWindowForReauth();
        }

        /// <summary>
        /// 自动重连场景下打开认证页：自动填表 + 提交，但不抢焦点。
        /// 与 OpenWebAuthWindowOnStartup 的区别是这里带"自动认证"语义。
        /// </summary>
        private void OpenWebAuthWindowForReauth()
        {
            try
            {
                if (_webAuthWindow != null && _webAuthWindow.IsLoaded)
                {
                    _portalReauthRunning = false;
                    return;
                }

                _webAuthWindow = new WebAuthWindow(this);
                _webAuthWindow.SetReauthMode(true);   // 通知它：这是自动重连，走自动填表
                _webAuthWindow.Owner = this;
                _webAuthWindow.ShowActivated = false;   // ⚠️ 不抢焦点

                _webAuthWindow.Closed += delegate(object s, EventArgs a)
                {
                    _webAuthWindow = null;
                    _portalReauthRunning = false;

                    // 窗口关了 → 看看到底连上没有，决定要不要继续重试
                    RefreshPortalOnlineNow();
                    Dispatcher.BeginInvoke(new Action(delegate()
                    {
                        CheckPortalReauthOutcome();
                    }), DispatcherPriority.Background);
                };

                _webAuthWindow.Show();
                AddHistory("已自动打开认证页 · 请填验证码并登录");
            }
            catch (Exception ex)
            {
                _portalReauthRunning = false;
                Log.Error("网页认证自动重连：打开认证页失败", ex);
            }
        }

        /// <summary>
        /// 认证页关闭后核对结果：通了就归零计数；没通就留着计数，
        /// 下一轮 TickPortal 判定仍不在线时会再触发一次（受退避和上限约束）。
        /// </summary>
        private void CheckPortalReauthOutcome()
        {
            try
            {
                // ⚠️ 同上：本方法是被 Dispatcher.BeginInvoke 投递进来的，同样在 UI 线程上，
                //    所以也得用不阻塞的 OnlineStale()（2026-10-01 审计确认）。
                if (NetProbe.OnlineStale())
                {
                    Log.Info("网页认证自动重连：已恢复上网");
                    _portalReauthAttempts = 0;
                    _autoReconnectAttempts = 0;
                    _portalExhaustedReported = false;   // 恢复上网，闸门也复位
                    AddHistory("自动重新认证成功 · 已恢复上网");
                }
                else
                {
                    Log.Info("网页认证自动重连：认证页关掉了但仍未上网"
                        + "（已试 " + _portalReauthAttempts + "/" + PortalReauthMaxAttempts + "）");
                }
            }
            catch { }
        }

        /// <summary>自测出口：当前自动重开认证页到第几次了。</summary>
        internal int DebugPortalReauthAttempts() { return _portalReauthAttempts; }

        /// <summary>
        /// 自测出口：走一遍"该用哪个重连分支"的判断，返回分支名，**不真的执行**。
        ///
        /// 为什么需要它：本次修的 bug 正是"分支走错了"——认证模式下跑去拨号。
        /// 所以用例必须能直接问"现在这个设置下，重连会走哪条路"，
        /// 而不是去看日志里有没有"拨号结果: exit=628"（那要真跑拨号，代价太大）。
        /// </summary>
        internal string DebugReconnectBranch()
        {
            return IsPortalMode() ? "portal" : "dial";
        }

        /// <summary>
        /// 自测出口：把一个假的"掉线"喂给判定逻辑，看会不会武装自动重连。
        /// 返回 (是否武装, 是否走认证分支)。
        /// </summary>
        internal bool DebugArmAutoReconnect()
        {
            if (!AutoReconnectEnabled()) return false;
            _autoReconnectArmed = true;
            _lastAutoTry = DateTime.MinValue;
            return _autoReconnectArmed;
        }

        /// <summary>
        /// 自测出口：把 _portalOnline 设成"曾经在线过"，用于模拟"在线中断线"。
        /// </summary>
        internal void DebugSetPortalWasOnline()
        {
            _portalOnline = true;
            _portalFailStreak = 0;
        }

        /// <summary>自测出口：读当前是否已武装（不改状态）。</summary>
        internal bool DebugIsArmed() { return _autoReconnectArmed; }

        /// <summary>自测出口：模拟一次"判定掉线"的收尾动作（不碰真实网络）。</summary>
        internal void DebugSimulatePortalDrop()
        {
            _portalFailStreak = 0;
            if (_portalOnline)
            {
                _portalOnline = false;
                _connectedAt = DateTime.MinValue;
                if (AutoReconnectEnabled() && !_quietActive)
                {
                    _autoReconnectArmed = true;
                    _lastAutoTry = DateTime.MinValue;
                }
            }
        }

        /// <summary>自测出口：重置自动重连计数，让用例可重复跑。</summary>
        internal void DebugResetPortalReauth()
        {
            _portalReauthAttempts = 0;
            _autoReconnectAttempts = 0;
            _portalReauthRunning = false;
            _portalExhaustedReported = false;
            _lastAutoTry = DateTime.MinValue;
            _pendingPortalReauth = false;
        }

        /// <summary>
        /// 自测出口：只做"计数 + 上限判断"这部分，不打开任何窗口。
        /// 用来验证重试到底会不会在 3 次之后停下。
        /// 返回 true = 还允许继续尝试。
        /// </summary>
        internal bool DebugBumpPortalReauthAttempt()
        {
            if (_portalReauthAttempts >= PortalReauthMaxAttempts) return false;
            _portalReauthAttempts++;
            return true;
        }

        /// <summary>自测出口：上限值是多少。</summary>
        internal int DebugPortalReauthMax() { return PortalReauthMaxAttempts; }

        /// <summary>自测出口：当前认证方式（原始字符串，用于打印）。</summary>
        internal string DebugAuthModeText() { return AuthMode(); }

        /// <summary>自测出口：当前是不是"等着用户点气泡"的状态。</summary>
        internal bool DebugPendingPortalReauth() { return _pendingPortalReauth; }

        // ==================================================================
        // 夜间免打扰
        //
        // 海辰的需求（从 AutoDial-GUIT 搬过来的能力）：
        //   宿舍夜里断电/断网时程序别一直干活、别弹提示 —— 到了设定时段完全静默，
        //   时段结束后立刻恢复检测，通网瞬间自动连上。
        //
        // 时段内做什么 / 不做什么：
        //   · 不重试拨号        —— 这是主要目的（省得白试、白给学校攒失败次数）
        //   · 暂停连接质量检测  —— 不再 ping，真的零打扰
        //   · 不弹任何气泡提示
        //   · 【但不断开已连着的网络】也不停心跳保活 —— 免打扰不该把人搞掉线
        // ==================================================================

        private bool _quietActive = false;

        internal bool NightQuietEnabled()
        {
            return ConfigStore.GetBool(_settings, "NightQuiet", false);
        }

        internal string NightQuietStart()
        {
            return ConfigStore.GetString(_settings, "NightQuietStart", "23:30");
        }

        internal string NightQuietEnd()
        {
            return ConfigStore.GetString(_settings, "NightQuietEnd", "07:00");
        }

        /// <summary>"HH:mm" → 当天的分钟数；格式不对返回 -1。</summary>
        private static int ParseHm(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            string[] parts = text.Trim().Split(':');
            if (parts.Length != 2) return -1;
            int h, m;
            if (!int.TryParse(parts[0].Trim(), out h)) return -1;
            if (!int.TryParse(parts[1].Trim(), out m)) return -1;
            if (h < 0 || h > 23 || m < 0 || m > 59) return -1;
            return h * 60 + m;
        }

        /// <summary>给设置界面用的格式校验（HH:mm，24 小时制）。</summary>
        internal static bool IsValidHm(string text)
        {
            return ParseHm(text) >= 0;
        }

        /// <summary>现在是否处于免打扰时段。支持跨零点（比如 23:30 → 07:00）。</summary>
        internal bool InNightQuiet()
        {
            if (!NightQuietEnabled()) return false;

            int s = ParseHm(NightQuietStart());
            int e = ParseHm(NightQuietEnd());
            if (s < 0 || e < 0 || s == e) return false;   // 配置不合法就当没开

            DateTime now = DateTime.Now;
            int cur = now.Hour * 60 + now.Minute;

            if (s < e) return cur >= s && cur < e;        // 同一天内
            return cur >= s || cur < e;                   // 跨零点
        }

        /// <summary>切到"夜间免打扰"。</summary>
        private void EnterQuiet()
        {
            if (_state == ConnState.Quiet) return;

            Log.Info("进入夜间免打扰时段（" + NightQuietStart() + " - " + NightQuietEnd()
                + "）：暂停检测与自动连接");
            ApplyState(ConnState.Quiet, "夜间免打扰中",
                "此时段不检测、不重连；到 " + NightQuietEnd() + " 自动恢复。");
        }

        // ==================================================================
        // 网线链路状态：拔掉 → 待机；插回 → 立刻连
        //
        // 为什么要单独看"网线"而不是只看拨号结果：
        //   网线拔了之后再怎么重试都必然失败，白白给学校攒失败次数
        //   （学校对频繁拨号是会限速的），日志也刷得没法看。
        //   判定用 HealthReport.IsWiredMediaConnected()：它用 WMI 只看
        //   **物理**有线网卡（PCI\/USB\ 开头，排除虚拟网卡、Wi-Fi），
        //   载波在不在即"网线插好没有"；探测失败时按"插好"处理，绝不误拦。
        // ==================================================================

        /// <summary>有线网卡是否接着（有载波）。默认 true —— 宁可多试一次，不可误拦。</summary>
        private volatile bool _linkOk = true;
        private string _linkDetail = "";
        private DateTime _linkCheckedAt = DateTime.MinValue;
        private int _linkChecking = 0;

        /// <summary>进入"等待网线"的时刻（只用来判断要不要把探测降频）。</summary>
        private DateTime _waitingSince = DateTime.MinValue;

        /// <summary>链路状态缓存有效期。WMI 查询不便宜，别每秒都查。</summary>
        private const int LinkCacheSeconds = 3;

        /// <summary>同步查一次（只在"刚断开""开机""用户点连接"这类一次性场景用，几十毫秒）。</summary>
        private bool CheckLinkNow()
        {
            try
            {
                string detail;
                bool ok = HealthReport.IsWiredMediaConnected(out detail);
                _linkOk = ok;
                _linkDetail = detail ?? "";
                _linkCheckedAt = DateTime.Now;
                return ok;
            }
            catch
            {
                _linkOk = true;
                _linkCheckedAt = DateTime.Now;
                return true;
            }
        }

        /// <summary>后台刷新链路状态，不阻塞界面；缓存没过期就跳过。</summary>
        private void RefreshLinkState()
        {
            // 采样间隔：平时 3 秒（插网线后反应快）；
            // 但如果"待机等网线"已经持续好几分钟（比如放假把网线收了），
            // 就放宽到 10 秒 —— 没必要让 WMI 一直空转。
            // （这个自适应是看了 AutoDial-GUIT 的做法后加的，它用固定 10 秒。）
            double ttl = LinkCacheSeconds;
            if (_state == ConnState.WaitingLink && _waitingSince != DateTime.MinValue
                && (DateTime.Now - _waitingSince).TotalMinutes >= 3)
            {
                ttl = 10;
            }
            if ((DateTime.Now - _linkCheckedAt).TotalSeconds < ttl) return;
            if (System.Threading.Interlocked.CompareExchange(ref _linkChecking, 1, 0) != 0) return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    string detail;
                    bool ok = HealthReport.IsWiredMediaConnected(out detail);
                    _linkOk = ok;
                    _linkDetail = detail ?? "";
                    _linkCheckedAt = DateTime.Now;
                }
                catch
                {
                    _linkOk = true;
                    _linkCheckedAt = DateTime.Now;
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _linkChecking, 0);
                }
            });
        }

        /// <summary>切到"待机 · 等待网线"。已经在待机就不重复刷界面、不重复写日志。</summary>
        private void EnterWaitingLink()
        {
            if (_state == ConnState.WaitingLink) return;

            _waitingSince = DateTime.Now;
            Log.Info("网线没接好（" + (_linkDetail ?? "") + "），进入待机等网线；插好后会自动连接");
            // 文案要短：状态条那行是单行省略号截断的，写长了关键承诺会被吃掉一半
            ApplyState(ConnState.WaitingLink, "待机中 · 等待网线",
                "网线没插好（或另一头没通电）。插好后会自动连接。");
        }

        // ==================================================================
        // 网速
        // ==================================================================

        /// <summary>
        /// 上一轮采样时统计的是哪块网卡（按 Id 记，Id 是稳定的）。
        ///
        /// 为什么要记：网卡一换（拨号建立、认证前后切到物理网卡），
        /// 新网卡的累计字节数跟老网卡没有可比性 —— 直接相减会算出一个
        /// 巨大或负数的速率，曲线上凭空出现一根尖刺或者一个掉到 0 的坑。
        /// 换了网卡就当"重新开始采样"，跳过这一次的差值计算。
        /// </summary>
        private string _speedAdapterId = "";

        private void SampleSpeed()
        {
            try
            {
                NetworkInterface ni = NetProbe.PickTrafficAdapter();

                long rx, tx;
                if (!NetProbe.TrafficOf(ni, out rx, out tx))
                {
                    // 这一轮没采到（网卡消失 / 权限问题）——
                    // 不要把它当成"零流量"，保持上一轮的显示，下一轮再试。
                    return;
                }

                string id = ni.Id ?? "";
                bool changed = (id != _speedAdapterId);

                DateTime now = DateTime.Now;
                if (_lastSampleAt != DateTime.MinValue && !changed)
                {
                    double secs = (now - _lastSampleAt).TotalSeconds;
                    if (secs > 0.2)
                    {
                        double down = Math.Max(0, (rx - _lastRx) / secs);
                        double up = Math.Max(0, (tx - _lastTx) / secs);

                        if (lblSpeedDown != null) lblSpeedDown.Text = "下行 " + FormatBytes((long)down) + "/s";
                        if (lblSpeedUp != null) lblSpeedUp.Text = "上行 " + FormatBytes((long)up) + "/s";
                        if (valSpeed != null) valSpeed.Text = FormatBytes((long)(down + up)) + "/s";

                        _speedHistory.Add(down);
                        while (_speedHistory.Count > SpeedHistoryMax) _speedHistory.RemoveAt(0);
                        RedrawSpeedGraph();
                    }
                }

                if (changed)
                {
                    // 换了网卡 —— 上一块的累计值不能用来算这块的差值。
                    // 记一笔，从这块网卡开始重新起算。
                    //
                    // ⚠️ 只有**运行中途换网卡**才写日志。
                    //    程序刚起来时 _speedAdapterId 还是空串，第一次采样必然
                    //    "changed == true" —— 那不是切换，是初始化。原先这里无条件写，
                    //    结果每次启动都留下一条"网速统计切换到网卡"，纯噪音
                    //    （2026-09-30 冒烟测试发现：13 条里有 11 条是这么来的）。
                    bool firstEver = (_speedAdapterId.Length == 0);
                    _speedAdapterId = id;
                    if (!firstEver)
                    {
                        Log.Info("网速统计切换到网卡: " + (ni.Name ?? "") + "（" + NetProbe.KindOf(ni) + "）");
                    }
                }

                _lastRx = rx;
                _lastTx = tx;
                _lastSampleAt = now;
            }
            catch { }
        }

        internal void RedrawSpeedGraph()
        {
            if (speedLine == null || speedFill == null || speedCanvas == null) return;
            double w = speedCanvas.ActualWidth;
            double h = speedCanvas.ActualHeight;
            if (w <= 1 || h <= 1) return;

            speedLine.Points.Clear();
            speedFill.Points.Clear();

            if (_speedHistory.Count < 2)
            {
                speedLine.Points.Add(new Point(0, h - 4));
                speedLine.Points.Add(new Point(w, h - 4));
                return;
            }

            double max = 0;
            foreach (double v in _speedHistory) if (v > max) max = v;
            if (max < 1024) max = 1024;

            // 按实际数据点数平铺整个宽度。
            // 曾经用固定步长 (w / 89)，程序刚跑起来时曲线会挤在最左边一小条。
            int n = _speedHistory.Count;
            double stepX = (n > 1) ? (w / (n - 1)) : w;

            for (int i = 0; i < n; i++)
            {
                double v = _speedHistory[i];
                double x = i * stepX;
                double y = h - (v / max) * (h - 8) - 4;
                if (y < 0) y = 0;
                if (y > h) y = h;
                speedLine.Points.Add(new Point(x, y));
                speedFill.Points.Add(new Point(x, y));
            }
            speedFill.Points.Add(new Point((n - 1) * stepX, h));
            speedFill.Points.Add(new Point(0, h));
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        // ==================================================================
        // 网络质量
        // ==================================================================

        private void OnQualityUpdated()
        {
            // 采样跑在后台线程，这里必须切回 UI 线程才能动控件
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate() { RefreshQualityUi(); }));
            }
            catch { }
        }

        /// <summary>按最新快照刷新质量卡片（徽章 / 指标 / 曲线）。</summary>
        internal void RefreshQualityUi()
        {
            if (_closing) return;
            if (badgeQuality == null && qualityCanvas == null) return;

            ApplyQualityVisual(_quality.GetSample());
        }

        private void ApplyQualityVisual(QualitySample s)
        {
            bool active = _quality.IsActive;

            Color c;
            if (!active) c = Theme.Idle;
            else if (s.Level == 1) c = Theme.Ok;
            else if (s.Level == 2) c = Theme.Warn;
            else if (s.Level == 3) c = Theme.Err;
            else c = Theme.Accent;

            SetBadge(badgeQuality, lblQualityVerdict, active ? s.Verdict : "未连接", c);

            Color lossColor = Theme.TextPrimary;
            if (!active) lossColor = Theme.TextMuted;
            else if (s.LossPct >= 10) lossColor = Theme.Err;
            else if (s.LossPct > 0) lossColor = Theme.Warn;

            Color rttColor = Theme.TextPrimary;
            if (!active) rttColor = Theme.TextMuted;
            else if (s.AvgRtt >= 150) rttColor = Theme.Warn;

            Color gwColor = Theme.TextPrimary;
            if (!active) gwColor = Theme.TextMuted;
            else if (s.GwLossPct >= 25) gwColor = Theme.Err;

            SetMetric(valLoss, active && s.HasData ? s.LossPct + " %" : "—", lossColor);
            SetMetric(valRtt, active && s.AvgRtt >= 0 ? s.AvgRtt + " ms" : "—", rttColor);
            SetMetric(valGwRtt, active && s.GwAvgRtt >= 0 ? s.GwAvgRtt + " ms" : "—", gwColor);

            // 链路速率跟有没有拨号无关 —— 网线插着、网卡协商出速率就该显示，
            // 这样用户能一眼看出是百兆口还是千兆口（限速常卡在这里）。
            string linkKind;
            long linkMbps = QualityMonitor.GetLinkSpeedMbps(out linkKind);
            SetMetric(valLink,
                linkMbps > 0 ? linkMbps + " Mbps" : "—",
                linkMbps > 0 ? Theme.TextPrimary : Theme.TextMuted);

            if (lblQualityDetail != null)
            {
                lblQualityDetail.Text = active ? s.Detail : "连接后自动开始监测丢包与延迟。";
            }

            // 适配器名（"程序看的是哪条线"）—— 与参数卡片同源，一起刷新不会打架
            if (lblAdapter != null)
            {
                lblAdapter.Text = _primaryAdapterLabel ?? "";
            }

            // 曲线跟着等级换色
            if (qualityLine != null) qualityLine.Stroke = new SolidColorBrush(c);
            if (qualityFill != null)
            {
                qualityFill.Stroke = new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B));
                qualityFill.Fill = new SolidColorBrush(Color.FromArgb(26, c.R, c.G, c.B));
            }

            RedrawQualityGraph(s);
        }

        private static void SetBadge(Border badge, TextBlock text, string value, Color c)
        {
            if (text != null)
            {
                text.Text = value == null ? "" : value;
                text.Foreground = new SolidColorBrush(c);
            }
            if (badge != null)
            {
                badge.Background = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
                badge.BorderBrush = new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B));
            }
        }

        private static void SetMetric(TextBlock t, string value, Color c)
        {
            if (t == null) return;
            t.Text = value;
            t.Foreground = new SolidColorBrush(c);
        }

        internal void RedrawQualityGraph()
        {
            RedrawQualityGraph(_quality.GetSample());
        }

        internal void RedrawQualityGraph(QualitySample s)
        {
            if (qualityCanvas == null || qualityLine == null || qualityFill == null) return;

            double w = qualityCanvas.ActualWidth;
            double h = qualityCanvas.ActualHeight;
            if (w <= 1 || h <= 1) return;

            qualityLine.Points.Clear();
            qualityFill.Points.Clear();

            List<int> hist = s.History;
            if (hist == null || hist.Count < 2)
            {
                qualityLine.Points.Add(new Point(0, h - 4));
                qualityLine.Points.Add(new Point(w, h - 4));
                return;
            }

            // 纵轴上限取有效样本的 P95，再留 15% 余量。
            // 直接用最大值的话，一个 800ms 的尖峰会把平时 20ms 的曲线压成一条直线。
            long max = 0;
            var ok = new List<int>();
            foreach (int v in hist)
            {
                if (v >= 0) ok.Add(v);
            }
            if (ok.Count > 0)
            {
                ok.Sort();
                int idx = (int)Math.Round(ok.Count * 0.95);
                if (idx >= ok.Count) idx = ok.Count - 1;
                max = (long)(ok[idx] * 1.15);
            }
            if (max < 50) max = 50;

            int n = hist.Count;
            double stepX = (n > 1) ? (w / (n - 1)) : w;

            for (int i = 0; i < n; i++)
            {
                int v = hist[i];
                double x = i * stepX;
                // 超时的点贴到底部，曲线上就会出现明显的"掉坑"
                double y = v < 0 ? h - 3 : h - ((double)v / max) * (h - 8) - 4;
                if (y < 0) y = 0;
                if (y > h) y = h;

                qualityLine.Points.Add(new Point(x, y));
                qualityFill.Points.Add(new Point(x, y));
            }

            qualityFill.Points.Add(new Point((n - 1) * stepX, h));
            qualityFill.Points.Add(new Point(0, h));
        }

        // ==================================================================
        // 参数卡片
        // ==================================================================

        private void RefreshParamCards()
        {
            try
            {
                // 参数卡片只关心"当前是哪条线在上网"。
                // 选卡逻辑统一收在 NetProbe 里 —— 原先这里自己写了一遍枚举，
                // 既没排虚拟网卡，也要跟质量模块的逻辑对不上（两处会得出不同答案）。
                NetworkInterface ni = NetProbe.PickPrimaryAdapter();

                string ip = NetProbe.Ipv4Of(ni);
                string gw = NetProbe.GatewayOf(ni);
                string dns = NetProbe.DnsOf(ni);

                if (valIp != null) valIp.Text = ip.Length > 0 ? ip : "—";
                if (valGateway != null) valGateway.Text = gw.Length > 0 ? gw : "—";
                if (valDns != null) valDns.Text = dns.Length > 0 ? dns : "—";

                // 顺手把"程序看的是哪块网卡"记下来给质量卡片用。
                // 本机同时挂着向日葵 / UU远程 / FlClash TUN / Watt Toolkit 一堆虚拟网卡，
                // 用户只看数字的话没法确认程序有没有选错线，得把名字摆出来。
                _primaryAdapterLabel = BuildAdapterLabel(ni);
            }
            catch { }
        }

        /// <summary>当前用于展示的适配器描述，形如「以太网 · 有线 1000 Mbps」。</summary>
        private string _primaryAdapterLabel = "";

        /// <summary>
        /// 给适配器拼一句人话，用于界面自证"看的是哪条线"。
        /// 取不到任何信息时返回空串（界面上就不显示，不占地方）。
        /// </summary>
        private static string BuildAdapterLabel(NetworkInterface ni)
        {
            if (ni == null) return "";

            string kind = NetProbe.KindOf(ni);
            string name = ni.Name ?? "";
            if (name.Length == 0) name = ni.Description ?? "";
            if (name.Length == 0) return "";

            string head = name;
            if (kind.Length > 0) head = head + " · " + kind;

            // 拨号接口报的速率没有参考价值，干脆不显示
            if (kind != "拨号")
            {
                try
                {
                    long mbps = ni.Speed / 1000000L;
                    if (mbps > 0) head = head + " " + mbps + " Mbps";
                }
                catch { }
            }
            return head;
        }

        // ==================================================================
        // 状态
        // ==================================================================

        private void ApplyState(ConnState state, string title, string detail)
        {
            _state = state;

            bool busy = state == ConnState.Connecting || state == ConnState.Disconnecting;

            if (btnMainAction != null)
            {
                btnMainAction.IsEnabled = !busy;
                if (state == ConnState.Connected) btnMainAction.Content = "重新连接";
                else if (state == ConnState.Connecting) btnMainAction.Content = "连接中…";
                else btnMainAction.Content = "立即连接";
            }
            if (btnDisconnect != null)
            {
                btnDisconnect.IsEnabled = !busy && state == ConnState.Connected;
            }

            // 质量监测跟着连接状态走：连上就开始测，断开就停
            _quality.SetActive(state == ConnState.Connected);
            RefreshQualityUi();

            // 在线时长跟着状态一起刷 —— 否则刚连上那一秒里时长是空的
            RefreshOnlineTime();

            // 心跳保活同理：只有连着网才需要发心跳，断开就该停
            _keepAlive.SetActive(state == ConnState.Connected && KeepAliveEnabled(),
                KeepAliveIntervalMinutes());

            RefreshStatusVisualWith(title, detail);
            UpdateTrayState(title);
        }

        /// <summary>重新渲染状态条（主题变化时也会调用）。</summary>
        internal void RefreshStatusVisual()
        {
            RefreshStatusVisualWith(null, null);
        }

        private void RefreshStatusVisualWith(string title, string detail)
        {
            if (statusStrip == null) return;

            Color c;
            if (_state == ConnState.Connected) c = Theme.Ok;
            else if (_state == ConnState.Connecting || _state == ConnState.Disconnecting) c = Theme.Warn;
            else if (_state == ConnState.Error) c = Theme.Err;
            else c = Theme.Idle;

            statusStrip.Background = new SolidColorBrush(Color.FromArgb(32, c.R, c.G, c.B));
            statusStrip.BorderBrush = new SolidColorBrush(Color.FromArgb(82, c.R, c.G, c.B));

            if (statusDot != null) statusDot.Fill = new SolidColorBrush(c);

            if (title != null && lblStatusTitle != null)
            {
                lblStatusTitle.Text = title;
                lblStatusTitle.Foreground = new SolidColorBrush(StatusTextColor());
            }
            else if (lblStatusTitle != null)
            {
                lblStatusTitle.Foreground = new SolidColorBrush(StatusTextColor());
            }

            if (detail != null && lblStatusDetail != null)
            {
                lblStatusDetail.Text = detail;
            }
            if (lblStatusDetail != null)
            {
                lblStatusDetail.Foreground = new SolidColorBrush(c);
            }
        }

        /// <summary>状态标题文字色：浅色模式下用深色变体，深色模式下用亮色变体。</summary>
        private Color StatusTextColor()
        {
            bool dark = Theme.Current == ThemeMode.Dark;
            if (_state == ConnState.Connected) return dark ? Cc(0xA7, 0xF3, 0xD0) : Cc(0x06, 0x5F, 0x46);
            if (_state == ConnState.Connecting || _state == ConnState.Disconnecting)
                return dark ? Cc(0xFD, 0xE0, 0x8A) : Cc(0x92, 0x40, 0x0E);
            if (_state == ConnState.Error) return dark ? Cc(0xFE, 0xCA, 0xCA) : Cc(0x99, 0x1B, 0x1B);
            return Theme.TextPrimary;
        }

        /// <summary>
        /// 刷新在线时长那行字。
        ///
        /// ⚠️ 两条历史教训，都写在这儿免得再踩：
        ///
        /// ① 原先这段逻辑只写在 OnTick 里，于是状态刚变成"已连接"的那一秒内
        ///    时长显示是空的 —— 用户点完认证、窗口关掉回头一看，正好撞上这个空窗期。
        ///    抽出来给 ApplyState 也调一次，状态一变时长立刻就有值。
        ///
        /// ② 【2026-09-30 海辰报的第二个 bug】"呼出主页时在线时长不动，
        ///    放在后台时时间才同步"。
        ///    根因不在这个函数，而在"谁来调它"：拖动窗口 / 点击标题栏会让 Windows
        ///    进入模态消息循环，DispatcherTimer 被压住不发。
        ///    而这个函数的写法本身是**自愈**的 —— 它永远按"现在 - 起点"重算，
        ///    不做 `+= 1` 的累加。所以只要被调到一次，显示值立刻跳到正确位置，
        ///    漏掉多少 tick 都不会累积误差。**别把它改成累加式，那样一定会漂。**
        /// </summary>
        private void RefreshOnlineTime()
        {
            if (lblOnlineTime == null) return;

            if (_connectedAt == DateTime.MinValue)
            {
                lblOnlineTime.Text = "";
                return;
            }

            TimeSpan ts = DateTime.Now - _connectedAt;
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;   // 系统时间被往回调过也不显示负数

            string text = "在线 " + string.Format("{0:00}:{1:00}:{2:00}",
                (int)ts.TotalHours, ts.Minutes, ts.Seconds);

            // 值没变就不写 —— 每秒都赋同一个字符串会让 WPF 白白重排一次文本
            if (lblOnlineTime.Text != text) lblOnlineTime.Text = text;
        }

        private static Color Cc(byte r, byte g, byte b)
        {
            return Color.FromRgb(r, g, b);
        }

        // ==================================================================
        // 拨号
        // ==================================================================

        private void BtnMainAction_Click(object sender, RoutedEventArgs e)
        {
            if (_state == ConnState.Connecting || _state == ConnState.Disconnecting) return;

            // 网页认证模式：「立即连接」= 打开认证页去登录，不是拨号
            if (IsPortalMode())
            {
                OpenWebAuthWindowOnStartup();
                return;
            }

            // 第二参数 false = 用户点按钮发起的：失败时可以正常弹窗（他就坐在屏幕前）
            StartDial(_state == ConnState.Connected, false);
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DoDisconnect(true);
        }

        /// <summary>
        /// 发起拨号。
        /// </summary>
        /// <param name="reconnectFirst">true = 拨之前先把现有的连接断掉再重拨。</param>
        /// <param name="auto">
        /// true = 这次是**自动重连**发起的（不是用户点的按钮）。
        ///
        /// ⚠️ 为什么要区分来源（2026-10-01 审计确认）：
        ///   失败时两者必须区别对待。手动失败弹窗是对的 —— 用户就在屏幕前，
        ///   他需要知道失败原因；但**自动重连失败绝不能弹窗**：
        ///   MessageBox 的模态消息循环**不阻塞** DispatcherTimer，于是第一个弹窗还挂着，
        ///   30 秒后退避到期又弹第二个，整夜能堆几十个模态框。
        ///   用户睡觉之后密码失效 / 欠费就会触发，而他完全不知情。
        /// </param>
        private void StartDial(bool reconnectFirst, bool auto)
        {
            if (_current == null || string.IsNullOrEmpty(_current.Name))
            {
                MessageBox.Show("请先添加一个账号。\n\n点击右侧「管理账号」，填写连接名称、宽带账号与密码。",
                    "校园网助手", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 网线没插好就别拨了 —— 只会得到一条看不懂的拨号错误码，
            // 还平白给学校攒一次失败记录（学校对频繁拨号是会限速的）。
            // 直接告诉他，并把"自动连接"武装上：网线插回去会自动连（见 OnTick）。
            if (!CheckLinkNow())
            {
                // ⚠️ 这个局部变量**不能**叫 auto —— 会和新加的同名参数撞（CS0136）。
                //    它问的是"网线插回来要不要自动连"，和"本次是不是自动重连发起的"是两回事。
                bool autoLink = AutoReconnectEnabled();
                if (autoLink) _autoReconnectArmed = true;
                EnterWaitingLink();
                MessageBox.Show("网线没有插好（或者另一头的设备没通电）。\n\n"
                    + (autoLink ? "插好网线后不用点任何按钮，程序会自动连接。"
                            : "插好网线后再点一次「立即连接」。"),
                    "校园网助手", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyState(ConnState.Connecting, "正在连接…", "正在与服务器协商，请稍候");

            string entryName = _current.Name;
            string user = _current.User;
            string pass = _current.Password;

            Thread t = new Thread(delegate()
            {
                try
                {
                    if (reconnectFirst && DialEngine.IsDialConnected())
                    {
                        string m;
                        DialEngine.Disconnect(entryName, out m);
                        Thread.Sleep(1200);
                    }

                    string msg;
                    if (!DialEngine.EntryExists(entryName))
                    {
                        DialEngine.EnsureEntry(entryName, user, pass, out msg);
                    }

                    DialEngine.DialResult res = DialEngine.Dial(entryName, user, pass);
                    Log.Info("拨号结果: exit=" + res.ExitCode + " success=" + res.Success + " :: " + res.Message);

                    SafeInvoke(delegate()
                    {
                        if (res.Success)
                        {
                            _connectedAt = DateTime.Now;
                            _autoReconnectArmed = true;
                            _autoReconnectAttempts = 0;   // 连上了，重试计数归零
                            _autoDialFailStreak = 0;      // 失败连击也归零（下次失败重新从"第一次"算）
                            ApplyState(ConnState.Connected, "已连接",
                                "已通过宽带拨号上网（" + entryName + "）");
                            AddHistory("连接成功 · " + entryName);
                            RefreshParamCards();
                            ShowBalloon("连接成功", entryName + " 已连接");
                        }
                        else
                        {
                            ApplyState(ConnState.Error, "连接失败", res.Message);
                            AddHistory("连接失败 · " + entryName + " · " + res.Message);

                            if (auto)
                            {
                                // 自动重连失败 —— **不弹窗**。
                                //   弹窗的模态消息循环不阻塞 DispatcherTimer，第一个框还挂着，
                                //   30 秒后退避到期又弹一个，整夜能堆几十个（2026-10-01 审计确认）。
                                //   状态条已经如实写明失败原因，用户看界面就知道出了事；
                                //   这里只对**本次连败的第一次**发个托盘气泡，之后静默 ——
                                //   免得每 30 秒吵他一次。
                                _autoDialFailStreak++;
                                if (_autoDialFailStreak == 1)
                                {
                                    ShowBalloon("自动重连失败", res.Message);
                                }
                            }
                            else
                            {
                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine(res.Message);
                                if (res.Hints.Count > 0)
                                {
                                    sb.AppendLine();
                                    sb.AppendLine("建议排查：");
                                    foreach (string h in res.Hints) sb.AppendLine("· " + h);
                                }
                                MessageBox.Show(sb.ToString(), "连接失败",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("拨号线程异常", ex);
                    SafeInvoke(delegate()
                    {
                        ApplyState(ConnState.Error, "连接异常", ex.Message);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 后台线程往 UI 线程投递动作的**安全包装**。
        ///
        /// ⚠️ 拨号 / 断开这类工作线程必须走它，不能直接 Dispatcher.Invoke：
        ///    退出流程里 Dispatcher 已经关闭，直接 Invoke 会抛 TaskCanceledException；
        ///    而如果那个 catch 里还再 Invoke 一次，就成了后台线程未捕获异常 ——
        ///    直接崩进程（2026-10-01 审计确认的"拨号中途点退出偶发崩溃"就是这个）。
        ///
        /// 行为：正在退出就**直接丢弃**（UI 都要没了，更新它没有意义）；
        ///      投递失败也只记一条日志，绝不往外抛。
        /// </summary>
        private void SafeInvoke(Action act)
        {
            if (_closing) return;
            try
            {
                Dispatcher.Invoke(act);
            }
            catch (Exception ex)
            {
                Log.Warn("UI 回调投递失败（程序可能正在退出）: " + ex.Message);
            }
        }

        private void DoDisconnect(bool userInitiated)
        {
            if (_current == null) return;

            ApplyState(ConnState.Disconnecting, "正在断开…", "正在结束拨号连接");
            string entryName = _current.Name;

            Thread t = new Thread(delegate()
            {
                // ⚠️ 整个委托体都要包 try/catch —— 以前这里连 try 都没有，
                //    Dispatcher.Invoke 是裸奔的（2026-10-01 审计确认）。
                //    退出流程中 Invoke 会抛 TaskCanceledException，没有 catch 就是
                //    后台线程未捕获异常，直接崩进程。
                try
                {
                    string msg;
                    bool ok = DialEngine.Disconnect(entryName, out msg);
                    Log.Info("断开结果: " + ok + " :: " + msg);

                    SafeInvoke(delegate()
                    {
                        _connectedAt = DateTime.MinValue;
                        if (lblOnlineTime != null) lblOnlineTime.Text = "";

                        if (userInitiated)
                        {
                            // 用户主动断开：解除自动重连，避免过一会儿自己又连上
                            _autoReconnectArmed = false;
                            _lastAutoTry = DateTime.MinValue;
                            _autoReconnectAttempts = 0;
                        }
                        else
                        {
                            _lastAutoTry = DateTime.Now;
                        }

                        if (ok)
                        {
                            ApplyState(ConnState.Idle, "尚未连接", msg);
                            if (userInitiated) AddHistory("手动断开 · " + entryName);
                            RefreshParamCards();
                        }
                        else
                        {
                            ApplyState(ConnState.Error, "断开异常", msg);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("断开线程异常", ex);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ==================================================================
        // 账号
        // ==================================================================

        internal void RefreshAccountList()
        {
            _suppressAccountEvent = true;
            try
            {
                if (cmbAccount != null)
                {
                    cmbAccount.Items.Clear();
                    foreach (ConfigStore.Account a in _accounts) cmbAccount.Items.Add(a.Name);

                    string last = ConfigStore.GetString(_settings, "LastAccount", "");
                    int idx = -1;
                    for (int i = 0; i < _accounts.Count; i++)
                    {
                        if (string.Equals(_accounts[i].Name, last, StringComparison.Ordinal)) { idx = i; break; }
                    }
                    if (idx < 0 && _accounts.Count > 0) idx = 0;

                    if (idx >= 0)
                    {
                        cmbAccount.SelectedIndex = idx;
                        _current = _accounts[idx];
                    }
                    else
                    {
                        _current = null;
                    }
                }
            }
            finally
            {
                _suppressAccountEvent = false;
            }
            RefreshQuickSwitch();
        }

        /// <summary>
        /// 「首次使用引导条」该不该显示。规则只有两条，都写在这里。
        ///
        /// 判定规则：**还没有任何账号** 且 **没点过「知道了」**。
        ///
        /// 为什么拿「没有账号」当主判据 ——
        ///   配过账号的人本来就不需要这提示，而这个条件是天然准确的，
        ///   不用额外维护一个「是不是第一次运行」的标记。那种标记一旦写歪就是永久性的错：
        ///   要么该显示的永远不显示，要么用户关不掉。
        ///
        /// ⚠️ 为什么抽成 static 纯函数（而不是直接写在 UpdateFirstRunTip 里）——
        ///    真正的显示还要经过 WPF 的 Visibility，而「离屏窗口上的可见性」这件事
        ///    本身就不太可靠（这也是为什么"静默场景不弹窗"那条至今只能人工验，
        ///    自测里只能做机制检查）。把**判定规则**单独拎出来，就能被自测直接断言；
        ///    规则对了，剩下的"赋不赋值给 Visibility"就没有歧义了。
        ///
        /// 调用时机：① 窗口初始化（ApplyTheme → RefreshQuickSwitch）
        ///           ② 账号增删之后（见 RefreshQuickSwitch 开头）
        /// </summary>
        internal static bool ShouldShowFirstRunTip(int accountCount, bool dismissed)
        {
            // 有账号 = 已经配过，不需要引导
            // 点过「知道了」 = 用户明确表示不需要，尊重他，别再烦
            return accountCount == 0 && !dismissed;
        }

        /// <summary>按当前账号数与「知道了」标记，刷新引导条的显示与否。</summary>
        internal void UpdateFirstRunTip()
        {
            if (_firstRunTip == null) return;

            bool dismissed = ConfigStore.GetBool(_settings, "FirstRunTipDismissed", false);
            int n = (_accounts == null) ? 0 : _accounts.Count;

            _firstRunTip.Visibility = ShouldShowFirstRunTip(n, dismissed)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>用户点了引导条上的「知道了」—— 记下来，以后不再显示。</summary>
        private void DismissFirstRunTip()
        {
            ConfigStore.SetBool(_settings, "FirstRunTipDismissed", true);

            string msg;
            if (!ConfigStore.SaveSettings(_settings, out msg) && !string.IsNullOrEmpty(msg))
            {
                // 存不上也不拦着用户干别的 —— 大不了下次再弹一次，不影响功能
                Log.Warn("保存「首次提示已关闭」失败: " + msg);
            }

            UpdateFirstRunTip();
            Log.Info("用户关闭了首次使用引导");
        }

        internal void RefreshQuickSwitch()
        {
            if (quickSwitchPanel == null) return;
            quickSwitchPanel.Children.Clear();

            // ⚠️ 必须放在下面那个 `return` **之前** ——
            //    没有账号时函数会提前返回，写到最后就永远执行不到（而"没有账号"恰好
            //    正是这个提示最该出现的时候）。
            UpdateFirstRunTip();

            if (_accounts.Count == 0)
            {
                quickSwitchPanel.Children.Add(MakeHint("还没有账号，点「管理账号」添加"));
                return;
            }

            int shown = 0;
            foreach (ConfigStore.Account a in _accounts)
            {
                if (shown >= 4) break;
                shown++;
                ConfigStore.Account captured = a;

                var b = MakeGhostButton(a.Name, delegate()
                {
                    // ⚠️ 已连接（或免打扰保活）状态下切换账号，必须**先把现有连接断掉**。
                    //
                    // 2026-10-01 审计确认的老问题：原代码直接就 ApplyState(Idle, "已选择账号")，
                    // 于是界面显示"尚未连接"，但 RAS 连接其实**还挂着**：
                    //   · 质量监测和心跳保活是按 state==Connected 启停的 → 两个都停了，
                    //     而学校有"空闲踢下线"策略，停发心跳可能真就把人踢了；
                    //   · OnTick 里唯一的自我纠正路径要求 !DialEngine.IsDialConnected()，
                    //     连接还在、进不去 —— 错误状态会一直持续到下次真掉线为止。
                    bool wasConnected = (_state == ConnState.Connected || _state == ConnState.Quiet);

                    if (wasConnected)
                    {
                        MessageBoxResult r = MessageBox.Show(
                            "当前已连接。切换账号会先断开现有连接，确定吗？",
                            "切换账号", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                        if (r != MessageBoxResult.OK) return;
                    }

                    _current = captured;
                    if (cmbAccount != null)
                    {
                        for (int i = 0; i < cmbAccount.Items.Count; i++)
                        {
                            if (string.Equals(cmbAccount.Items[i] as string, captured.Name, StringComparison.Ordinal))
                            {
                                cmbAccount.SelectedIndex = i;
                                break;
                            }
                        }
                    }

                    if (wasConnected)
                    {
                        // 断开是异步的，它自己的完成回调会把状态置成 Idle
                        //（此时 _current 已经是新账号了，语义正确）—— 所以这里不用再 ApplyState。
                        // Disconnecting 期间主按钮本来就是禁用的（busy 判定），不会抢跑。
                        DoDisconnect(true);
                        return;
                    }

                    ApplyState(ConnState.Idle, "已选择账号", "点击「立即连接」开始拨号");
                });
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(0, 0, 0, 6);
                b.FontSize = 11;
                quickSwitchPanel.Children.Add(b);
            }
        }

        private void CmbAccount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAccountEvent) return;
            int i = cmbAccount != null ? cmbAccount.SelectedIndex : -1;
            if (i >= 0 && i < _accounts.Count)
            {
                _current = _accounts[i];
                _settings["LastAccount"] = _current.Name;
            }
        }

        private void BtnManageAccounts_Click(object sender, RoutedEventArgs e)
        {
            var w = new AccountWindow(this, _accounts);
            w.Owner = this;
            w.ShowDialog();

            LoadAll();
            RefreshAccountList();
            RefreshParamCards();
        }

        // ==================================================================
        // 历史
        // ==================================================================

        internal void AddHistory(string line)
        {
            string item = DateTime.Now.ToString("MM-dd HH:mm") + "  " + line;
            _history.Insert(0, item);
            while (_history.Count > 30) _history.RemoveAt(_history.Count - 1);
            RefreshHistoryUi();
            SaveAll();
        }

        internal void RefreshHistoryUi()
        {
            if (historyPanel == null) return;
            historyPanel.Children.Clear();

            if (_history.Count == 0)
            {
                historyPanel.Children.Add(MakeHint("暂无记录"));
                return;
            }

            foreach (string h in _history)
            {
                historyPanel.Children.Add(new TextBlock
                {
                    Text = h,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Theme.TextMuted),
                    Margin = new Thickness(0, 0, 0, 5),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
        }

        // ==================================================================
        // 子窗口
        // ==================================================================

        internal void OpenHealthWindow()
        {
            if (_healthWindow != null && _healthWindow.IsLoaded)
            {
                _healthWindow.Activate();
                return;
            }
            _healthWindow = new HealthWindow(_current != null ? _current.Name : "");
            _healthWindow.Owner = this;
            _healthWindow.Show();
        }

        private void BtnSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            if (_speedTestWindow != null && _speedTestWindow.IsLoaded)
            {
                _speedTestWindow.Activate();
                return;
            }

            _speedTestWindow = new SpeedTestWindow();
            _speedTestWindow.Owner = this;
            _speedTestWindow.Closed += delegate(object s, EventArgs a) { _speedTestWindow = null; };
            _speedTestWindow.Show();
        }

        internal void OpenSettingsWindow()
        {
            if (_settingsWindow != null && _settingsWindow.IsLoaded)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Owner = this;
            _settingsWindow.Show();
        }

        /// <summary>用系统默认浏览器打开链接。</summary>
        internal void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开链接失败: " + ex.Message);
                MessageBox.Show("打开链接失败，可以把下面这个地址复制到浏览器里打开：\n\n" + url,
                    "校园网助手", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ==================================================================
        // 托盘
        // ==================================================================

        /// <summary>
        /// 取程序图标（托盘用）。直接复用编译时嵌进 exe 的那份（/win32icon），
        /// 这样不用额外维护一份资源文件；取不到就退回系统图标，
        /// 绝不能因为图标问题让程序起不来。
        /// </summary>
        [System.Runtime.InteropServices.DllImport("shell32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        /// <summary>
        /// 取程序自身的图标（托盘、窗口都用它）。
        ///
        /// ⚠️ 为什么不用 Icon.ExtractAssociatedIcon（2026-10-01 换掉的原实现）——
        ///    assets/logo.ico 的帧**全部是 PNG 压缩格式**（16~256 共 7 帧）。
        ///    而 .NET Framework 的 System.Drawing **解不了 PNG 压缩的图标帧**
        ///    （GDI+ 的老限制，.NET Core 3.0 才修好）。它会抛异常，
        ///    被 catch 吞掉后返回 SystemIcons.Shield —— 表现就是托盘上挂着一个
        ///    **蓝色盾牌**，用户根本认不出那是校园网助手。
        ///
        ///    这个坑以前不显形：csc 的 /win32icon 会先把图转成 BMP 再嵌入；
        ///    换成 /win32res（为了带版本号）之后，rc.exe 是**原样嵌入 PNG** 的，
        ///    于是 .NET 解不动了 —— 属于换构建方式带出来的回归，实测才发现。
        ///
        ///    改用 ExtractIconEx 拿 HICON：那是系统已经解析好的位图句柄，
        ///    不经过 .NET 的 ICO 解析，PNG 帧照样能用。
        /// </summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            string exe = null;
            try { exe = System.Reflection.Assembly.GetEntryAssembly().Location; }
            catch { }

            if (!string.IsNullOrEmpty(exe))
            {
                IntPtr[] large = new IntPtr[1];
                IntPtr[] small = new IntPtr[1];
                try
                {
                    uint n = ExtractIconExW(exe, 0, large, small, 1);
                    IntPtr h = (large[0] != IntPtr.Zero) ? large[0] : small[0];
                    if (n > 0 && h != IntPtr.Zero)
                    {
                        System.Drawing.Icon ic;
                        using (System.Drawing.Icon tmp = System.Drawing.Icon.FromHandle(h))
                        {
                            // FromHandle 只是包了一层，h 一释放它就没用了 —— 必须 Clone
                            ic = (System.Drawing.Icon)tmp.Clone();
                        }
                        // 这两个句柄是 ExtractIconEx 给我们的，得自己还回去
                        if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                        if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                        return ic;
                    }
                    if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                    if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                }
                catch (Exception ex)
                {
                    Log.Warn("用 ExtractIconEx 取程序图标失败: " + ex.Message);
                }

                // 兜底：老的 .NET 方式（对 BMP 格式的 ico 有效，PNG 的会抛异常）
                try
                {
                    System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (ico != null) return ico;
                }
                catch (Exception ex)
                {
                    Log.Warn("ExtractAssociatedIcon 也失败（多半是 PNG 压缩的 ico）: " + ex.Message);
                }
            }

            Log.Warn("取不到程序图标，退回系统默认盾牌");
            return System.Drawing.SystemIcons.Shield;
        }

        /// <summary>
        /// 给自测用：程序图标是不是**退回了系统默认盾牌**（也就是取值失败）。
        ///
        /// ⚠️ 判据是「和有系统盾牌逐像素比一遍」而不是看尺寸 ——
        ///    盾牌和真 logo 都可能是 32×32，光看尺寸分辨不出来。
        ///    这个回归曾经真的发生过：assets/logo.ico 全是 PNG 压缩帧，
        ///    换成 /win32res 之后 .NET 的 System.Drawing 解不了，
        ///    于是静默退回盾牌，托盘上挂着一个用户根本不认识的图标。
        ///    加了这条断言，以后换构建方式或换图标文件时会立刻报警。
        /// </summary>
        internal static bool DebugIconIsFallbackShield()
        {
            try
            {
                using (System.Drawing.Bitmap a = LoadAppIcon().ToBitmap())
                using (System.Drawing.Bitmap b = System.Drawing.SystemIcons.Shield.ToBitmap())
                {
                    if (a.Width != b.Width || a.Height != b.Height) return false;
                    for (int y = 0; y < a.Height; y++)
                    {
                        for (int x = 0; x < a.Width; x++)
                        {
                            if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
                        }
                    }
                    return true;   // 一模一样 → 就是盾牌
                }
            }
            catch (Exception ex)
            {
                Log.Warn("比较程序图标失败: " + ex.Message);
                return true;   // 取不到也算有问题
            }
        }

        /// <summary>给自测用：程序图标的尺寸，用来确认确实取到了真图标。</summary>
        internal static string DebugAppIconInfo()
        {
            try
            {
                // ⚠️ 这里**故意不 Dispose**：LoadAppIcon 失败时返回的是
                //    SystemIcons.Shield 这个**共享实例**，Dispose 它会波及全进程。
                System.Drawing.Icon ic = LoadAppIcon();
                return ic.Width + "x" + ic.Height;
            }
            catch (Exception ex)
            {
                return "取图标失败: " + ex.Message;
            }
        }

        /// <summary>给自测用：四张状态图标有没有生成出来（托盘配色是否可用）。</summary>
        internal static string DebugTrayIconShapes()
        {
            string exe = null;
            try { exe = System.Reflection.Assembly.GetEntryAssembly().Location; } catch { }
            System.Drawing.Icon src = LoadAppIcon();
            string[] names = new string[] { "绿", "橙", "红", "灰" };
            System.Drawing.Color[] tints = new System.Drawing.Color[]
            {
                Dr(Theme.Ok), Dr(Theme.Warn), Dr(Theme.Err),
                System.Drawing.Color.FromArgb(255, 140, 140, 140)
            };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < tints.Length; i++)
            {
                if (i > 0) sb.Append(" / ");
                try
                {
                    System.Drawing.Icon ic = BuildStateIcon(src, tints[i]);
                    sb.Append(names[i]).Append("=").Append(ic == null ? "null"
                        : ic.Width + "x" + ic.Height);
                }
                catch (Exception ex)
                {
                    sb.Append(names[i]).Append("=失败(").Append(ex.Message).Append(")");
                }
            }
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static System.Drawing.Color Dr(System.Windows.Media.Color c)
        {
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        /// <summary>
        /// 把 logo 图标单色化成指定颜色，用来表示连接状态。
        ///
        /// ⚠️ 为什么不是「直接往目标色混」（第一版就是这么做的，被推翻了）——
        ///    logo 的底色是蓝的，加法混色会把蓝一起带进来：混绿得到的是**青色**、
        ///    混红得到的是**紫红**，颜色不纯、语义就糊了。
        ///    离线按 16px（托盘的真实尺寸）渲染对比过，"青"和"绿"根本分不清。
        ///
        ///    改成「先取明度、再用目标色乘上去」后，颜色是纯的，
        ///    同时**保留了 logo 的明暗层次**（学士帽仍然是亮的、底仍然是暗的），
        ///    缩到 16px 也还认得出是什么图形。
        ///
        /// ⚠️ floor 给最暗处留一点亮度：
        ///    纯乘法会把暗部直接压成黑块，而托盘底色偏深，那就彻底看不见了。
        ///
        /// ⚠️ 句柄归属：GetHicon() 拿到的原生句柄**必须由我们 DestroyIcon**，
        ///    否则每次调用泄漏一个。这里返回的是 Clone，脱离原生句柄独立存在。
        /// </summary>
        private static System.Drawing.Icon BuildStateIcon(System.Drawing.Icon src,
            System.Drawing.Color tint)
        {
            if (src == null) return null;
            const float floor = 0.25f;   // 最暗处也保留 25% 的目标色亮度
            const int SZ = 32;           // 见下面关于尺寸的注释

            try
            {
                // ⚠️ 必须**先缩小再逐像素处理**，不能拿原图直接干：
                //    logo.ico 是 256×256，GetPixel/SetPixel 是出了名的慢，
                //    65536 像素 × 两次调用 × 4 张图 ≈ 半秒的启动卡顿。
                //    托盘图标实际只用得到 16~32px，先重采样到 32×32 再处理：
                //    1024 像素 × 4 张 ≈ 几毫秒，而且缩放本身带了插值，边缘还更顺。
                using (System.Drawing.Bitmap srcBmp = src.ToBitmap())
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(SZ, SZ))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.Clear(System.Drawing.Color.Transparent);
                        g.DrawImage(srcBmp, 0, 0, SZ, SZ);
                    }

                    for (int y = 0; y < SZ; y++)
                    {
                        for (int x = 0; x < SZ; x++)
                        {
                            System.Drawing.Color p = bmp.GetPixel(x, y);
                            if (p.A == 0) continue;   // 全透明像素不碰

                            // 先算明度（人眼对绿最敏感、对蓝最迟钝，所以不是简单取平均）
                            float gray = p.R * 0.299f + p.G * 0.587f + p.B * 0.114f;
                            float f = floor + (1f - floor) * (gray / 255f);

                            int r = (int)(tint.R * f);
                            int g2 = (int)(tint.G * f);
                            int b = (int)(tint.B * f);
                            bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(p.A,
                                r > 255 ? 255 : r, g2 > 255 ? 255 : g2, b > 255 ? 255 : b));
                        }
                    }

                    IntPtr h = bmp.GetHicon();
                    try
                    {
                        using (System.Drawing.Icon tmp = System.Drawing.Icon.FromHandle(h))
                        {
                            // FromHandle 只是包了一层，h 一释放它就没用了 —— 必须 Clone
                            return (System.Drawing.Icon)tmp.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(h);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("生成状态图标失败: " + ex.Message);
                return src;   // 配色挂了也不能变成没图标，退回原图
            }
        }

        /// <summary>
        /// 一次生成四张状态图标备用（进程内只做一次）。
        /// 颜色语义跟界面上那套保持一致（见 Theme.Ok/Warn/Err）：
        ///     已连接 → 绿　　连接中/断开中 → 橙　　出错 → 红　　其余（未连/待机/免打扰）→ 灰
        /// </summary>
        private void InitTrayIcons()
        {
            System.Drawing.Icon src = LoadAppIcon();
            if (src == null) return;

            _trayIcOk = BuildStateIcon(src, Dr(Theme.Ok));
            _trayIcBusy = BuildStateIcon(src, Dr(Theme.Warn));
            _trayIcErr = BuildStateIcon(src, Dr(Theme.Err));
            _trayIcIdle = BuildStateIcon(src, System.Drawing.Color.FromArgb(255, 140, 140, 140));
        }

        /// <summary>
        /// 按当前连接状态切换托盘图标。
        ///
        /// ⚠️ 先把 _state 归成四类再比，不直接比 _state：
        ///    WaitingLink / Quiet / Disconnecting 这些状态共用同一张图，
        ///    直接比的话每切一次状态就要往 NotifyIcon.Icon 赋一次值 ——
        ///    而 NotifyIcon 每次赋值都会让托盘重绘，白白闪一下。
        /// </summary>
        private void UpdateTrayIcon()
        {
            if (trayIcon == null || _trayIcOk == null) return;

            ConnState bucket;
            System.Drawing.Icon want;
            if (_state == ConnState.Connected)
            {
                bucket = ConnState.Connected; want = _trayIcOk;
            }
            else if (_state == ConnState.Connecting || _state == ConnState.Disconnecting)
            {
                bucket = ConnState.Connecting; want = _trayIcBusy;
            }
            else if (_state == ConnState.Error)
            {
                bucket = ConnState.Error; want = _trayIcErr;
            }
            else
            {
                bucket = ConnState.Idle; want = _trayIcIdle;
            }

            if (bucket == _trayIcShown) return;
            _trayIcShown = bucket;
            trayIcon.Icon = want;
        }

        private void InitTray()
        {            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMiStatus = new System.Windows.Forms.ToolStripMenuItem("状态：未连接");
            trayMiStatus.Enabled = false;
            trayMenu.Items.Add(trayMiStatus);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("显示主窗口", null, delegate(object s, EventArgs e) { ShowMainWindow(); });
            trayMiDial = new System.Windows.Forms.ToolStripMenuItem("连接上网", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { StartDial(false, false); }); });
            trayMenu.Items.Add(trayMiDial);
            trayMiDisconnect = new System.Windows.Forms.ToolStripMenuItem("断开连接", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { DoDisconnect(true); }); });
            trayMenu.Items.Add(trayMiDisconnect);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("网络体检", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { OpenHealthWindow(); }); });
            trayMenu.Items.Add("退出程序", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { ExitApp(); }); });

            // 先生成四张状态图标（绿/橙/红/灰），后面只切引用
            InitTrayIcons();

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                // 刚启动时还没连上，直接挂灰色那张；连上后由 UpdateTrayIcon 换掉
                Icon = _trayIcIdle != null ? _trayIcIdle : LoadAppIcon(),
                Text = "校园网助手",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate(object s, EventArgs e) { ShowMainWindow(); };

            // 点了气泡 → 如果正等着"用户确认才开认证页"（自动重连的静默分支），
            // 这时候才真的把它打开。这样静默/游戏场景下就不会无预警弹窗。
            trayIcon.BalloonTipClicked += delegate(object s, EventArgs e)
            {
                OpenPendingPortalReauthIfAny();
            };
        }

        private void UpdateTrayState(string text)
        {
            if (trayMiStatus != null) trayMiStatus.Text = "状态：" + (text ?? "");
            if (trayIcon != null)
            {
                string t = "校园网助手 - " + (text ?? "");
                if (t.Length > 60) t = t.Substring(0, 60);
                trayIcon.Text = t;
            }

            // 顺带把图标色调也切过去 —— 这样不用打开窗口、也不用把鼠标移上去看提示，
            // 扫一眼托盘就知道通没通（绿=已连，灰=没连，橙=在连，红=出错）。
            UpdateTrayIcon();
            if (trayMiDial != null)
            {
                trayMiDial.Enabled = _state != ConnState.Connected && _state != ConnState.Connecting;
            }
            if (trayMiDisconnect != null)
            {
                trayMiDisconnect.Enabled = _state == ConnState.Connected;
            }
        }

        /// <summary>
        /// 接住「第二个实例叫我出来」的自定义消息，转到自己的显示流程。
        ///
        /// 为什么非要自己接：App 那边找不到窗口时是用 Win32 的 ShowWindow(SW_SHOW)
        /// 硬把句柄点亮的。句柄是亮了，但 WPF 自己并不知道窗口"已经显示"，
        /// 于是之后用户一点托盘图标（走 ShowMainWindow → Activate）就抛
        /// 「显示 Window 之前，无法调用 DragMove 或 Activate()」。
        /// 接住这条消息走正常显示流程，两边状态才一致。
        /// </summary>
        private void HookActivateMessage()
        {
            try
            {
                _activateMsg = WinApi.RegisterWindowMessage("CampusNetHelper_Activate");
                if (_activateMsg == 0) return;

                System.Windows.Interop.HwndSource src =
                    System.Windows.Interop.HwndSource.FromHwnd(
                        new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (src != null) src.AddHook(OnWindowMessage);
            }
            catch (Exception ex)
            {
                Log.Warn("装激活消息钩子失败: " + ex.Message);
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_activateMsg != 0 && msg == (int)_activateMsg)
            {
                handled = true;
                ShowMainWindow();
            }
            return IntPtr.Zero;
        }

        internal void ShowMainWindow()
        {
            Dispatcher.Invoke(delegate()
            {
                try
                {
                    ShowInTaskbar = true;
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Visibility = Visibility.Visible;

                    // ⭐ 开机自启用的是「计划任务」档，它带 /silent 启动 ——
                    //    那种启动方式下 App 从头到尾没调用过 Show()，只 EnsureHandle() 建了个句柄。
                    //    这种状态下 WPF 认为窗口"还没显示"，而且【只把 Visibility 设成 Visible 不算数】：
                    //    实测 IsLoaded 依旧是 false，连系统层面 IsWindowVisible 都还是 false。
                    //    此时调用 Activate() 会抛「显示 Window 之前，无法调用 DragMove 或 Activate()」，
                    //    用户看到的就是一个吓人的 .NET 报错框。
                    //    所以这里补一次 Show()；显示过的话跳过（Show() 幂等，重复调用无害）。
                    if (!IsLoaded) Show();

                    Activate();
                }
                catch (Exception ex)
                {
                    // 显示窗口失败不该让整个程序崩掉 —— 记日志，继续在托盘里干活
                    Log.Warn("显示主窗口失败: " + ex.Message);
                }
            });
        }

        internal void ShowBalloon(string title, string msg)
        {
            try
            {
                // 免打扰时段内不弹任何提示 —— 只说给日志听，不打扰人
                if (InNightQuiet())
                {
                    Log.Info("（免打扰时段内不弹提示）" + title + "：" + (msg ?? "").Replace("\n", " "));
                    return;
                }

                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(3000, title, msg, System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch { }
        }

        internal void ExitApp()
        {
            // ⚠️ 第一件事就是把"正在退出"立起来，再动别的。
            //    这台机器上此刻可能还有拨号线程在跑（拨号最长 60 秒），
            //    它干完活会 Dispatcher.Invoke 回来 —— 而下面那句 Shutdown 会关掉 Dispatcher。
            _closing = true;

            // 顺手补齐退出清理：OnClosing 的两条路径都做了这两句，这里原先漏了。
            // 漏掉的后果：Shutdown 期间 tick 定时器和 QualityMonitor 的 Updated 事件
            // 还在往已经关掉的 Dispatcher 上投递。
            _quality.Shutdown();
            _keepAlive.Shutdown();

            SaveAll();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            Application.Current.Shutdown();
        }

        // ==================================================================
        // 关闭
        // ==================================================================

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // ⭐ 关机 / 注销时必须老实退出，不能像"关窗口"那样 cancel 掉。
            //
            // 2026-09-29 的事故根因就在这里：开机自启+关闭到托盘的用户点关机时，
            // 程序 cancel 掉关闭、缩回托盘继续跑 → Windows 等超时后【强制结束】进程。
            // 而那一刻它正在写配置文件 —— 内容是"先清空文件再写"，结果文件被清空后
            // 只写出了 UTF-8 文件头，正文还在缓冲区里就随进程一起没了，
            // 账号文件从此只剩 3 字节，第二天开机变成"没有账号"。
            if (_sessionEnding)
            {
                if (!_closing)
                {
                    _closing = true;
                    Log.Info("系统正在关机/注销，立即保存并退出（不缩到托盘）");
                    _quality.Shutdown();
                    _keepAlive.Shutdown();
                    SaveAll();
                    if (trayIcon != null)
                    {
                        trayIcon.Visible = false;
                        trayIcon.Dispose();
                        trayIcon = null;
                    }

                    // ⚠️ 只"不 cancel"还不够：App 里设的是 ShutdownMode.OnExplicitShutdown，
                    //    关掉唯一的窗口进程并不会退出，会留一个没有窗口的进程等着被系统杀掉 ——
                    //    那就又变成"被强制结束"了，白修。
                    //    这里必须显式 Shutdown；用 BeginInvoke 排队执行，避免在 Closing 里重入。
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate()
                    {
                        try { Application.Current.Shutdown(); }
                        catch { }
                    }));
                }
                return;   // 不设 e.Cancel → 窗口正常关闭
            }

            if (!CloseToTrayEnabled())
            {
                _closing = true;
                _quality.Shutdown();
                _keepAlive.Shutdown();
                SaveAll();
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                Application.Current.Shutdown();
                return;
            }

            e.Cancel = true;
            SaveAll();
            Visibility = Visibility.Hidden;
            ShowInTaskbar = false;
            ShowBalloon("仍在后台运行", "程序已最小化到托盘，会继续守护网络连接。\n需要退出请右键托盘图标选择「退出程序」。");
        }
    }
}
