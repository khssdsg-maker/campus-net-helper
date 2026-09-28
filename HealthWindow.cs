using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CampusNetHelper
{
    /// <summary>
    /// 网络体检窗口。8 项通用检查，结果以徽章卡片流式呈现。
    /// </summary>
    public class HealthWindow : Window
    {
        private StackPanel resultHost;
        private TextBlock lblProgress;
        private Button btnRun;
        private Button btnExport;
        private readonly string accountName;
        private StringBuilder reportText = new StringBuilder();

        public HealthWindow(string account)
        {
            accountName = account ?? "";

            Title = "网络体检";
            Width = 640;
            Height = 680;
            MinWidth = 560;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(new TextBlock
            {
                Text = "网络体检",
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 12)
            };
            btnRun = MainWindow.MakeButton("开始体检", Theme.Accent, Theme.OnAccent, Theme.Accent, RunHealth);
            btnExport = MainWindow.MakeButton("导出诊断包", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder, ExportDiag);
            btnExport.Margin = new Thickness(8, 0, 0, 0);
            btnExport.IsEnabled = false;

            lblProgress = new TextBlock
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11
            };

            bar.Children.Add(btnRun);
            bar.Children.Add(btnExport);
            bar.Children.Add(lblProgress);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            resultHost = new StackPanel();
            scroll.Content = resultHost;
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            resultHost.Children.Add(new TextBlock
            {
                Text = "点击「开始体检」，依次检查网卡、IP、网关、DNS、外网连通、延迟、拨号状态与系统环境。\n\n"
                     + "体检结果只在本机展示，不会上传任何数据。",
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22
            });

            Content = root;
        }

        private void RunHealth()
        {
            btnRun.IsEnabled = false;
            btnExport.IsEnabled = false;
            resultHost.Children.Clear();
            reportText = new StringBuilder();
            lblProgress.Text = "正在体检…";

            Thread t = new Thread(delegate()
            {
                try
                {
                    HealthReport.RunStreaming(accountName, delegate(int done, int total, HealthCheckStep step)
                    {
                        Dispatcher.Invoke(delegate()
                        {
                            resultHost.Children.Add(MainWindow.MakeHealthCard(step));
                            lblProgress.Text = "已完成 " + done + " / " + total;
                            reportText.AppendLine("[" + (step.StatusBadge ?? "") + "] " + (step.Title ?? ""));
                            reportText.AppendLine("    " + (step.Summary ?? ""));
                            if (step.Details != null)
                            {
                                foreach (string d in step.Details) reportText.AppendLine("    · " + d);
                            }
                            reportText.AppendLine();
                        });
                    });

                    Dispatcher.Invoke(delegate()
                    {
                        lblProgress.Text = "体检完成";
                        btnRun.IsEnabled = true;
                        btnExport.IsEnabled = true;
                        resultHost.Children.Add(new TextBlock
                        {
                            Text = "—— 体检结束 ——",
                            Foreground = new SolidColorBrush(Theme.TextFaint),
                            FontSize = 11,
                            Margin = new Thickness(0, 8, 0, 0)
                        });
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("体检异常", ex);
                    Dispatcher.Invoke(delegate()
                    {
                        lblProgress.Text = "体检出错";
                        btnRun.IsEnabled = true;
                        resultHost.Children.Add(new TextBlock
                        {
                            Text = "体检过程出错: " + ex.Message,
                            Foreground = new SolidColorBrush(Theme.Err),
                            TextWrapping = TextWrapping.Wrap
                        });
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ExportDiag()
        {
            try
            {
                string header = "校园网助手 网络体检报告\r\n"
                    + "账号: " + (string.IsNullOrEmpty(accountName) ? "(未填写)" : accountName) + "\r\n\r\n";
                string path = HealthReport.ExportDiagPackage(header + reportText.ToString());
                MessageBox.Show("诊断包已生成到桌面：\n\n" + path
                    + "\n\n诊断包不含账号密码，可安全外发用于求助。",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error("导出诊断包失败", ex);
                MessageBox.Show("导出失败: " + ex.Message, "导出失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
