using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CampusNetHelper
{
    /// <summary>
    /// 设置窗口：自动化、开机自启、关于。
    /// </summary>
    public class SettingsWindow : Window
    {
        private readonly MainWindow owner;
        private CheckBox chkAutoReconnect;
        private CheckBox chkSilent;
        private CheckBox chkCloseToTray;
        private CheckBox chkNightQuiet;
        private TextBox txtQuietStart;
        private TextBox txtQuietEnd;
        private TextBox txtInterval;
        private CheckBox chkKeepAlive;
        private TextBox txtKeepAlive;
        private TextBlock lblKeepAliveHint;
        private ComboBox cmbTheme;
        private TextBlock lblThemeHint;
        private Button btnAutostart;
        private TextBlock lblAutostart;

        // 认证方式
        private RadioButton rbDial;
        private RadioButton rbPortal;
        private TextBlock lblAuthHint;
        private StackPanel panelPortalOpts;
        private RadioButton rbPortalAuto;
        private RadioButton rbPortalKeep;

        /// <summary>
        /// 装载配置期间置 true —— 这期间控件的赋值会触发变更事件，
        /// 不挡住的话会在窗口刚打开时反写一遍配置（还可能弹校验失败的框）。
        /// </summary>
        private bool _loading = false;

        /// <summary>自动保存的节流定时器：连点几下只写一次盘。</summary>
        private System.Windows.Threading.DispatcherTimer _autoSaveTimer;

        /// <summary>顶部那行"已自动保存 ✓"提示。</summary>
        private TextBlock lblAutoSaveHint;

        public SettingsWindow(MainWindow ownerWindow)
        {
            owner = ownerWindow;

            Title = "设置";
            // ⚠️ 这里的宽度和 MinWidth 是"文字不被挤断"的下限：
            //    认证方式那两条说明是最长的一行，540 是量着它定的。
            //    MinWidth 也照着给，别让用户把窗口拉窄到文字折成一条一条。
            Width = 560;
            Height = 640;
            MinWidth = 540;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();
            LoadFromOwner();
            HookAutoSave();          // 改动即存 —— 不用再滚到底找「保存」
            RefreshAutostartState();
        }

        private void BuildUi()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            scroll.Content = stack;

            // ---------- 顶部：自动保存提示条 ----------
            lblAutoSaveHint = new TextBlock
            {
                Text = "本页设置会自动保存，改完即可关闭。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.Accent),
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(lblAutoSaveHint);

            // ---------- 认证方式（最上面，最关键的一个选择） ----------
            stack.Children.Add(SectionTitle("认证方式"));

            var authCard = new Border
            {
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.Accent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 14),
                Margin = new Thickness(0, 10, 0, 0)
            };
            var authPanel = new StackPanel();
            authCard.Child = authPanel;
            stack.Children.Add(authCard);

            rbDial = new RadioButton
            {
                Content = "系统拨号（宽带连接）",
                GroupName = "AuthMode",
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 0)
            };
            rbDial.Checked += delegate(object s, RoutedEventArgs e) { OnAuthModeChanged(); };
            authPanel.Children.Add(rbDial);
            authPanel.Children.Add(HintLine("上网账号密码保存在本机，开机可以静默自动连接，掉线自动重连。"));

            rbPortal = new RadioButton
            {
                Content = "网页认证（打开认证页面登录）",
                GroupName = "AuthMode",
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 0)
            };
            rbPortal.Checked += delegate(object s, RoutedEventArgs e) { OnAuthModeChanged(); };
            authPanel.Children.Add(rbPortal);
            authPanel.Children.Add(HintLine("登录网页里要填图形验证码，程序没法替你自动认，"
                + "所以开机后会把认证窗口直接弹出来，账号密码帮你填好，你填一下验证码就行。"));

            // 网页认证模式下的开机行为（只在选了网页认证时显示）
            panelPortalOpts = new StackPanel { Margin = new Thickness(20, 12, 0, 0) };
            panelPortalOpts.Children.Add(new TextBlock
            {
                Text = "开机弹出认证窗口后：",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(0, 0, 0, 6)
            });

            rbPortalAuto = new RadioButton
            {
                Content = "登录完成后自动关闭窗口",
                GroupName = "PortalStartup",
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12
            };
            panelPortalOpts.Children.Add(rbPortalAuto);

            rbPortalKeep = new RadioButton
            {
                Content = "窗口保持打开，方便反复使用",
                GroupName = "PortalStartup",
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0)
            };
            panelPortalOpts.Children.Add(rbPortalKeep);
            authPanel.Children.Add(panelPortalOpts);

            lblAuthHint = HintLine("");
            lblAuthHint.Margin = new Thickness(0, 10, 0, 0);
            authPanel.Children.Add(lblAuthHint);

            stack.Children.Add(Divider());

            // ---------- 自动化 ----------
            stack.Children.Add(SectionTitle("自动化"));

            chkAutoReconnect = MakeCheck("断线后自动重连", true);
            stack.Children.Add(chkAutoReconnect);

            var rowIv = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 8, 0, 0)
            };
            rowIv.Children.Add(new TextBlock
            {
                Text = "检测间隔（秒）",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11
            });
            txtInterval = new TextBox
            {
                Width = 70,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
            rowIv.Children.Add(txtInterval);
            stack.Children.Add(rowIv);

            // ---------- 心跳保活 ----------
            chkKeepAlive = MakeCheck("保持连接活跃（防学校空闲下线）", true);
            stack.Children.Add(chkKeepAlive);

            var rowKa = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 8, 0, 0)
            };
            rowKa.Children.Add(new TextBlock
            {
                Text = "心跳间隔（分钟）",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11
            });
            txtKeepAlive = new TextBox
            {
                Width = 70,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
            rowKa.Children.Add(txtKeepAlive);
            stack.Children.Add(rowKa);

            lblKeepAliveHint = new TextBlock
            {
                Text = "很多学校会在「空闲 N 分钟」或「在线满 N 小时」时把连接踢掉。"
                     + "开启后，程序在联网期间每隔一段时间发一个极小的请求，"
                     + "让接入设备认为这条链路一直是活的（请求响应体为空，流量可以忽略）。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(24, 6, 0, 0)
            };
            stack.Children.Add(lblKeepAliveHint);

            chkSilent = MakeCheck("开机自动启动（登录后静默运行）", false);
            stack.Children.Add(chkSilent);

            chkCloseToTray = MakeCheck("关闭窗口时最小化到托盘", true);
            stack.Children.Add(chkCloseToTray);

            // ---------- 夜间免打扰 ----------
            chkNightQuiet = MakeCheck("夜间免打扰（时段内不检测、不重连、不弹提示）", false);
            stack.Children.Add(chkNightQuiet);

            var rowQuiet = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 8, 0, 0)
            };
            rowQuiet.Children.Add(new TextBlock
            {
                Text = "免打扰时段",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11
            });
            txtQuietStart = MakeTimeBox("23:30");
            rowQuiet.Children.Add(txtQuietStart);
            rowQuiet.Children.Add(new TextBlock
            {
                Text = "到",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11,
                Margin = new Thickness(8, 0, 8, 0)
            });
            txtQuietEnd = MakeTimeBox("07:00");
            rowQuiet.Children.Add(txtQuietEnd);
            rowQuiet.Children.Add(new TextBlock
            {
                Text = "（可跨零点，比如 23:30 → 07:00）",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            stack.Children.Add(rowQuiet);

            stack.Children.Add(new TextBlock
            {
                Text = "这段时间里程序完全不打扰：不检测网络、不重试拨号、不弹任何提示。"
                     + "宿舍夜里会断电断网，开着它最省心 —— 通电通网后（或时段结束时）会自动连上。"
                     + "已经连着的网络不会因此被断开。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(24, 6, 0, 0)
            });

            stack.Children.Add(Divider());

            // ---------- 外观 ----------
            stack.Children.Add(SectionTitle("外观"));

            var themeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            themeRow.Children.Add(new TextBlock
            {
                Text = "主题",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 12
            });

            cmbTheme = MainWindow.MakeDarkCombo(170);
            cmbTheme.Margin = new Thickness(12, 0, 0, 0);
            cmbTheme.Items.Add("跟随系统");
            cmbTheme.Items.Add("固定浅色");
            cmbTheme.Items.Add("固定深色");
            cmbTheme.SelectedIndex = ThemeIndex(Theme.Preference);
            cmbTheme.SelectionChanged += CmbTheme_SelectionChanged;
            themeRow.Children.Add(cmbTheme);
            stack.Children.Add(themeRow);

            lblThemeHint = new TextBlock
            {
                Text = "选「跟随系统」时，改 Windows 的「设置 → 个性化 → 颜色」本程序会自动跟着变；"
                     + "选固定则始终使用所选外观。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 8, 0, 0)
            };
            stack.Children.Add(lblThemeHint);

            stack.Children.Add(Divider());

            // ---------- 开机自启（提权） ----------
            stack.Children.Add(SectionTitle("开机自启（管理员权限）"));

            lblAutostart = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Margin = new Thickness(0, 10, 0, 0)
            };
            stack.Children.Add(lblAutostart);

            var hint = new TextBlock
            {
                Text = "安装后会创建一个「登录时以最高权限运行」的计划任务，只在安装时需要确认一次权限，"
                     + "之后每次开机都会静默启动，不再弹窗。\n\n"
                     + "如果只是普通上网，不需要装这一项。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 8, 0, 0)
            };
            stack.Children.Add(hint);

            btnAutostart = MainWindow.MakeButton("以管理员身份安装自启",
                Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder, ToggleAutostart);
            btnAutostart.HorizontalAlignment = HorizontalAlignment.Left;
            btnAutostart.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(btnAutostart);

            stack.Children.Add(Divider());

            // ---------- 关于 ----------
            stack.Children.Add(SectionTitle("关于"));

            var about = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 10, 0, 0)
            };
            about.Text = "校园网助手 v" + MainWindow.VersionText + "\n"
                + "一个轻量的校园网连接与体检小工具。\n\n"
                + "· 拨号使用 Windows 系统自带的宽带连接功能\n"
                + "· 账号密码只保存在本机，不会上传\n"
                + "· 连接写入系统电话簿，与「网络和共享中心」一致\n"
                + "· 界面主题：" + Theme.PrefLabel(Theme.Preference)
                + "（当前 " + Theme.Name(Theme.Current) + "）\n\n"
                + "配置文件目录：\n" + ConfigStore.AppDataDir;
            stack.Children.Add(about);

            var btnLog = MainWindow.MakeButton("打开配置与日志目录",
                Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder, OpenLogDir);
            btnLog.HorizontalAlignment = HorizontalAlignment.Left;
            btnLog.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(btnLog);

            var btnSave = MainWindow.MakeButton("保存并校验设置（本页已自动保存）",
                Theme.Accent, Theme.OnAccent, Theme.Accent, SaveToOwner);
            btnSave.HorizontalAlignment = HorizontalAlignment.Left;
            btnSave.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(btnSave);

            Content = scroll;
        }

        private static TextBlock SectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
        }

        /// <summary>缩进的小字说明（挂在某个选项下面）。</summary>
        private static TextBlock HintLine(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                Margin = new Thickness(20, 4, 0, 0)
            };
        }

        /// <summary>切换认证方式时：显隐"网页认证专属选项"、更新提示文案。</summary>
        private void OnAuthModeChanged()
        {
            bool portal = rbPortal != null && rbPortal.IsChecked == true;

            if (panelPortalOpts != null)
            {
                panelPortalOpts.Visibility = portal ? Visibility.Visible : Visibility.Collapsed;
            }

            if (lblAuthHint != null)
            {
                lblAuthHint.Text = portal
                    ? "选它之后：开机自启不再静默拨号，而是直接弹出网页认证窗口（夜间免打扰时段内不弹）。"
                    : "选它之后：开机自启在后台静默拨号，保持原来的行为，不会弹窗打扰。";
            }
        }

        // ==================================================================
        // 自动保存
        //
        // 为什么改成自动保存：原来只有窗口最底部一个「保存设置」按钮，
        // 设置项一多就得往下滚很久才找得到，很容易改完以为生效了其实没保存。
        // 现在改成"改哪儿存哪儿"，底部那个按钮保留，作为"我就是要保存一下"的兜底。
        // ==================================================================

        /// <summary>给所有会改配置的控件挂上变更事件。</summary>
        private void HookAutoSave()
        {
            _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _autoSaveTimer.Tick += delegate(object s, EventArgs e)
            {
                _autoSaveTimer.Stop();
                AutoSaveNow();
            };

            if (chkAutoReconnect != null)
                chkAutoReconnect.Checked += delegate { RequestAutoSave(); };
            if (chkAutoReconnect != null)
                chkAutoReconnect.Unchecked += delegate { RequestAutoSave(); };
            if (chkKeepAlive != null)
                chkKeepAlive.Checked += delegate { RequestAutoSave(); };
            if (chkKeepAlive != null)
                chkKeepAlive.Unchecked += delegate { RequestAutoSave(); };
            if (chkCloseToTray != null)
                chkCloseToTray.Checked += delegate { RequestAutoSave(); };
            if (chkCloseToTray != null)
                chkCloseToTray.Unchecked += delegate { RequestAutoSave(); };

            // 夜间免打扰：开关和时段都要存
            if (chkNightQuiet != null)
                chkNightQuiet.Checked += delegate { RequestAutoSave(); };
            if (chkNightQuiet != null)
                chkNightQuiet.Unchecked += delegate { RequestAutoSave(); };
            if (txtQuietStart != null)
                txtQuietStart.LostFocus += delegate { RequestAutoSave(); };
            if (txtQuietEnd != null)
                txtQuietEnd.LostFocus += delegate { RequestAutoSave(); };

            // 数字/文本输入框：失焦时才触发，避免边打字边存
            if (txtInterval != null)
                txtInterval.LostFocus += delegate { RequestAutoSave(); };
            if (txtKeepAlive != null)
                txtKeepAlive.LostFocus += delegate { RequestAutoSave(); };

            // 认证方式（RadioButton 的 Checked 事件在 BuildUi 里已挂 OnAuthModeChanged，
            // 这里再挂一个保存）
            if (rbDial != null)
                rbDial.Checked += delegate { RequestAutoSave(); };
            if (rbPortal != null)
                rbPortal.Checked += delegate { RequestAutoSave(); };
            if (rbPortalAuto != null)
                rbPortalAuto.Checked += delegate { RequestAutoSave(); };
            if (rbPortalKeep != null)
                rbPortalKeep.Checked += delegate { RequestAutoSave(); };
        }

        /// <summary>请求一次自动保存（600ms 内的多次请求合并成一次）。</summary>
        private void RequestAutoSave()
        {
            if (_loading) return;            // 装载阶段的赋值不触发保存
            if (_autoSaveTimer == null) return;
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        /// <summary>
        /// 真正执行自动保存。
        ///
        /// ⚠️ 与手动保存的区别：**不弹任何对话框**，也不因为格式不合法而回退输入。
        ///    用户还在输入途中（比如时间只打了一半），这时候弹框或改他的字都很讨厌。
        ///    格式不合法就**跳过这次保存**，等他填完整再说 —— 界面上有底部按钮
        ///    负责"认真保存并校验"。
        /// </summary>
        private void AutoSaveNow()
        {
            if (_loading) return;

            int iv;
            if (!int.TryParse((txtInterval.Text ?? "").Trim(), out iv) || iv < 5 || iv > 600) return;

            int ka;
            if (!int.TryParse((txtKeepAlive.Text ?? "").Trim(), out ka) || ka < 1 || ka > 60) return;

            string qs = (txtQuietStart.Text ?? "").Trim();
            string qe = (txtQuietEnd.Text ?? "").Trim();
            if (chkNightQuiet.IsChecked == true)
            {
                if (!MainWindow.IsValidHm(qs) || !MainWindow.IsValidHm(qe)) return;
                if (qs == qe) return;
            }

            try
            {
                owner.ApplySettingsFromWindowSilent(
                    chkAutoReconnect.IsChecked == true,
                    chkSilent.IsChecked == true,
                    chkCloseToTray.IsChecked == true,
                    iv,
                    chkKeepAlive.IsChecked == true,
                    ka,
                    chkNightQuiet.IsChecked == true,
                    qs,
                    qe,
                    (rbPortal != null && rbPortal.IsChecked == true) ? "portal" : "dial",
                    (rbPortalKeep != null && rbPortalKeep.IsChecked == true) ? "keep" : "auto");

                // 静默保存不改勾选框（避免打断用户），但开机自启的实际状态要跟一下
                if (chkSilent != null) chkSilent.IsChecked = owner.SilentEnabled();

                SetSaveHint("已自动保存 ✓");
            }
            catch (Exception ex)
            {
                Log.Warn("自动保存设置失败: " + ex.Message);
            }
        }

        private void SetSaveHint(string text)
        {
            if (lblAutoSaveHint == null) return;
            lblAutoSaveHint.Text = text;
        }

        private static CheckBox MakeCheck(string text, bool initial)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = initial,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12
            };
        }

        private static Border Divider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Theme.Divider),
                Margin = new Thickness(0, 18, 0, 18)
            };
        }

        private void LoadFromOwner()
        {
            _loading = true;
            try
            {
                chkAutoReconnect.IsChecked = owner.AutoReconnectEnabled();
                chkSilent.IsChecked = owner.SilentEnabled();
                chkCloseToTray.IsChecked = owner.CloseToTrayEnabled();
                txtInterval.Text = owner.ReconnectInterval().ToString();
                chkKeepAlive.IsChecked = owner.KeepAliveEnabled();
                txtKeepAlive.Text = owner.KeepAliveIntervalMinutes().ToString();

                chkNightQuiet.IsChecked = owner.NightQuietEnabled();
                txtQuietStart.Text = owner.NightQuietStart();
                txtQuietEnd.Text = owner.NightQuietEnd();

                // 认证方式
                bool portal = owner.IsPortalMode();
                rbDial.IsChecked = !portal;
                rbPortal.IsChecked = portal;
                bool keepOpen = owner.PortalStartupBehavior() == "keep";
                rbPortalKeep.IsChecked = keepOpen;
                rbPortalAuto.IsChecked = !keepOpen;
                OnAuthModeChanged();
            }
            finally { _loading = false; }
        }

        private void SaveToOwner()
        {
            int iv;
            if (!int.TryParse((txtInterval.Text ?? "").Trim(), out iv) || iv < 5 || iv > 600)
            {
                MessageBox.Show("检测间隔请填写 5 到 600 之间的整数（秒）。", "设置",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtInterval.Text = "30";
                return;
            }

            int ka;
            if (!int.TryParse((txtKeepAlive.Text ?? "").Trim(), out ka) || ka < 1 || ka > 60)
            {
                MessageBox.Show("心跳间隔请填写 1 到 60 之间的整数（分钟）。", "设置",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                txtKeepAlive.Text = "3";
                return;
            }

            // 免打扰时段：只在勾选时校验格式（没勾就不用管填了什么）
            string qs = (txtQuietStart.Text ?? "").Trim();
            string qe = (txtQuietEnd.Text ?? "").Trim();
            if (chkNightQuiet.IsChecked == true)
            {
                if (!MainWindow.IsValidHm(qs) || !MainWindow.IsValidHm(qe))
                {
                    MessageBox.Show("免打扰时段请按 24 小时制填写，形如 23:30。",
                        "设置", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (qs == qe)
                {
                    MessageBox.Show("免打扰的开始和结束时间不能一样。",
                        "设置", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            string note = owner.ApplySettingsFromWindow(
                chkAutoReconnect.IsChecked == true,
                chkSilent.IsChecked == true,
                chkCloseToTray.IsChecked == true,
                iv,
                chkKeepAlive.IsChecked == true,
                ka,
                chkNightQuiet.IsChecked == true,
                qs,
                qe,
                (rbPortal.IsChecked == true) ? "portal" : "dial",
                (rbPortalKeep.IsChecked == true) ? "keep" : "auto");

            // 勾选框以**实际结果**为准：装计划任务时会拒绝创建副本、从副本运行时删不掉，
            // 这两种情况都得让界面如实回退，不能让用户看着是勾上的却其实没生效。
            chkSilent.IsChecked = owner.SilentEnabled();

            MessageBox.Show(
                string.IsNullOrEmpty(note) ? "设置已保存。" : "设置已保存。\n\n" + note,
                "设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>时间输入框（HH:mm）。样式跟其它输入框保持一致。</summary>
        private static TextBox MakeTimeBox(string def)
        {
            return new TextBox
            {
                Width = 62,
                Text = def,
                TextAlignment = TextAlignment.Center,
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
        }

        private void RefreshAutostartState()
        {
            try
            {
                bool task = AutostartHelper.IsTaskInstalled();
                if (task)
                {
                    lblAutostart.Text = "当前状态：已安装计划任务（每次登录静默以管理员权限启动）";
                    btnAutostart.Content = "卸载开机自启";
                }
                else
                {
                    lblAutostart.Text = "当前状态：未安装";
                    btnAutostart.Content = "以管理员身份安装自启";
                }

                // 计划任务与「启动」文件夹副本互斥：安装任务时会移除副本、卸载时会恢复副本，
                // 所以上面那个勾选框必须跟着一起刷新，否则界面会和实际状态对不上。
                if (chkSilent != null) chkSilent.IsChecked = owner.SilentEnabled();
            }
            catch (Exception ex)
            {
                lblAutostart.Text = "状态读取失败: " + ex.Message;
            }
        }

        private void ToggleAutostart()
        {
            bool installed = false;
            try { installed = AutostartHelper.IsTaskInstalled(); } catch { }

            string msg;
            bool ok;
            if (installed)
            {
                ok = AutostartHelper.Uninstall(out msg);
            }
            else
            {
                ok = AutostartHelper.Install(out msg);
            }

            Log.Info("开机自启操作: " + (installed ? "卸载" : "安装") + " ok=" + ok + " :: " + msg);

            if (!ok)
            {
                MessageBox.Show("操作未完成：\n\n" + msg
                    + "\n\n如果提示需要管理员权限，请重新点击并在弹出的系统对话框中点「是」。",
                    "开机自启", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(msg, "开机自启", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            RefreshAutostartState();
        }

        private static int ThemeIndex(ThemePreference p)
        {
            if (p == ThemePreference.Light) return 1;
            if (p == ThemePreference.Dark) return 2;
            return 0;
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = cmbTheme != null ? cmbTheme.SelectedIndex : 0;
            ThemePreference p = ThemePreference.System;
            if (i == 1) p = ThemePreference.Light;
            else if (i == 2) p = ThemePreference.Dark;

            Theme.SetPreference(p);

            if (lblThemeHint != null)
            {
                lblThemeHint.Text = "已切换为「" + Theme.PrefLabel(p) + "」，当前生效："
                    + Theme.Name(Theme.Current) + "。\n本窗口的配色需要重新打开才会更新。";
            }
        }

        private void OpenLogDir()
        {
            try
            {
                string dir = ConfigStore.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败: " + ex.Message, "设置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
