using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CampusNetHelper
{
    /// <summary>
    /// 连接周报窗口：最近 7 天的掉线次数 / 在线时长 / 平均延迟 / 最大延迟，
    /// 底下给一句人话结论。
    ///
    /// 为什么单独开一个窗口而不是在主界面塞一块：
    ///   主界面已经够挤了（状态条 + 参数卡 + 质量卡 + 账号区 + 历史），
    ///   而周报是"偶尔想看一次"的东西，常驻反而占地方。
    /// </summary>
    internal class WeeklyReportWindow : Window
    {
        public WeeklyReportWindow()
        {
            Title = "连接周报";
            Width = 720;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 12;

            // 和体检窗口一样，用系统标题栏（子窗口保持系统样式）
            WindowStyle = WindowStyle.SingleBorderWindow;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 表格
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 结论

            // ---- 标题 ----
            var head = new StackPanel { Margin = new Thickness(18, 16, 18, 10) };
            var t = new TextBlock
            {
                Text = "连接周报（最近 7 天）",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            head.Children.Add(t);
            var sub = new TextBlock
            {
                Text = "数据只存在你自己电脑上，不会上传。",
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Theme.TextMuted)
            };
            head.Children.Add(sub);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ---- 表格 ----
            var lv = new ListView
            {
                Margin = new Thickness(18, 0, 18, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };

            var gv = new GridView();
            gv.Columns.Add(MakeCol("日期", "Date", 110));
            gv.Columns.Add(MakeCol("首次上线", "FirstOnlineText", 90));
            gv.Columns.Add(MakeCol("掉线次数", "DropText", 90));
            gv.Columns.Add(MakeCol("在线时长", "OnlineText", 120));
            gv.Columns.Add(MakeCol("平均延迟", "AvgLatencyText", 100));
            gv.Columns.Add(MakeCol("最大延迟", "MaxLatencyText", 100));
            lv.View = gv;

            // ---- 结论 ----
            var foot = new Border
            {
                Margin = new Thickness(18, 12, 18, 16),
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.Accent),
                BorderThickness = new Thickness(1)
            };
            var conclusion = new TextBlock
            {
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            foot.Child = conclusion;
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            Grid.SetRow(lv, 1);
            root.Children.Add(lv);

            Content = root;

            // ---- 填数据 ----
            try
            {
                List<WeeklyStats.DayStat> days = WeeklyStats.LoadRecent(7);
                var rows = new List<Row>();
                foreach (WeeklyStats.DayStat d in days) rows.Add(new Row(d));
                lv.ItemsSource = rows;

                conclusion.Text = WeeklyStats.BuildConclusion(days);
            }
            catch (Exception ex)
            {
                // 读侧出问题也不能让窗口打不开 —— 那才是真的没法排查
                Log.Warn("生成周报失败: " + ex.Message);
                conclusion.Text = "读取统计数据时出错了：" + ex.Message;
            }
        }

        /// <summary>表格的一行。ListView 的绑定要的是公开属性，所以这里做个薄包装。</summary>
        internal class Row
        {
            private readonly WeeklyStats.DayStat _d;
            public Row(WeeklyStats.DayStat d) { _d = d; }

            public string Date
            {
                get
                {
                    // 显示成 10-02 这种短形式，省得列太宽
                    if (_d.Date.Length >= 10) return _d.Date.Substring(5);
                    return _d.Date;
                }
            }
            public string FirstOnlineText
            {
                get { return _d.FirstOnline.Length > 0 ? _d.FirstOnline : "—"; }
            }
            public string DropText
            {
                get { return _d.DropCount == 0 ? "0" : _d.DropCount + " 次"; }
            }
            public string OnlineText
            {
                get { return _d.OnlineSeconds > 0 ? WeeklyStats.FmtDuration(_d.OnlineSeconds) : "—"; }
            }
            public string AvgLatencyText
            {
                get
                {
                    double a = _d.AvgLatencyMs();
                    return a > 0 ? ((int)Math.Round(a)) + " ms" : "—";
                }
            }
            public string MaxLatencyText
            {
                get { return _d.MaxLatencyMs > 0 ? _d.MaxLatencyMs + " ms" : "—"; }
            }
        }

        private static GridViewColumn MakeCol(string header, string path, double width)
        {
            var c = new GridViewColumn();
            c.Header = header;
            c.Width = width;
            c.DisplayMemberBinding = new System.Windows.Data.Binding(path);
            return c;
        }
    }
}
