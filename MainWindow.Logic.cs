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
        internal enum ConnState { Idle, Connecting, Connected, Disconnecting, Error }

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
        private bool _closing = false;

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
                    TryAutoReconnect();
                }
            }
        }

        // ==================================================================
        // 配置
        // ==================================================================

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
            int interval, bool keepAlive, int keepAliveMinutes)
        {
            ConfigStore.SetBool(_settings, "AutoReconnect", autoReconnect);
            ConfigStore.SetBool(_settings, "CloseToTray", closeToTray);
            _settings["ReconnectInterval"] = interval.ToString();
            ConfigStore.SetBool(_settings, "KeepAlive", keepAlive);
            _settings["KeepAliveInterval"] = keepAliveMinutes.ToString();
            SaveAll();

            // 开关可能刚被改掉，立刻生效，不用等下次连接状态变化
            _keepAlive.SetActive(_state == ConnState.Connected && keepAlive, keepAliveMinutes);

            string note = ReconcileStartupCopy(silent);

            Log.Info("设置已更新: 自动重连=" + autoReconnect + " 开机自启=" + silent
                + " 关闭到托盘=" + closeToTray + " 间隔=" + interval
                + " 保活=" + keepAlive + " 保活间隔=" + keepAliveMinutes + "分钟");
            return note;
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
        // 网页认证
        // ==================================================================

        internal string WebAuthUrl()
        {
            string v = ConfigStore.GetString(_settings, "WebAuthUrl", "");
            if (!string.IsNullOrEmpty(v)) return v;
            // 没填过就用编译时的默认值（本校专属，放在 SiteConfig，不进公开仓库）
            return SiteConfig.DefaultWebAuthUrl;
        }

        internal void SaveWebAuthUrl(string url)
        {
            _settings["WebAuthUrl"] = url ?? "";
            SaveAll();
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
            _webAuthWindow.Closed += delegate(object s, EventArgs a) { _webAuthWindow = null; };
            _webAuthWindow.Show();
        }

        // ==================================================================
        // 定时器
        // ==================================================================

        private void InitTimers()
        {
            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tickTimer.Tick += (s, e) => OnTick();
            _tickTimer.Start();

            _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _speedTimer.Tick += (s, e) => SampleSpeed();
            _speedTimer.Start();
        }

        private void OnTick()
        {
            if (_state == ConnState.Connected)
            {
                if (_connectedAt != DateTime.MinValue && lblOnlineTime != null)
                {
                    TimeSpan ts = DateTime.Now - _connectedAt;
                    lblOnlineTime.Text = "在线 " + string.Format("{0:00}:{1:00}:{2:00}",
                        (int)ts.TotalHours, ts.Minutes, ts.Seconds);
                }

                if (!DialEngine.IsDialConnected())
                {
                    Log.Warn("检测到拨号连接已断开");
                    AddHistory("连接断开");
                    _connectedAt = DateTime.MinValue;
                    if (lblOnlineTime != null) lblOnlineTime.Text = "";
                    ApplyState(ConnState.Idle, "连接已断开", "拨号连接已中断");
                    RefreshParamCards();
                    return;
                }

                RefreshParamCards();
            }

            if (_autoReconnectArmed
                && (_state == ConnState.Idle || _state == ConnState.Error)
                && AutoReconnectEnabled() && _current != null)
            {
                if (!DialEngine.IsDialConnected()) TryAutoReconnect();
            }
        }

        private void TryAutoReconnect()
        {
            if ((DateTime.Now - _lastAutoTry).TotalSeconds < NextRetryDelay()) return;
            _lastAutoTry = DateTime.Now;
            _autoReconnectAttempts++;
            Log.Info("自动重连：第 " + _autoReconnectAttempts + " 次尝试恢复连接 "
                + (_current != null ? _current.Name : "(无)"));
            StartDial(false);
        }

        // ==================================================================
        // 网速
        // ==================================================================

        private void SampleSpeed()
        {
            try
            {
                long rx = 0, tx = 0;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    // 只统计拨号接口 —— 否则局域网共享、Wi-Fi 的流量也会被算进来，
                    // 用户会看到"明明没上网，速率却在跳"。
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ppp) continue;
                    try
                    {
                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        rx += st.BytesReceived;
                        tx += st.BytesSent;
                    }
                    catch { }
                }

                DateTime now = DateTime.Now;
                if (_lastSampleAt != DateTime.MinValue)
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
                string ip = "", gw = "", dns = "";

                // 优先展示拨号接口；没有拨号接口时退回其他网卡。
                // 否则同时插着网线又连着 Wi-Fi 时，卡片上显示的可能是 Wi-Fi 的地址。
                var ordered = new List<NetworkInterface>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) ordered.Insert(0, ni);
                    else ordered.Add(ni);
                }

                foreach (NetworkInterface ni in ordered)
                {
                    IPInterfaceProperties props = ni.GetIPProperties();

                    if (ip.Length == 0)
                    {
                        foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    if (gw.Length == 0)
                    {
                        foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                        {
                            if (g.Address != null
                                && g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && g.Address.ToString() != "0.0.0.0")
                            {
                                gw = g.Address.ToString();
                                break;
                            }
                        }
                    }
                    if (dns.Length == 0)
                    {
                        foreach (System.Net.IPAddress d in props.DnsAddresses)
                        {
                            if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                dns = d.ToString();
                                break;
                            }
                        }
                    }
                }

                if (valIp != null) valIp.Text = ip.Length > 0 ? ip : "—";
                if (valGateway != null) valGateway.Text = gw.Length > 0 ? gw : "—";
                if (valDns != null) valDns.Text = dns.Length > 0 ? dns : "—";
            }
            catch { }
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
            StartDial(_state == ConnState.Connected);
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DoDisconnect(true);
        }

        private void StartDial(bool reconnectFirst)
        {
            if (_current == null || string.IsNullOrEmpty(_current.Name))
            {
                MessageBox.Show("请先添加一个账号。\n\n点击右侧「管理账号」，填写连接名称、宽带账号与密码。",
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

                    Dispatcher.Invoke(delegate()
                    {
                        if (res.Success)
                        {
                            _connectedAt = DateTime.Now;
                            _autoReconnectArmed = true;
                            _autoReconnectAttempts = 0;   // 连上了，重试计数归零
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
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("拨号线程异常", ex);
                    Dispatcher.Invoke(delegate()
                    {
                        ApplyState(ConnState.Error, "连接异常", ex.Message);
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoDisconnect(bool userInitiated)
        {
            if (_current == null) return;

            ApplyState(ConnState.Disconnecting, "正在断开…", "正在结束拨号连接");
            string entryName = _current.Name;

            Thread t = new Thread(delegate()
            {
                string msg;
                bool ok = DialEngine.Disconnect(entryName, out msg);
                Log.Info("断开结果: " + ok + " :: " + msg);

                Dispatcher.Invoke(delegate()
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

        internal void RefreshQuickSwitch()
        {
            if (quickSwitchPanel == null) return;
            quickSwitchPanel.Children.Clear();

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
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch { }
            return System.Drawing.SystemIcons.Shield;
        }

        private void InitTray()
        {            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMiStatus = new System.Windows.Forms.ToolStripMenuItem("状态：未连接");
            trayMiStatus.Enabled = false;
            trayMenu.Items.Add(trayMiStatus);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("显示主窗口", null, delegate(object s, EventArgs e) { ShowMainWindow(); });
            trayMiDial = new System.Windows.Forms.ToolStripMenuItem("连接上网", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { StartDial(false); }); });
            trayMenu.Items.Add(trayMiDial);
            trayMiDisconnect = new System.Windows.Forms.ToolStripMenuItem("断开连接", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { DoDisconnect(true); }); });
            trayMenu.Items.Add(trayMiDisconnect);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayMenu.Items.Add("网络体检", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { OpenHealthWindow(); }); });
            trayMenu.Items.Add("退出程序", null,
                delegate(object s, EventArgs e) { Dispatcher.Invoke(delegate() { ExitApp(); }); });

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "校园网助手",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate(object s, EventArgs e) { ShowMainWindow(); };
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
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(3000, title, msg, System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            catch { }
        }

        internal void ExitApp()
        {
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
                Log.Info("系统正在关机/注销，立即保存并退出（不缩到托盘）");
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
                return;   // 不设 e.Cancel → 窗口正常关闭 → 程序干净退出
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
