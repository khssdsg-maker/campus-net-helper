using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 网络测速窗口。
    ///
    /// 布局：节点选择 → 四个大数字（下载 / 上传 / 延迟 / 抖动）→ 实时波形 → 进度条 → 状态文字。
    ///
    /// 波形是后来加的：光看四个数字只能知道"平均多少 Mbps"，
    /// 看不出中途掉速、抖动、上传是不是被限速。所以引擎每 200ms 单独发瞬时速率过来，
    /// 这里交给 SpeedChart 画成两条曲线（下载青 / 上传紫），测速过程中能实时看到网络动态。
    /// </summary>
    public class SpeedTestWindow : Window
    {
        private readonly SpeedTestEngine engine = new SpeedTestEngine();

        private ComboBox cmbNode;
        private TextBlock lblNodeHint;
        private Button btnRun;

        private TextBlock valDown;
        private TextBlock valUp;
        private TextBlock valPing;
        private TextBlock valJitter;

        private Grid progressGrid;
        private Border progressFill;
        private TextBlock lblPercent;
        private TextBlock lblStatus;

        private SpeedChart chart;
        private int _lastPercent = 0;

        public SpeedTestWindow()
        {
            Title = "网络测速";
            Width = 640;
            Height = 700;      // 比主窗口矮一点，刚好装下"图例 + 波形 + 四行说明"
            MinWidth = 580;
            MinHeight = 560;

            // 1366×768 这类矮屏笔记本上别让窗口顶出屏幕外。
            // 状态区那一行是可压缩的，屏幕矮就只少显示两行说明，不影响测速本身。
            double maxH = SystemParameters.WorkArea.Height - 40;
            if (Height > maxH) Height = maxH;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();

            engine.Progress += OnProgress;
            engine.Finished += OnFinished;
            engine.Sample += OnSample;

            // 主题变了要重画（网格线和刻度数字的颜色取自 Theme）
            EventHandler onTheme = delegate(object s, EventArgs e)
            {
                if (chart != null) chart.InvalidateVisual();
            };
            Theme.Changed += onTheme;

            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (engine.IsRunning) engine.Cancel();
                Theme.Changed -= onTheme;   // 别让静态事件把这个窗口一直拽着不放
            };
        }

        // ==================================================================
        // 界面
        // ==================================================================

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 1 节点
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 2 四个数字
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 3 实时波形
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 4 进度条
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 5 状态
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 6 按钮

            // ---- 标题 ----
            root.Children.Add(new TextBlock
            {
                Text = "网络测速",
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });

            // ---- 节点选择 ----
            var nodeBox = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

            var nodeRow = new StackPanel { Orientation = Orientation.Horizontal };
            nodeRow.Children.Add(new TextBlock
            {
                Text = "测速节点",
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            cmbNode = MainWindow.MakeDarkCombo(300);
            foreach (SpeedTestNode n in SpeedTestEngine.Nodes) cmbNode.Items.Add(n);

            lblNodeHint = MainWindow.MakeHint("");
            lblNodeHint.Margin = new Thickness(0, 6, 0, 0);

            nodeRow.Children.Add(cmbNode);
            nodeBox.Children.Add(nodeRow);
            nodeBox.Children.Add(lblNodeHint);

            cmbNode.SelectedIndex = 0;
            cmbNode.SelectionChanged += delegate(object s, SelectionChangedEventArgs e) { UpdateNodeHint(); };
            UpdateNodeHint();

            Grid.SetRow(nodeBox, 1);
            root.Children.Add(nodeBox);

            // ---- 四个大数字 ----
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 16, 0, 0) };
            grid.Children.Add(MakeMetricCard("下载", "Mbps", out valDown, Theme.Ok));
            grid.Children.Add(MakeMetricCard("上传", "Mbps", out valUp, Theme.Accent));
            grid.Children.Add(MakeMetricCard("延迟", "ms", out valPing, Theme.Warn));
            grid.Children.Add(MakeMetricCard("抖动", "ms", out valJitter, Theme.Idle));

            Grid.SetRow(grid, 2);
            root.Children.Add(grid);

            // ---- 实时波形 ----
            var chartBox = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

            var chartHead = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            chartHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            chartHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chartHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var legend = new StackPanel { Orientation = Orientation.Horizontal };
            legend.Children.Add(MakeLegendItem(SpeedChart.DlColor, "下载 · 左轴"));
            legend.Children.Add(MakeLegendItem(SpeedChart.UlColor, "上传 · 右轴"));
            chartHead.Children.Add(legend);

            var lblWindow = new TextBlock
            {
                Text = "单位 Mbps　·　最近 30 秒",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lblWindow, 2);
            chartHead.Children.Add(lblWindow);

            chartBox.Children.Add(chartHead);

            // 高度是写死的：波形要有稳定的纵向尺度，跟着窗口缩放会让人误判"掉速了"
            chart = new SpeedChart { Height = 170 };

            var chartFrame = new Border
            {
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Child = chart
            };
            chartBox.Children.Add(chartFrame);

            Grid.SetRow(chartBox, 3);
            root.Children.Add(chartBox);

            // ---- 进度条 ----
            var progBox = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            progBox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progBox.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            progressGrid = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
            progressGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Theme.FieldBg),
                CornerRadius = new CornerRadius(3)
            });

            progressFill = new Border
            {
                Background = new SolidColorBrush(Theme.Accent),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            progressGrid.Children.Add(progressFill);
            progressGrid.SizeChanged += delegate(object s, SizeChangedEventArgs e) { ApplyPercent(_lastPercent); };
            progBox.Children.Add(progressGrid);

            lblPercent = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 34,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(lblPercent, 1);
            progBox.Children.Add(lblPercent);

            Grid.SetRow(progBox, 4);
            root.Children.Add(progBox);

            // ---- 状态 ----
            var statusBox = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

            lblStatus = new TextBlock
            {
                Text = "选好节点后点「开始测速」。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            statusBox.Children.Add(lblStatus);

            var tip = MainWindow.MakeHint(
                "测速会消耗较多流量：下载约 8 秒、上传约 10 秒，千兆链路下合计可能到几百 MB。\n"
                + "节点不可用时会自动改用国内 CDN 源，结果里会标出实际用的哪一家。\n"
                + "曲线是每 0.2 秒的瞬时速率，四个大数字是整段平均值 —— 两者对不上是正常的。\n"
                + "上传段末尾如果有一段贴着底部的平线，是在等服务器应答，不是掉速。");
            tip.Margin = new Thickness(0, 8, 0, 0);
            statusBox.Children.Add(tip);

            Grid.SetRow(statusBox, 5);
            root.Children.Add(statusBox);

            // ---- 按钮 ----
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 0)
            };
            btnRun = MainWindow.MakePrimaryButton("开始测速", OnRunClick);
            btnRun.MinWidth = 110;

            var btnClose = MainWindow.MakeGhostButton("关闭", delegate() { Close(); });
            btnClose.Margin = new Thickness(8, 0, 0, 0);

            btns.Children.Add(btnRun);
            btns.Children.Add(btnClose);

            Grid.SetRow(btns, 6);
            root.Children.Add(btns);

            Content = root;
        }

        private Border MakeMetricCard(string label, string unit, out TextBlock valueBlock, Color accent)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(4, 0, 4, 0)
            };

            var stack = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Width = 3,
                Height = 11,
                Background = new SolidColorBrush(accent),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(head);

            valueBlock = new TextBlock
            {
                Text = "—",
                FontSize = 26,
                FontWeight = FontWeights.Medium,
                FontFamily = new FontFamily(MainWindow.FontMono),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(valueBlock);

            stack.Children.Add(new TextBlock
            {
                Text = unit,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                Margin = new Thickness(0, 2, 0, 0)
            });

            card.Child = stack;
            return card;
        }

        private static StackPanel MakeLegendItem(Color color, string text)
        {
            var box = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 14, 0)
            };

            box.Children.Add(new Border
            {
                Width = 9,
                Height = 9,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            });
            box.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            return box;
        }

        // ==================================================================
        // 交互
        // ==================================================================

        private void UpdateNodeHint()
        {
            if (lblNodeHint == null) return;
            SpeedTestNode n = cmbNode == null ? null : (cmbNode.SelectedItem as SpeedTestNode);
            lblNodeHint.Text = n == null ? "" : n.Hint;
        }

        private void OnRunClick()
        {
            // 测速中再点一次 = 停止
            if (engine.IsRunning)
            {
                engine.Cancel();
                btnRun.IsEnabled = false;
                btnRun.Content = "正在停止…";
                return;
            }

            SpeedTestNode node = cmbNode.SelectedItem as SpeedTestNode;
            if (node == null) node = SpeedTestEngine.Nodes[0];

            valDown.Text = "—";
            valUp.Text = "—";
            valPing.Text = "—";
            valJitter.Text = "—";

            // 重新开始就得擦掉上一轮的曲线，不然第二轮的波形会叠在第一轮上面
            if (chart != null) chart.Clear();

            ApplyPercent(0);
            lblPercent.Text = "0%";
            lblStatus.Text = "准备开始…";

            btnRun.Content = "停止测速";
            engine.Start(node);
        }

        private void ApplyPercent(int pct)
        {
            _lastPercent = pct < 0 ? 0 : (pct > 100 ? 100 : pct);

            if (progressFill == null || progressGrid == null) return;
            double w = progressGrid.ActualWidth;
            if (w < 1) return;

            progressFill.Width = w * _lastPercent / 100.0;
        }

        // ==================================================================
        // 引擎回调（后台线程 → 切回 UI 线程）
        // ==================================================================

        private void OnProgress(SpeedTestProgress p)
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate() { ApplyProgress(p); }));
            }
            catch { }
        }

        private void OnFinished(SpeedTestResult r)
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate() { ApplyResult(r); }));
            }
            catch { }
        }

        /// <summary>
        /// 引擎采样线程回调（5 次/秒）。必须切回 UI 线程才能碰控件。
        /// 用 BeginInvoke 而不是 Invoke：测速线程不该被界面重绘拖慢，
        /// 万一界面卡一下，最坏是波形漏画一两个点，无所谓。
        /// </summary>
        private void OnSample(SpeedSample s)
        {
            if (chart == null) return;

            bool isUpload = s.Phase == SpeedPhase.Upload;
            double mbps = s.Mbps;

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate() { chart.AddSample(isUpload, mbps); }));
            }
            catch { }
        }

        private void ApplyProgress(SpeedTestProgress p)
        {
            ApplyPercent(p.Percent);
            lblPercent.Text = p.Percent + "%";

            if (!string.IsNullOrEmpty(p.Message)) lblStatus.Text = p.Message;

            SetValue(valPing, p.PingMs);
            SetValue(valJitter, p.JitterMs);
            SetValue(valDown, p.DownloadMbps);
            SetValue(valUp, p.UploadMbps);
        }

        private void ApplyResult(SpeedTestResult r)
        {
            btnRun.IsEnabled = true;
            btnRun.Content = "开始测速";

            if (r.Canceled)
            {
                lblStatus.Text = "已停止测速。";
                return;
            }

            if (r.DownloadMbps < 0 && r.UploadMbps < 0)
            {
                lblStatus.Text = string.IsNullOrEmpty(r.Error)
                    ? "测速失败，请稍后重试。" : r.Error;
                return;
            }

            SetValue(valPing, r.PingMs);
            SetValue(valJitter, r.JitterMs);
            SetValue(valDown, r.DownloadMbps);
            SetValue(valUp, r.UploadMbps);

            ApplyPercent(100);
            lblPercent.Text = "100%";

            // 因为会自动降级换源，必须如实告诉用户实际测的是哪一家
            string src = "";
            if (r.DownloadMbps >= 0 && !string.IsNullOrEmpty(r.DownloadSource))
            {
                src += "下载源：" + r.DownloadSource;
            }
            if (r.UploadMbps >= 0 && !string.IsNullOrEmpty(r.UploadSource))
            {
                if (src.Length > 0) src += "　·　";
                src += "上传源：" + r.UploadSource;
            }
            if (!string.IsNullOrEmpty(r.EgressIp))
            {
                if (src.Length > 0) src += "\n";
                src += "出口 IP：" + r.EgressIp;
            }

            lblStatus.Text = "测速完成。" + (src.Length > 0 ? "\n" + src : "");

            // 如果测出来的延迟比节点探测延迟高一截，多半是流量被代理/加速器接管了
            if (r.PingMs > 0 && r.PingMs > 150)
            {
                lblStatus.Text += "\n注意：延迟偏高，可能走了代理或加速器 —— 那样测出的不是真实带宽。";
            }
        }

        private static void SetValue(TextBlock t, double v)
        {
            if (t == null) return;
            t.Text = v < 0 ? "—" : Fmt(v);
        }

        private static string Fmt(double v)
        {
            if (v >= 100) return v.ToString("0");
            if (v >= 10) return v.ToString("0.0");
            return v.ToString("0.00");
        }
    }
}
