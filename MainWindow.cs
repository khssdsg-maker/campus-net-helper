using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CampusNetHelper
{
    /// <summary>
    /// 主窗口 —— 界面构建（磨砂玻璃主题，跟随系统深浅色）。
    ///
    /// 结构：
    ///   Grid（窗口底色 = Theme.WindowBg）
    ///    ├─ 光斑层（3 个柔和彩色圆，制造"玻璃后面有东西"的观感）
    ///    └─ 玻璃面板（半透明白 + 浅色描边）
    ///        ├─ 顶栏（标题 + 网络体检 / 设置）
    ///        ├─ 状态横条（状态点 + 文案 + 在线时长 + 主按钮 / 断开）
    ///        ├─ 参数卡片 ×4（本机 IP / 默认网关 / DNS / 实时速率）
    ///        ├─ 网络质量卡片（丢包率 / 平均延迟 / 到网关 + 延迟曲线）
    ///        └─ 主体（左：实时网速 + 近期记录；右：账号）
    ///
    /// 业务逻辑在 MainWindow.Logic.cs。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---------- 状态横条 ----------
        internal Border statusStrip;
        internal Ellipse statusDot;
        internal TextBlock lblStatusTitle;
        internal TextBlock lblStatusDetail;
        internal TextBlock lblOnlineTime;
        internal Button btnMainAction;
        internal Button btnDisconnect;

        // ---------- 参数卡片 ----------
        internal TextBlock valIp;
        internal TextBlock valGateway;
        internal TextBlock valDns;
        internal TextBlock valSpeed;

        // ---------- 网速曲线 ----------
        internal Canvas speedCanvas;
        internal Polyline speedFill;
        internal Polyline speedLine;
        internal TextBlock lblSpeedDown;
        internal TextBlock lblSpeedUp;

        // ---------- 网络质量 ----------
        internal Canvas qualityCanvas;
        internal Polyline qualityFill;
        internal Polyline qualityLine;
        internal Border badgeQuality;
        internal TextBlock lblQualityVerdict;
        internal TextBlock valLoss;
        internal TextBlock valRtt;
        internal TextBlock valGwRtt;
        internal TextBlock valLink;
        internal TextBlock lblQualityDetail;
        internal Button btnSpeedTest;

        // ---------- 账号侧栏 ----------
        internal ComboBox cmbAccount;
        internal Button btnManageAccounts;
        internal StackPanel quickSwitchPanel;

        // ---------- 历史 ----------
        internal StackPanel historyPanel;

        // ---------- 顶栏 ----------
        internal Button btnOpenHealth;
        internal Button btnOpenSettings;
        private Border btnMaximize;

        // ---------- 托盘 ----------
        internal System.Windows.Forms.NotifyIcon trayIcon;
        internal System.Windows.Forms.ContextMenuStrip trayMenu;
        internal System.Windows.Forms.ToolStripMenuItem trayMiStatus;
        internal System.Windows.Forms.ToolStripMenuItem trayMiDial;
        internal System.Windows.Forms.ToolStripMenuItem trayMiDisconnect;

        // ---------- 主题相关（需要随主题重绘的元素） ----------
        private Grid _glowLayer;
        private Border _glassRoot;
        private readonly List<Border> _glassCards = new List<Border>();
        private readonly List<TextBlock> _primaryTexts = new List<TextBlock>();
        private readonly List<TextBlock> _mutedTexts = new List<TextBlock>();
        private readonly List<Border> _dividers = new List<Border>();
        private Border _speedCard;
        private Border _historyCard;
        private Border _accountCard;
        private Border _titleBarArea;
        private Border _guitBadge;              // 「官方入口」里的 GUIT 徽标
        private TextBlock _guitBadgeText;

        internal const string FontUi = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";
        internal const string FontMono = "Consolas, Microsoft YaHei UI";
        internal const string VersionText = "1.1.0";

        // ==================================================================
        // 官方入口地址
        //
        // 学校官网 —— 公网可访问，写在这里没有隐私问题。
        //
        // 自助服务 —— 属于校内网地址（只有连着校园网才能打开），是"本校专属信息"，
        // 所以**不硬编码在源码里**，改由 SiteConfig 提供：
        //   · SiteConfig.cs         自己的真实地址，已被 .gitignore 排除
        //   · SiteConfig.sample.cs  提交到仓库的空模板，首次构建时自动生成上面那个
        // 留空时界面上不显示这个入口（不给死链）。
        //
        // 这两个地址都来自学校官方客户端界面本身，是学校公开给学生使用的。
        // 本工具只提供"打开链接"，不调用其中的任何接口。
        // ==================================================================
        internal const string UrlSchool = "https://www.guit.edu.cn";

        // ==================================================================
        // 按钮样式（缓存，避免每次创建都解析 XAML）
        // ==================================================================

        private static Style _btnStyle;

        private static Style BtnStyle
        {
            get
            {
                if (_btnStyle == null)
                {
                    string xaml =
                        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>"
                        + "<Setter Property='Template'><Setter.Value>"
                        + "<ControlTemplate TargetType='Button'>"
                        + "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' "
                        + "BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8'>"
                        + "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' "
                        + "Margin='16,8,16,8'/>"
                        + "</Border>"
                        + "</ControlTemplate>"
                        + "</Setter.Value></Setter>"
                        + "<Style.Triggers>"
                        + "<Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.80'/></Trigger>"
                        + "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.38'/></Trigger>"
                        + "</Style.Triggers>"
                        + "</Style>";
                    _btnStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                }
                return _btnStyle;
            }
        }

        // ==================================================================
        // 控件工厂
        // ==================================================================

        internal static Button MakeButton(string text, Color bg, Color fg, Color border, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                Background = new SolidColorBrush(bg),
                Foreground = new SolidColorBrush(fg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                FontFamily = new FontFamily(FontUi),
                FontWeight = FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand,
                Style = BtnStyle
            };
            if (onClick != null) btn.Click += (s, e) => onClick();
            return btn;
        }

        /// <summary>主按钮（实心主色）。</summary>
        internal static Button MakePrimaryButton(string text, Action onClick)
        {
            return MakeButton(text, Theme.Accent, Theme.OnAccent, Theme.Accent, onClick);
        }

        /// <summary>次要按钮（玻璃描边）。</summary>
        internal static Button MakeGhostButton(string text, Action onClick)
        {
            return MakeButton(text, Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder, onClick);
        }

        /// <summary>禁用态按钮。</summary>
        internal static Button MakeDisabledButton(string text)
        {
            var b = MakeGhostButton(text, null);
            b.IsEnabled = false;
            return b;
        }

        /// <summary>
        /// 下拉框 / 列表框的条目样式（含自定义模板）。
        ///
        /// ⚠️ 两个必须一起做的原因：
        ///   1. 只设 Foreground / Background 没用 —— WPF 默认模板在 IsHighlighted 时
        ///      会把底色刷成系统高亮色，配上我们为深色底准备的浅色文字 = 看不见。
        ///   2. ComboBox 自身也带模板，不换掉的话 Background 属性根本不生效，永远是系统白底。
        ///
        /// typeName 传 "ComboBoxItem" 或 "ListBoxItem"。
        /// </summary>
        internal static Style MakeItemStyle(string typeName)
        {
            string tn = (typeName == "ListBoxItem") ? "ListBoxItem" : "ComboBoxItem";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='" + tn + "'>"
                + "<Setter Property='Foreground' Value='" + Hex(Theme.TextPrimary) + "'/>"
                + "<Setter Property='Background' Value='" + Hex(SolidFieldBg()) + "'/>"
                + "<Setter Property='Padding' Value='10,7'/>"
                + "<Setter Property='HorizontalContentAlignment' Value='Stretch'/>"
                + "<Setter Property='Template'><Setter.Value>"
                + "<ControlTemplate TargetType='" + tn + "'>"
                + "<Border x:Name='Bd' Background='{TemplateBinding Background}' "
                + "Padding='{TemplateBinding Padding}'>"
                + "<ContentPresenter VerticalAlignment='Center'/>"
                + "</Border>"
                + "<ControlTemplate.Triggers>"
                + "<Trigger Property='IsSelected' Value='True'>"
                + "<Setter TargetName='Bd' Property='Background' Value='" + HexA(Theme.Accent, 0x4D) + "'/>"
                + "<Setter Property='Foreground' Value='" + Hex(Theme.TextPrimary) + "'/>"
                + "</Trigger>"
                + "<Trigger Property='IsMouseOver' Value='True'>"
                + "<Setter TargetName='Bd' Property='Background' Value='" + Hex(Theme.Accent) + "'/>"
                + "<Setter Property='Foreground' Value='" + Hex(Theme.OnAccent) + "'/>"
                + "</Trigger>"
                + "</ControlTemplate.Triggers>"
                + "</ControlTemplate>"
                + "</Setter.Value></Setter>"
                + "</Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        /// <summary>
        /// ComboBox 自身的模板。不换掉它，Background / BorderBrush 都不会生效，
        /// 控件在深色主题下会突兀地显示成系统白色。
        /// </summary>
        internal static ControlTemplate MakeComboTemplate()
        {
            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>"
                + "<Grid>"
                + "<ToggleButton x:Name='ToggleButton' Focusable='False' ClickMode='Press' "
                + "Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' "
                + "BorderThickness='{TemplateBinding BorderThickness}' "
                + "IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>"
                + "<ToggleButton.Template>"
                + "<ControlTemplate TargetType='ToggleButton'>"
                + "<Border x:Name='Bd' Background='{TemplateBinding Background}' "
                + "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' "
                + "CornerRadius='6'>"
                + "<Grid>"
                + "<Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='22'/></Grid.ColumnDefinitions>"
                + "<Path Grid.Column='1' HorizontalAlignment='Center' VerticalAlignment='Center' "
                + "Data='M 0 0 L 4 4 L 8 0 Z' Fill='" + Hex(Theme.TextMuted) + "'/>"
                + "</Grid>"
                + "</Border>"
                + "</ControlTemplate>"
                + "</ToggleButton.Template>"
                + "</ToggleButton>"
                + "<ContentPresenter x:Name='Sel' Margin='10,7,28,7' VerticalAlignment='Center' "
                + "IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' "
                + "ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>"
                + "<Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' "
                + "IsOpen='{TemplateBinding IsDropDownOpen}'>"
                + "<Border Background='" + PopupBg() + "' BorderBrush='" + Hex(Theme.GlassBorder) + "' "
                + "BorderThickness='1' CornerRadius='6' Margin='0,2,0,0'>"
                + "<ScrollViewer MaxHeight='260'><ItemsPresenter/></ScrollViewer>"
                + "</Border>"
                + "</Popup>"
                + "</Grid>"
                + "</ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        /// <summary>下拉弹层必须是不透明底色（半透明会透出后面的界面）。</summary>
        private static string PopupBg()
        {
            if (Theme.Current == ThemeMode.Dark) return Hex(Color.FromRgb(0x1A, 0x21, 0x2E));
            return Hex(Colors.White);
        }

        /// <summary>
        /// Color → XAML 颜色串。
        /// ⚠️ 必须保留原 alpha：曾经这里硬编码成 "#FF"，把主题里的半透明底色
        /// （Theme.FieldBg 是 8% 透明的白）变成了纯白，导致深色主题下
        /// 下拉项变成白底配浅色字，完全看不清。
        /// </summary>
        private static string Hex(Color c)
        {
            return "#" + c.A.ToString("X2") + c.R.ToString("X2")
                 + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>
        /// 下拉项的实色底。这里刻意用不透明色：
        /// 半透明底浮在弹层上会变成中间灰，和文字对比不够。
        /// </summary>
        private static Color SolidFieldBg()
        {
            if (Theme.Current == ThemeMode.Dark) return Color.FromRgb(0x22, 0x2B, 0x3B);
            return Colors.White;
        }

        private static string HexA(Color c, byte a)
        {
            return "#" + a.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static string ToHex(Color c)
        {
            return "#FF" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>下拉框统一样式。</summary>
        internal static ComboBox MakeDarkCombo(double width)
        {
            var cb = new ComboBox
            {
                Width = width,
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 12,
                ItemContainerStyle = MakeItemStyle("ComboBoxItem"),
                Template = MakeComboTemplate()
            };
            return cb;
        }

        internal TextBlock MakeTitle(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            _primaryTexts.Add(tb);
            return tb;
        }

        internal TextBlock MakeLabel(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted)
            };
            _mutedTexts.Add(tb);
            return tb;
        }

        internal static TextBlock MakeHint(string text)
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
        // 窗口圆角（Windows 11）
        // ==================================================================

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// 让窗口四角变圆。
        ///
        /// 为什么必须做：去掉系统标题栏后窗口是**直角**的，
        /// 而内部的玻璃面板是圆角的 —— 四个角上就会露出一圈方形底色，
        /// 看起来就像"界面外面套了个正方形的框"。
        /// Windows 11 可以用 DWM 原生把窗口裁成圆角（比 AllowsTransparency 方案好：
        /// 后者会关掉硬件加速，文字渲染变糊）。
        /// </summary>
        private void ApplyRoundedCorners()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int pref = 2;   // DWMWCP_ROUND（1=不圆角 2=圆角 3=小圆角）
                // 33 = DWMWA_WINDOW_CORNER_PREFERENCE，Win11 build 22000+ 才有
                int hr = DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
                if (hr != 0) Log.Warn("窗口圆角设置返回 hr=" + hr + "（可能是旧系统，忽略）");
            }
            catch (Exception ex)
            {
                Log.Warn("设置窗口圆角失败（不影响使用）: " + ex.Message);
            }
        }

        private Border MakeDivider()
        {
            var d = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Theme.Divider),
                Margin = new Thickness(0, 14, 0, 12)
            };
            _dividers.Add(d);
            return d;
        }

        private Border MakeGlassCard()
        {
            var c = new Border
            {
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            _glassCards.Add(c);
            return c;
        }

        // ==================================================================
        // 构建界面
        // ==================================================================

        private void BuildUi()
        {
            Title = "校园网助手";
            Width = 920;
            Height = 810;
            MinWidth = 860;
            MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(FontUi);
            FontSize = 13;

            // 去掉系统标题栏，让顶栏和界面融为一体。
            // 用 WindowChrome 而不是 WindowStyle.None 裸奔：
            // 它会保留窗口缩放、双击最大化、Win+方向键、任务栏缩略图这些原生行为，
            // 只是把"画标题栏"这件事交给我们自己。
            WindowStyle = WindowStyle.None;
            try
            {
                var chrome = new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 0,          // 0 = 不要系统的标题栏拖动区，我们自己实现
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(0),
                    UseAeroCaptionButtons = false
                };
                System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
            }
            catch (Exception ex)
            {
                Log.Warn("WindowChrome 初始化失败，回退到系统标题栏: " + ex.Message);
                WindowStyle = WindowStyle.SingleBorderWindow;
            }

            var root = new Grid();

            // 光斑层
            _glowLayer = new Grid { ClipToBounds = true };
            root.Children.Add(_glowLayer);

            // 玻璃面板
            //
            // ⚠️ 这里**不要**外边距、也不要自己画圆角和边框。
            // 早期版本是 `Margin=12 + CornerRadius=16 + 边框`，结果窗口底色在面板外面
            // 露出一圈方形区域 —— 用户看到的"外面有个框"就是这一圈。
            // 现在让面板直接铺满窗口，圆角交给 Windows 11 的 DWM 处理
            //（ApplyRoundedCorners 里设的 DWMWA_WINDOW_CORNER_PREFERENCE），
            // 视觉上就是"整个窗口 = 界面本身"。
            _glassRoot = new Border
            {
                Background = new SolidColorBrush(Theme.GlassPanel),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(0)
            };

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _titleBarArea = new Border { Padding = new Thickness(20, 16, 20, 10) };
            _titleBarArea.Child = BuildTopBar();
            // 顶栏空白处按住即可拖动窗口（替代被去掉的系统标题栏）
            _titleBarArea.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
            content.Children.Add(_titleBarArea);

            var strip = BuildStatusStrip();
            Grid.SetRow(strip, 1);
            content.Children.Add(strip);

            var cards = BuildParamCards();
            Grid.SetRow(cards, 2);
            content.Children.Add(cards);

            var quality = BuildQualityCard();
            Grid.SetRow(quality, 3);
            content.Children.Add(quality);

            var body = BuildBody();
            Grid.SetRow(body, 4);
            content.Children.Add(body);

            _glassRoot.Child = content;
            root.Children.Add(_glassRoot);

            Content = root;

            RebuildGlow();

            SizeChanged += (s, e) => RebuildGlow();
            Theme.Changed += (s, e) => ApplyTheme();
        }

        // ==================================================================
        // 光斑层
        // ==================================================================

        private void RebuildGlow()
        {
            if (_glowLayer == null) return;
            _glowLayer.Children.Clear();

            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;

            _glowLayer.Children.Add(MakeGlow(Theme.Glow1, w * 0.42, -w * 0.16, -h * 0.22));
            _glowLayer.Children.Add(MakeGlow(Theme.Glow2, w * 0.40, w * 0.72, -h * 0.14));
            _glowLayer.Children.Add(MakeGlow(Theme.Glow3, w * 0.46, w * 0.26, h * 0.62));
        }

        private Ellipse MakeGlow(Color center, double size, double left, double top)
        {
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(center, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, center.R, center.G, center.B), 1));

            var e = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(left, top, 0, 0),
                IsHitTestVisible = false
            };
            return e;
        }

        // ==================================================================
        // 顶栏
        // ==================================================================

        private Grid BuildTopBar()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel();
            var t = new TextBlock
            {
                Text = "校园网助手",
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            _primaryTexts.Add(t);
            title.Children.Add(t);

            var st = new TextBlock
            {
                Text = "校园网连接管理 · 网络体检",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(0, 3, 0, 0)
            };
            _mutedTexts.Add(st);
            title.Children.Add(st);
            grid.Children.Add(title);

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnOpenHealth = MakeGhostButton("网络体检", () => OpenHealthWindow());
            right.Children.Add(btnOpenHealth);
            btnOpenSettings = MakeGhostButton("设置", () => OpenSettingsWindow());
            btnOpenSettings.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(btnOpenSettings);

            // ---- 窗口控制按钮 ----
            // 去掉系统标题栏后要自己画这三个。用 Border 而不是 Button，
            // 因为需要"平时透明、悬停变色（关闭键变红）"这种完全自定义的反馈。
            var divider = new Border
            {
                Width = 1,
                Height = 16,
                Background = new SolidColorBrush(Theme.Divider),
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            right.Children.Add(divider);

            right.Children.Add(MakeCaptionButton("\u2500", Theme.Accent, delegate()
            {
                WindowState = WindowState.Minimized;
            }));

            btnMaximize = MakeCaptionButton("\u25A1", Theme.Accent, delegate() { ToggleMaximize(); });
            right.Children.Add(btnMaximize);

            var btnClose = MakeCaptionButton("\u2715", Theme.Err, delegate() { Close(); });
            right.Children.Add(btnClose);

            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        /// <summary>窗口控制按钮（最小化 / 最大化 / 关闭）。</summary>
        private Border MakeCaptionButton(string glyph, Color hoverColor, Action onClick)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _mutedTexts.Add(text);

            var bd = new Border
            {
                Width = 42,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Child = text,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            bd.MouseEnter += delegate
            {
                bd.Background = new SolidColorBrush(C(hoverColor, 46));
                text.Foreground = new SolidColorBrush(hoverColor);
            };
            bd.MouseLeave += delegate
            {
                bd.Background = Brushes.Transparent;
                text.Foreground = new SolidColorBrush(Theme.TextMuted);
            };
            bd.MouseLeftButtonDown += delegate(object s, System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;   // 别让顶栏的 DragMove 抢走
                onClick();
            };
            return bd;
        }

        /// <summary>最大化 / 还原切换（顺便同步按钮图标）。</summary>
        private void ToggleMaximize()
        {
            WindowState = (WindowState == WindowState.Maximized)
                ? WindowState.Normal
                : WindowState.Maximized;
            UpdateMaximizeGlyph();
        }

        private void UpdateMaximizeGlyph()
        {
            if (btnMaximize == null) return;
            var t = btnMaximize.Child as TextBlock;
            if (t == null) return;
            // □ = 可最大化，❐ = 可还原
            t.Text = (WindowState == WindowState.Maximized) ? "\u2750" : "\u25A1";
        }

        /// <summary>顶栏拖动（等价于系统标题栏的按住拖动）。</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }
            if (WindowState == WindowState.Maximized) return;   // 最大化时拖动会抛异常

            try { DragMove(); }
            catch { }
        }

        // ==================================================================
        // 状态横条
        // ==================================================================

        private Border BuildStatusStrip()
        {
            statusStrip = new Border
            {
                Margin = new Thickness(20, 0, 20, 0),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 14, 18, 14)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dotWrap = new Grid
            {
                Width = 40,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusDot = new Ellipse
            {
                Width = 11,
                Height = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dotWrap.Children.Add(statusDot);
            grid.Children.Add(dotWrap);

            var mid = new StackPanel
            {
                Margin = new Thickness(8, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            lblStatusTitle = new TextBlock
            {
                Text = "尚未连接",
                FontSize = 15,
                FontWeight = FontWeights.Medium
            };
            mid.Children.Add(lblStatusTitle);

            var row2 = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };
            lblStatusDetail = new TextBlock
            {
                Text = "",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center
            };
            row2.Children.Add(lblStatusDetail);

            lblOnlineTime = new TextBlock
            {
                Text = "",
                FontSize = 11,
                FontFamily = new FontFamily(FontMono),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row2.Children.Add(lblOnlineTime);
            mid.Children.Add(row2);
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnMainAction = MakePrimaryButton("立即连接", delegate() { BtnMainAction_Click(null, null); });
            btnMainAction.MinWidth = 108;
            btnDisconnect = MakeGhostButton("断开", delegate() { BtnDisconnect_Click(null, null); });
            btnDisconnect.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(btnMainAction);
            right.Children.Add(btnDisconnect);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            statusStrip.Child = grid;
            return statusStrip;
        }

        // ==================================================================
        // 参数卡片
        // ==================================================================

        private UniformGrid BuildParamCards()
        {
            var grid = new UniformGrid
            {
                Columns = 4,
                Margin = new Thickness(20, 12, 20, 0)
            };

            valIp = new TextBlock();
            valGateway = new TextBlock();
            valDns = new TextBlock();
            valSpeed = new TextBlock();

            grid.Children.Add(MakeParamCard("本机 IP", valIp, Theme.Accent));
            grid.Children.Add(MakeParamCard("默认网关", valGateway, Theme.Accent));
            grid.Children.Add(MakeParamCard("DNS 服务器", valDns, Theme.Accent));
            grid.Children.Add(MakeParamCard("实时速率", valSpeed, Theme.Ok));

            return grid;
        }

        private Border MakeParamCard(string label, TextBlock valueBlock, Color accent)
        {
            var card = MakeGlassCard();
            card.Padding = new Thickness(12, 10, 12, 10);
            card.Margin = new Thickness(4, 0, 4, 0);

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
            var lb = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _mutedTexts.Add(lb);
            head.Children.Add(lb);
            stack.Children.Add(head);

            valueBlock.Text = "—";
            valueBlock.FontSize = 12;
            valueBlock.FontWeight = FontWeights.Medium;
            valueBlock.Foreground = new SolidColorBrush(Theme.TextPrimary);
            valueBlock.Margin = new Thickness(0, 7, 0, 0);
            valueBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            _primaryTexts.Add(valueBlock);
            stack.Children.Add(valueBlock);

            card.Child = stack;
            return card;
        }

        // ==================================================================
        // 主体
        // ==================================================================

        private Grid BuildBody()
        {
            var body = new Grid { Margin = new Thickness(20, 12, 20, 18) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });

            _speedCard = BuildSpeedCard();
            left.Children.Add(_speedCard);

            _historyCard = BuildHistoryCard();
            Grid.SetRow(_historyCard, 2);
            left.Children.Add(_historyCard);
            body.Children.Add(left);

            _accountCard = BuildAccountSideCard();
            Grid.SetColumn(_accountCard, 2);
            body.Children.Add(_accountCard);

            return body;
        }

        private Border BuildSpeedCard()
        {
            var card = MakeGlassCard();
            card.Padding = new Thickness(14, 12, 14, 12);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var t = new TextBlock
            {
                Text = "实时网速",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            };
            _primaryTexts.Add(t);
            head.Children.Add(t);

            lblSpeedDown = new TextBlock
            {
                Text = "下行 0 B/s",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.Ok),
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(FontMono)
            };
            head.Children.Add(lblSpeedDown);

            lblSpeedUp = new TextBlock
            {
                Text = "上行 0 B/s",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.Accent),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily(FontMono)
            };
            head.Children.Add(lblSpeedUp);
            grid.Children.Add(head);

            speedCanvas = new Canvas
            {
                Height = 96,
                Margin = new Thickness(0, 8, 0, 0),
                ClipToBounds = true
            };
            speedCanvas.SizeChanged += (s, e) => RedrawSpeedGraph();

            speedFill = new Polyline
            {
                Stroke = new SolidColorBrush(Theme.BrA(Theme.Ok, 70).Color),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Theme.BrA(Theme.Ok, 26).Color)
            };
            speedCanvas.Children.Add(speedFill);

            speedLine = new Polyline
            {
                Stroke = new SolidColorBrush(Theme.Ok),
                StrokeThickness = 1.8
            };
            speedCanvas.Children.Add(speedLine);

            Grid.SetRow(speedCanvas, 1);
            grid.Children.Add(speedCanvas);

            card.Child = grid;
            return card;
        }

        /// <summary>
        /// 网络质量卡片：左边三个指标（丢包率 / 平均延迟 / 到网关），右边一条延迟曲线。
        /// 曲线颜色跟随质量等级（绿 / 黄 / 红），所以上色统一交给 RefreshQualityUi()，
        /// 这里只负责搭骨架。
        /// </summary>
        private Border BuildQualityCard()
        {
            var card = MakeGlassCard();
            card.Padding = new Thickness(14, 12, 14, 12);
            card.Margin = new Thickness(20, 12, 20, 0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 标题行：标题 + 结论徽章 …… 右侧「网络测速」按钮 ----
            var head = new StackPanel { Orientation = Orientation.Horizontal };

            var t = new TextBlock
            {
                Text = "网络质量",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            };
            _primaryTexts.Add(t);
            head.Children.Add(t);

            lblQualityVerdict = new TextBlock
            {
                Text = "未连接",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeQuality = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = lblQualityVerdict
            };
            head.Children.Add(badgeQuality);

            btnSpeedTest = MakeGhostButton("网络测速", delegate() { BtnSpeedTest_Click(null, null); });
            btnSpeedTest.FontSize = 11;
            btnSpeedTest.Padding = new Thickness(10, 3, 10, 3);
            btnSpeedTest.VerticalAlignment = VerticalAlignment.Center;

            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.Children.Add(head);
            Grid.SetColumn(btnSpeedTest, 1);
            headGrid.Children.Add(btnSpeedTest);

            Grid.SetColumnSpan(headGrid, 3);
            grid.Children.Add(headGrid);

            // ---- 左侧指标列 ----
            var metrics = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            metrics.Children.Add(MakeMetricRow("丢包率", out valLoss));
            metrics.Children.Add(MakeMetricRow("平均延迟", out valRtt));
            metrics.Children.Add(MakeMetricRow("到网关", out valGwRtt));
            metrics.Children.Add(MakeMetricRow("链路速率", out valLink));
            Grid.SetRow(metrics, 1);
            grid.Children.Add(metrics);

            // ---- 右侧延迟曲线 ----
            Color acc = Theme.Accent;

            qualityCanvas = new Canvas
            {
                Height = 74,
                Margin = new Thickness(0, 10, 0, 0),
                ClipToBounds = true
            };
            qualityCanvas.SizeChanged += (s, e) => RedrawQualityGraph();

            qualityFill = new Polyline
            {
                Stroke = new SolidColorBrush(C(acc, 70)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(C(acc, 26))
            };
            qualityCanvas.Children.Add(qualityFill);

            qualityLine = new Polyline
            {
                Stroke = new SolidColorBrush(acc),
                StrokeThickness = 1.8
            };
            qualityCanvas.Children.Add(qualityLine);

            Grid.SetRow(qualityCanvas, 1);
            Grid.SetColumn(qualityCanvas, 2);
            grid.Children.Add(qualityCanvas);

            // ---- 底部解释（说人话，指出问题在哪一段） ----
            lblQualityDetail = new TextBlock
            {
                Text = "连接后自动开始监测丢包与延迟。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(0, 6, 0, 0)
            };
            _mutedTexts.Add(lblQualityDetail);
            Grid.SetRow(lblQualityDetail, 2);
            Grid.SetColumnSpan(lblQualityDetail, 3);
            grid.Children.Add(lblQualityDetail);

            card.Child = grid;
            return card;
        }

        /// <summary>一行「标签 + 数值」，数值块通过 out 交给调用方保存（颜色随状态变化）。</summary>
        private Grid MakeMetricRow(string label, out TextBlock valueBlock)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lb = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center
            };
            _mutedTexts.Add(lb);
            row.Children.Add(lb);

            valueBlock = new TextBlock
            {
                Text = "—",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                FontFamily = new FontFamily(FontMono),
                Foreground = new SolidColorBrush(Theme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(valueBlock);

            return row;
        }

        private Border BuildHistoryCard()
        {
            var card = MakeGlassCard();
            card.Padding = new Thickness(14, 12, 14, 12);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var t = new TextBlock
            {
                Text = "近期连接记录",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            _primaryTexts.Add(t);
            grid.Children.Add(t);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 0)
            };
            historyPanel = new StackPanel();
            scroll.Content = historyPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            card.Child = grid;
            return card;
        }

        private Border BuildAccountSideCard()
        {
            var card = MakeGlassCard();
            card.Padding = new Thickness(14, 12, 14, 12);

            var stack = new StackPanel();
            var t = new TextBlock
            {
                Text = "账号",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            };
            _primaryTexts.Add(t);
            stack.Children.Add(t);

            cmbAccount = new ComboBox
            {
                Margin = new Thickness(0, 10, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12,
                ItemContainerStyle = MakeItemStyle("ComboBoxItem"),
                Template = MakeComboTemplate()
            };
            cmbAccount.SelectionChanged += CmbAccount_SelectionChanged;
            stack.Children.Add(cmbAccount);

            btnManageAccounts = MakeGhostButton("管理账号", delegate() { BtnManageAccounts_Click(null, null); });
            btnManageAccounts.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnManageAccounts.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(btnManageAccounts);

            stack.Children.Add(MakeDivider());

            stack.Children.Add(MakeLabel("快捷切换"));
            quickSwitchPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(quickSwitchPanel);

            stack.Children.Add(MakeDivider());

            stack.Children.Add(MakeLabel("官方入口"));

            // GUIT 标识：小徽标 + 校名。
            // 这一栏两个入口都是本校的服务，放上校名标识让学生一眼认出来，
            // 也免得"校园网自助服务"那个内网地址被当成来路不明的外链。
            var brandRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 2)
            };

            _guitBadgeText = new TextBlock
            {
                Text = "GUIT",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Theme.OnAccent)
            };
            _guitBadge = new Border
            {
                Background = new SolidColorBrush(Theme.Accent),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _guitBadgeText
            };
            brandRow.Children.Add(_guitBadge);

            var schoolName = new TextBlock
            {
                Text = "桂林信息科技学院",
                FontSize = 11,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.TextMuted)
            };
            _mutedTexts.Add(schoolName);
            brandRow.Children.Add(schoolName);

            stack.Children.Add(brandRow);

            var btnSite = MakeGhostButton("学校官网", delegate() { OpenUrl(UrlSchool); });
            btnSite.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnSite.Margin = new Thickness(0, 6, 0, 6);
            btnSite.FontSize = 11;
            stack.Children.Add(btnSite);

            // 自助服务是校内专属入口，地址来自 SiteConfig（不在公开源码里）。
            // 没配置就不显示 —— 免得给出一个点不开的死链。
            if (SiteConfig.SelfServiceUrl.Length > 0)
            {
                var btnSelf = MakeGhostButton("校园网自助服务",
                    delegate() { OpenUrl(SiteConfig.SelfServiceUrl); });
                btnSelf.HorizontalAlignment = HorizontalAlignment.Stretch;
                btnSelf.FontSize = 11;
                stack.Children.Add(btnSelf);

                var linkHint = MakeHint("自助服务需连在校园网内才能打开。");
                linkHint.Margin = new Thickness(0, 8, 0, 0);
                stack.Children.Add(linkHint);
            }

            card.Child = stack;
            return card;
        }

        // ==================================================================
        // 主题应用
        // ==================================================================

        /// <summary>把当前主题色套到所有已知元素上。</summary>
        internal void ApplyTheme()
        {
            Background = new SolidColorBrush(Theme.WindowBg);

            if (_glassRoot != null)
            {
                _glassRoot.Background = new SolidColorBrush(Theme.GlassPanel);
                _glassRoot.BorderBrush = new SolidColorBrush(Theme.GlassBorder);
            }

            foreach (Border c in _glassCards)
            {
                c.Background = new SolidColorBrush(Theme.GlassCard);
                c.BorderBrush = new SolidColorBrush(Theme.GlassBorder);
            }

            foreach (Border d in _dividers)
            {
                d.Background = new SolidColorBrush(Theme.Divider);
            }

            foreach (TextBlock t in _primaryTexts)
            {
                t.Foreground = new SolidColorBrush(Theme.TextPrimary);
            }

            foreach (TextBlock t in _mutedTexts)
            {
                t.Foreground = new SolidColorBrush(Theme.TextMuted);
            }

            // GUIT 徽标
            if (_guitBadge != null)
            {
                _guitBadge.Background = new SolidColorBrush(Theme.Accent);
            }
            if (_guitBadgeText != null)
            {
                _guitBadgeText.Foreground = new SolidColorBrush(Theme.OnAccent);
            }

            // 顶栏按钮
            StyleGhost(btnOpenHealth);
            StyleGhost(btnOpenSettings);
            StyleGhost(btnDisconnect);
            StyleGhost(btnManageAccounts);
            StyleGhost(btnSpeedTest);

            // 曲线颜色
            if (speedLine != null) speedLine.Stroke = new SolidColorBrush(Theme.Ok);
            if (speedFill != null)
            {
                speedFill.Stroke = new SolidColorBrush(C(Theme.Ok, 70));
                speedFill.Fill = new SolidColorBrush(C(Theme.Ok, 26));
            }
            if (lblSpeedDown != null) lblSpeedDown.Foreground = new SolidColorBrush(Theme.Ok);
            if (lblSpeedUp != null) lblSpeedUp.Foreground = new SolidColorBrush(Theme.Accent);

            // 下拉框
            if (cmbAccount != null)
            {
                cmbAccount.Background = new SolidColorBrush(Theme.FieldBg);
                cmbAccount.Foreground = new SolidColorBrush(Theme.TextPrimary);
                cmbAccount.BorderBrush = new SolidColorBrush(Theme.GlassBorder);
            }

            // 主按钮
            if (btnMainAction != null)
            {
                btnMainAction.Background = new SolidColorBrush(Theme.Accent);
                btnMainAction.Foreground = new SolidColorBrush(Theme.OnAccent);
                btnMainAction.BorderBrush = new SolidColorBrush(Theme.Accent);
            }

            RebuildGlow();

            // 状态条与账号区由逻辑层重绘
            RefreshStatusVisual();
            RefreshQuickSwitch();
            RefreshHistoryUi();
            RefreshQualityUi();
        }

        private static Color C(Color baseColor, byte alpha)
        {
            return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }

        private static void StyleGhost(Button b)
        {
            if (b == null) return;
            b.Background = new SolidColorBrush(Theme.GlassCard);
            b.Foreground = new SolidColorBrush(Theme.TextPrimary);
            b.BorderBrush = new SolidColorBrush(Theme.GlassBorder);
        }

        /// <summary>渲染一项体检结果为徽章卡片（供体检窗口复用）。</summary>
        internal static Border MakeHealthCard(HealthCheckStep step)
        {
            Color c;
            if (step.Status == "OK") c = Theme.Ok;
            else if (step.Status == "ERROR") c = Theme.Err;
            else if (step.Status == "WARN") c = Theme.Warn;
            else c = Theme.Accent;

            var card = new Border
            {
                Background = new SolidColorBrush(Theme.GlassCard),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };

            head.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock
                {
                    Text = step.StatusBadge ?? "",
                    Foreground = new SolidColorBrush(c),
                    FontSize = 11,
                    FontWeight = FontWeights.Medium
                }
            });

            head.Children.Add(new TextBlock
            {
                Text = step.Title ?? "",
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(head);

            if (!string.IsNullOrEmpty(step.Summary))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = step.Summary,
                    Foreground = new SolidColorBrush(Theme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                    FontSize = 12,
                    Opacity = 0.88
                });
            }

            if (step.Details != null && step.Details.Count > 0)
            {
                foreach (string d in step.Details)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "· " + d,
                        Foreground = new SolidColorBrush(Theme.TextMuted),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0),
                        FontSize = 11
                    });
                }
            }

            card.Child = stack;
            return card;
        }
    }
}
