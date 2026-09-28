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
        private TextBox txtInterval;
        private CheckBox chkKeepAlive;
        private TextBox txtKeepAlive;
        private TextBlock lblKeepAliveHint;
        private ComboBox cmbTheme;
        private TextBlock lblThemeHint;
        private Button btnAutostart;
        private TextBlock lblAutostart;

        public SettingsWindow(MainWindow ownerWindow)
        {
            owner = ownerWindow;

            Title = "设置";
            Width = 540;
            Height = 640;
            MinWidth = 480;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();
            LoadFromOwner();
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

            var btnSave = MainWindow.MakeButton("保存设置",
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
            chkAutoReconnect.IsChecked = owner.AutoReconnectEnabled();
            chkSilent.IsChecked = owner.SilentEnabled();
            chkCloseToTray.IsChecked = owner.CloseToTrayEnabled();
            txtInterval.Text = owner.ReconnectInterval().ToString();
            chkKeepAlive.IsChecked = owner.KeepAliveEnabled();
            txtKeepAlive.Text = owner.KeepAliveIntervalMinutes().ToString();
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

            owner.ApplySettingsFromWindow(
                chkAutoReconnect.IsChecked == true,
                chkSilent.IsChecked == true,
                chkCloseToTray.IsChecked == true,
                iv,
                chkKeepAlive.IsChecked == true,
                ka);

            MessageBox.Show("设置已保存。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
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
