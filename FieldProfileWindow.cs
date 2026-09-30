using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CampusNetHelper
{
    /// <summary>
    /// 「字段档案」编辑窗口。
    ///
    /// 解决什么问题：
    ///   认证页上的框太多时，靠 name/id 猜不准（桂电信科那页就有 5 个框）。
    ///   这个窗口把页面上**实际存在的**输入框列出来，让用户一个个指定"该填什么"，
    ///   存成档案。以后打开这一页，程序照着档案填，不走猜的逻辑。
    ///
    /// 交互设计（为什么这么做）：
    ///   · 左边一列是页面上扫到的框，右边是"这框该填什么"——
    ///     用户不用理解 name/id 是什么，只需要认得页面上那行字。
    ///   · 类型只有 4 种，其中 3 种是"从账号取"（改一次账号密码全档案跟着变）、
    ///     1 种是"固定值"（运营商、学校代码那种每次都一样的东西）。
    ///   · 验证码单独列一种，程序永远不填它 —— 这是刻意的，见 WebAuthWindow 里的说明。
    /// </summary>
    public class FieldProfileWindow : Window
    {
        private readonly MainWindow ownerRef;
        private readonly string url;

        /// <summary>扫到的页面字段（已克隆，改了不影响外面）。</summary>
        private List<FieldProfileStore.FieldProfile> fields;

        /// <summary>右侧整个编辑区。没选中任何字段时整块禁用 —— 只此一处开关。</summary>
        private StackPanel rightPanel;

        /// <summary>当前编辑的字段下标；-1 = 没选。</summary>
        private int currentIndex = -1;

        private ListBox lstFields;
        private ComboBox cmbType;
        private ComboBox cmbAccountField;
        private TextBox txtFixedValue;
        private CheckBox chkEnabled;
        private TextBlock lblPreview;
        private StackPanel panelAccount;
        private StackPanel panelFixed;
        private StackPanel panelCaptcha;
        private StackPanel panelIgnore;
        private TextBlock lblAdvice;

        /// <summary>用户点了"保存"。</summary>
        public bool Saved { get; private set; }

        // ==================================================================
        // 自测用的调试接口（只被 UiTest 调用，正常运行不受影响）
        // ==================================================================

        /// <summary>把"固定值输入框"那个 TextBox 交出来，让自测能模拟打字。</summary>
        internal TextBox DebugFixedValueBox { get { return txtFixedValue; } }

        /// <summary>「固定值」那个面板现在可不可见。</summary>
        internal bool DebugPanelFixedVisible()
        {
            return panelFixed != null && panelFixed.Visibility == Visibility.Visible;
        }

        /// <summary>切类型（0=从账号取 1=固定值 2=验证码 3=不填），按用户的路径走一遍。</summary>
        internal void DebugSetTypeIndex(int i)
        {
            cmbType.SelectedIndex = i;
            OnTypeChanged();
        }

        /// <summary>选中列表里第 i 行。</summary>
        internal void DebugSelectRow(int i)
        {
            if (lstFields != null && i >= 0 && i < lstFields.Items.Count) lstFields.SelectedIndex = i;
        }

        /// <summary>当前编辑的字段下标（自测用）。</summary>
        internal int DebugCurrentIndex() { return currentIndex; }

        /// <summary>右侧整块编辑区是否可用（自测用）。</summary>
        internal bool DebugRightPanelEnabled()
        {
            return rightPanel != null && rightPanel.IsEnabled;
        }

        /// <summary>左侧列表行数（自测用）。</summary>
        internal int DebugRowCount() { return lstFields == null ? -1 : lstFields.Items.Count; }

        public FieldProfileWindow(MainWindow owner, string authUrl,
            List<FieldProfileStore.FieldProfile> scannedFields)
        {
            ownerRef = owner;
            url = authUrl ?? "";
            Saved = false;

            // 克隆一份再编辑 —— 用户点"取消"时外面那份不能被改脏
            fields = new List<FieldProfileStore.FieldProfile>();
            if (scannedFields != null)
            {
                foreach (FieldProfileStore.FieldProfile f in scannedFields) fields.Add(f.Clone());
            }

            Title = "字段档案 —— 教程序认框";
            Width = 880;
            Height = 660;
            MinWidth = 780;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();
            RefreshList(0);
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 网址
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 建议条
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 主体
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部按钮

            // ---------- 标题 ----------
            var head = new StackPanel();
            head.Children.Add(new TextBlock
            {
                Text = "字段档案",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });
            head.Children.Add(new TextBlock
            {
                Text = "左边是程序在你这个认证页上扫到的输入框。选中一个，在右边告诉程序「这个框该填什么」。"
                     + "存好后，以后打开这一页就按这份档案填，不用再靠猜。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 6, 0, 0)
            });
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ---------- 网址 ----------
            var urlBox = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(10, 7, 10, 7),
                Background = new SolidColorBrush(Theme.FieldBg),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1)
            };
            urlBox.Child = new TextBlock
            {
                Text = "适用网址：" + (url.Length > 0 ? url : "（没填网址）"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(urlBox, 1);
            root.Children.Add(urlBox);

            // ---------- 建议条 ----------
            // ⚠️ 固定高度：这行文字长短不一，如果让它自己撑高，下面的内容会上下跳。
            //    用户在右边输入框打字时最忌讳这个（会被 WPF 的跟随焦点滚动甩走）。
            lblAdvice = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Height = 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(lblAdvice, 2);
            root.Children.Add(lblAdvice);

            // ---------- 主体：左右两栏 ----------
            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左：框列表
            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = "页面上的输入框",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });
            lstFields = new ListBox
            {
                Height = 380,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                ItemContainerStyle = MainWindow.MakeItemStyle("ListBoxItem")
            };
            lstFields.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (_rebuildingList) return;
                LoadField(lstFields.SelectedIndex);
            };
            // 列表本身永远不要抢键盘焦点 ——
            // 用户在右边输入框打字时，只要列表有任何理由被激活，
            // 焦点就会被它夺走，输入就断了（用户报的正是这个）。
            lstFields.Focusable = false;
            left.Children.Add(lstFields);

            var btnRescan = MainWindow.MakeButton("重新扫描这一页", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder,
                delegate() { RescanFromPage(); });
            btnRescan.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnRescan.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(btnRescan);

            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            // 右：编辑区
            var right = new StackPanel();
            rightPanel = right;   // 整块的可用性统一由 LoadField 控制（见那里的注释）
            right.Children.Add(new TextBlock
            {
                Text = "这个框该填什么",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });

            // 类型下拉
            cmbType = MainWindow.MakeDarkCombo(280);
            cmbType.Margin = new Thickness(0, 8, 0, 0);
            cmbType.Items.Add("从账号取 —— 跟着账号走，改密码不用重教");
            cmbType.Items.Add("固定值 —— 每次都填一样的内容");
            cmbType.Items.Add("验证码 —— 由我自己手动填");
            cmbType.Items.Add("不填 —— 这个框程序不要动");
            // ⚠️ 用户亲手改类型时，**别自动切换界面上的行**。
            //    之前这里会读下拉框自己的值，用户拉一下列表就把当前字段重新归类了 ——
            //    那不是用户的本意。
            cmbType.DropDownClosed += delegate(object s, EventArgs e) { OnTypeChanged(); };
            right.Children.Add(cmbType);

            // —— 从账号取 ——
            panelAccount = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panelAccount.Children.Add(Label("取账号里的哪一项"));
            cmbAccountField = MainWindow.MakeDarkCombo(280);
            cmbAccountField.Items.Add("上网账号（宽带账号 / 手机号）");
            cmbAccountField.Items.Add("上网密码");
            cmbAccountField.Items.Add("附加账号（学工号）");
            cmbAccountField.SelectionChanged += delegate(object s, SelectionChangedEventArgs e) { OnDetailChanged(); };
            panelAccount.Children.Add(cmbAccountField);
            right.Children.Add(panelAccount);

            // —— 固定值 ——
            panelFixed = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panelFixed.Children.Add(Label("每次都填这个值"));
            txtFixedValue = Field();
            txtFixedValue.TextChanged += delegate(object s, TextChangedEventArgs e) { OnDetailChanged(); };
            panelFixed.Children.Add(txtFixedValue);
            right.Children.Add(panelFixed);

            // —— 验证码 / 不填：只有说明 ——
            panelCaptcha = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panelCaptcha.Children.Add(new TextBlock
            {
                Text = "验证码不会由程序代填 —— 它存在的意义就是证明填表的是个人。\n"
                     + "程序会把其它框都填好，验证码你自己看一眼填进去，再点页面上的「登录」。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            });
            right.Children.Add(panelCaptcha);

            panelIgnore = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panelIgnore.Children.Add(new TextBlock
            {
                Text = "这个框会被跳过，程序不碰它。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            });
            right.Children.Add(panelIgnore);

            chkEnabled = new CheckBox
            {
                Content = "参与自动填写",
                IsChecked = true,
                Margin = new Thickness(0, 16, 0, 0),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                FontSize = 12
            };
            chkEnabled.Checked += delegate(object s, RoutedEventArgs e) { OnDetailChanged(); };
            chkEnabled.Unchecked += delegate(object s, RoutedEventArgs e) { OnDetailChanged(); };
            right.Children.Add(chkEnabled);

            // 结果预览
            //
            // ⚠️ 这里必须严格限制行数（见 UpdatePreview）。
            //    预览文字行数一变，下面所有内容就跟着上下跳 ——
            //    而用户可能正把鼠标停在"固定值"那个输入框上打字。
            //    跳动会让 WPF 为了跟随焦点自动滚动 / 让输入框从鼠标底下跑掉，
            //    用户的感觉就是"我每打一个字它就把输入框关了"。
            lblPreview = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                // 固定高度 + 文本裁剪：内容再多也占这么多地方，界面绝不跳动
                Height = 72,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 16, 0, 0)
            };
            right.Children.Add(lblPreview);

            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            Grid.SetRow(body, 3);
            root.Children.Add(body);

            // ---------- 底部按钮 ----------
            var foot = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var footHint = new TextBlock
            {
                Text = "档案只保存「哪个框填什么」，不保存密码本身 —— 密码仍然从「管理账号」那份加密数据里取。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(footHint, 0);
            foot.Children.Add(footHint);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(MainWindow.MakeButton("取消", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder,
                delegate() { Close(); }));
            var btnSave = MainWindow.MakePrimaryButton("保存档案", SaveAndClose);
            btnSave.Margin = new Thickness(8, 0, 0, 0);
            btns.Children.Add(btnSave);
            Grid.SetColumn(btns, 1);
            foot.Children.Add(btns);

            Grid.SetRow(foot, 4);
            root.Children.Add(foot);

            Content = root;

            // 没有扫到框时给一句明确的话
            if (fields.Count == 0)
            {
                lblAdvice.Text = "⚠️ 一个输入框都没扫到 —— 可能是页面还没加载完，或者认证页在跨域框架里（IE 内核取不到）。"
                    + "先回认证窗口等页面显示出来，再点「重新扫描这一页」。";
            }
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static TextBox Field()
        {
            return new TextBox
            {
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
        }

        // ==================================================================
        // 列表
        // ==================================================================

        private void RefreshList(int select)
        {
            // 重建列表期间压住 SelectionChanged ——
            // 否则 Clear() 会产生一次"选中变空"，把编辑区禁用掉，
            // 紧接着 SelectedIndex = select 又把状态翻回来，白白重排两次。
            _rebuildingList = true;
            try
            {
                lstFields.Items.Clear();
                for (int i = 0; i < fields.Count; i++)
                {
                    FieldProfileStore.FieldProfile f = fields[i];
                    string tag = f.Label.Length > 0 ? f.Label : (f.Id.Length > 0 ? f.Id : f.Name);
                    if (tag.Length == 0) tag = "第 " + (i + 1) + " 个框";
                    lstFields.Items.Add((f.Enabled ? "" : "（停用）") + tag + "   →   " + FieldProfileStore.KindText(f));
                }

                if (fields.Count == 0)
                {
                    currentIndex = -1;
                    if (rightPanel != null) rightPanel.IsEnabled = false;
                    lblPreview.Text = "";
                    return;
                }

                if (select < 0 || select >= fields.Count) select = 0;
                lstFields.SelectedIndex = select;
            }
            finally { _rebuildingList = false; }

            LoadField(select);
        }

        /// <summary>正在重建列表 —— 期间忽略 SelectionChanged。</summary>
        private bool _rebuildingList = false;

        private void LoadField(int idx)
        {
            // ⚠️ **空选中一律忽略**，不要当成"用户取消了选择"。
            //
            // 踩过的坑（这是用户报的"每打一个字就得重新点一下"的真凶）：
            //   点一下「类型」下拉框，WPF 会顺手把左侧 ListBox 的选中清空，
            //   于是这里收到 idx = -1 → 整块编辑区被判为"没用上"→ 禁用，
            //   用户正在打字的那个输入框当场变灰、打不进字。
            //   用户只好重新点行、重新点输入框，再打一个字 —— 循环往复。
            //
            //   选中清空只是"焦点转移"的副作用，**不是**用户的意图。
            //   所以这里直接忽略：编辑区保持原样，用户该打什么打什么。
            if (idx < 0 || idx >= fields.Count)
            {
                if (currentIndex < 0) lblPreview.Text = "";
                return;
            }

            currentIndex = idx;

            // 整块编辑区的可用性统一在这里控制 ——
            // 绝不再逐个设 txtFixedValue.IsEnabled 之类的开关（两套状态必然打架）。
            if (rightPanel != null) rightPanel.IsEnabled = true;

            FieldProfileStore.FieldProfile f = fields[idx];

            // 把当前档案翻译成界面上的选择 —— 期间会触发 SelectionChanged，
            // 用 suppress 避免把用户还没改的东西又读回去写一遍。
            suppressUi = true;
            try
            {
                chkEnabled.IsChecked = f.Enabled;

                if (f.Kind == FieldProfileStore.KindFixed) cmbType.SelectedIndex = 1;
                else if (f.Kind == FieldProfileStore.KindCaptcha) cmbType.SelectedIndex = 2;
                else if (f.Kind == FieldProfileStore.KindAccount) cmbType.SelectedIndex = 0;
                else cmbType.SelectedIndex = 3;

                if (string.Equals(f.AccountField, "password", StringComparison.OrdinalIgnoreCase))
                    cmbAccountField.SelectedIndex = 1;
                else if (string.Equals(f.AccountField, "user2", StringComparison.OrdinalIgnoreCase))
                    cmbAccountField.SelectedIndex = 2;
                else
                    cmbAccountField.SelectedIndex = 0;

                txtFixedValue.Text = f.Value ?? "";
            }
            finally { suppressUi = false; }

            UpdatePanels();
            UpdateAdvice(f);
            UpdatePreview();
        }

        private bool suppressUi = false;

        private void OnTypeChanged()
        {
            if (suppressUi) return;

            ApplyUiToField();

            // ⚠️ 这里**不是**直接调用 UpdatePanels()，而是排到"布局空闲"时再改，
            //    并且只在面板显隐**真的需要变**时才动。
            //
            // 原因（踩过两次的坑）：
            //   用户在「固定值」输入框里打字时也会走到这里。如果这时候同步改
            //   各个面板的 Visibility，布局当场重排 —— 输入框的位置跟着动，
            //   用户鼠标底下的东西就跑了，感觉像"程序把输入框关了"。
            //   排到后台优先级 + 只在必要时改，打字就不会引起任何重排。
            SchedulePanelSync();
            RefreshRowText();
            UpdatePreview();
        }

        /// <summary>
        /// 把"面板显隐同步"延迟到布局空闲时执行，并且只在需要变时才改。
        ///
        /// 为什么不用 DispatcherTimer 之类的"定时器"：
        ///   这里要的是"这一轮输入处理完之后"，不是"过一会儿"。
        ///   Background 优先级的 BeginInvoke 正好是这个语义，
        ///   而且打字时用户手指不会停，队列里也不会积压。
        /// </summary>
        private bool _panelSyncQueued = false;

        private void SchedulePanelSync()
        {
            if (_panelSyncQueued) return;
            _panelSyncQueued = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate()
            {
                _panelSyncQueued = false;
                SyncPanelsIfNeeded();
            }));
        }

        /// <summary>只在面板显隐与当前类型不一致时才改（避免无意义的重排）。</summary>
        private void SyncPanelsIfNeeded()
        {
            if (cmbType == null || panelFixed == null) return;

            int t = cmbType.SelectedIndex;

            Visibility wantAccount = t == 0 ? Visibility.Visible : Visibility.Collapsed;
            Visibility wantFixed = t == 1 ? Visibility.Visible : Visibility.Collapsed;
            Visibility wantCaptcha = t == 2 ? Visibility.Visible : Visibility.Collapsed;
            Visibility wantIgnore = t == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (panelAccount.Visibility != wantAccount) panelAccount.Visibility = wantAccount;
            if (panelFixed.Visibility != wantFixed) panelFixed.Visibility = wantFixed;
            if (panelCaptcha.Visibility != wantCaptcha) panelCaptcha.Visibility = wantCaptcha;
            if (panelIgnore.Visibility != wantIgnore) panelIgnore.Visibility = wantIgnore;
        }

        private void OnDetailChanged()
        {
            if (suppressUi) return;
            ApplyUiToField();
            RefreshRowText();
            UpdatePreview();
        }

        /// <summary>把界面上的选择写回当前字段。</summary>
        private void ApplyUiToField()
        {
            if (currentIndex < 0 || currentIndex >= fields.Count) return;
            FieldProfileStore.FieldProfile f = fields[currentIndex];

            int t = cmbType.SelectedIndex;
            if (t == 0)
            {
                f.Kind = FieldProfileStore.KindAccount;
                if (cmbAccountField.SelectedIndex == 1) f.AccountField = "password";
                else if (cmbAccountField.SelectedIndex == 2) f.AccountField = "user2";
                else f.AccountField = "user";
                f.Value = "";
            }
            else if (t == 1)
            {
                f.Kind = FieldProfileStore.KindFixed;
                f.Value = txtFixedValue.Text ?? "";
                f.AccountField = "";
            }
            else if (t == 2)
            {
                f.Kind = FieldProfileStore.KindCaptcha;
                f.Value = "";
                f.AccountField = "";
            }
            else
            {
                f.Kind = FieldProfileStore.KindIgnore;
                f.Value = "";
                f.AccountField = "";
            }

            f.Enabled = chkEnabled.IsChecked == true;
        }

        /// <summary>把四个选项面板的显隐调成与当前类型一致（立即执行）。</summary>
        private void UpdatePanels()
        {
            SyncPanelsIfNeeded();
        }

        /// <summary>给"这个框可能是什么"的提示 —— 减少用户瞎猜。</summary>
        private void UpdateAdvice(FieldProfileStore.FieldProfile f)
        {
            string blob = ((f.Label ?? "") + " " + (f.Name ?? "") + " " + (f.Id ?? "")).ToLowerInvariant();
            string advice = "";

            bool looksPwd = blob.IndexOf("pwd", StringComparison.Ordinal) >= 0
                || blob.IndexOf("pass", StringComparison.Ordinal) >= 0
                || blob.IndexOf("密码", StringComparison.Ordinal) >= 0;
            bool looksCc = blob.IndexOf("cc", StringComparison.Ordinal) >= 0
                || blob.IndexOf("captcha", StringComparison.Ordinal) >= 0
                || blob.IndexOf("checkcode", StringComparison.Ordinal) >= 0
                || blob.IndexOf("验证码", StringComparison.Ordinal) >= 0;
            bool looksIdentity = blob.IndexOf("guitid", StringComparison.Ordinal) >= 0
                || blob.IndexOf("student", StringComparison.Ordinal) >= 0
                || blob.IndexOf("stuid", StringComparison.Ordinal) >= 0
                || blob.IndexOf("学工号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("学号", StringComparison.Ordinal) >= 0
                || blob.IndexOf("工号", StringComparison.Ordinal) >= 0;
            bool looksAccount = blob.IndexOf("acc", StringComparison.Ordinal) >= 0
                || blob.IndexOf("user", StringComparison.Ordinal) >= 0
                || blob.IndexOf("account", StringComparison.Ordinal) >= 0;
            bool looksIsp = blob.IndexOf("isp", StringComparison.Ordinal) >= 0
                || blob.IndexOf("运营商", StringComparison.Ordinal) >= 0
                || blob.IndexOf("operator", StringComparison.Ordinal) >= 0;

            if (looksPwd) advice = "这个看起来是密码框 —— 选「从账号取 → 上网密码」。";
            else if (looksCc) advice = "这个看起来是验证码框 —— 选「验证码」，程序不填它。";
            else if (looksIdentity) advice = "这个看起来是学工号/身份号框 —— 选「从账号取 → 附加账号（学工号）」。";
            else if (looksIsp) advice = "这个看起来是运营商/ISP 框 —— 每次都填同一个值，用「固定值」；或者选「不填」交给页面默认值。";
            else if (looksAccount) advice = "这个看起来是账号框 —— 选「从账号取 → 上网账号」。";

            string head = "页面识别到的信息：标签「" + (f.Label ?? "")
                + "」  name=" + ((f.Name ?? "").Length > 0 ? f.Name : "(无)")
                + "  id=" + ((f.Id ?? "").Length > 0 ? f.Id : "(无)")
                + "  第 " + (f.Index + 1) + " 个输入框";

            lblAdvice.Text = head + (advice.Length > 0 ? "\n💡 " + advice : "");
        }

        private void RefreshRowText()
        {
            int i = currentIndex;
            if (i < 0 || i >= fields.Count) return;
            FieldProfileStore.FieldProfile f = fields[i];
            string tag = f.Label.Length > 0 ? f.Label : (f.Id.Length > 0 ? f.Id : f.Name);
            if (tag.Length == 0) tag = "第 " + (i + 1) + " 个框";

            lstFields.Items[i] = (f.Enabled ? "" : "（停用）") + tag + "   →   " + FieldProfileStore.KindText(f);
        }

        private void UpdatePreview()
        {
            if (currentIndex < 0 || currentIndex >= fields.Count) { lblPreview.Text = ""; return; }

            // 用「示例账号」跑一遍，让用户看到实际会填什么
            var sample = new ConfigStore.Account
            {
                Name = "(示例)",
                User = "13800000000",
                Password = "(账号里的密码)",
                User2 = "20230001"
            };

            var filled = new List<string>();
            var skipped = new List<string>();
            foreach (FieldProfileStore.FieldProfile f in fields)
            {
                if (!f.Enabled || f.Kind == FieldProfileStore.KindIgnore)
                {
                    skipped.Add(ShortTag(f));
                    continue;
                }
                if (f.Kind == FieldProfileStore.KindCaptcha)
                {
                    skipped.Add(ShortTag(f) + "（你填）");
                    continue;
                }
                string v = FieldProfileStore.ResolveValue(f, sample);
                if (v == null) skipped.Add(ShortTag(f) + "（账号里没值）");
                else filled.Add(ShortTag(f) + " = " + MaskIfPassword(f, v));
            }

            // ⚠️ 行数必须**恒定 4 行**，否则用户打字时界面会上下跳（见 BuildUi 里的注释）。
            //    所以这里主动截断：每行超长就砍掉尾巴，绝不让它自己换行。
            string page = "保存后，打开这个网址会这样填：";
            string l2 = filled.Count > 0 ? "  填写：" + Join(filled) : "  填写：（无）";
            string l3 = skipped.Count > 0 ? "  跳过：" + Join(skipped) : "  跳过：（无）";
            string l4 = "（预览用示例账号 —— 学工号 20230001、账号 13800000000）";

            lblPreview.Text = FitLine(page, 46) + "\n" + FitLine(l2, 46) + "\n" + FitLine(l3, 46) + "\n" + FitLine(l4, 46);
        }

        /// <summary>把一行文字砍到指定宽度（按"中文字算两个字符"估宽）。</summary>
        private static string FitLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            int w = 0;
            foreach (char c in s)
            {
                int cw = c > 0x2E7F ? 2 : 1;   // 中日韩字符按 2 计
                if (w + cw > max - 1) { sb.Append('…'); break; }
                sb.Append(c);
                w += cw;
            }
            return sb.ToString();
        }

        private static string Join(List<string> items)
        {
            if (items == null || items.Count == 0) return "";
            return string.Join("、", items.ToArray());
        }

        private static string ShortTag(FieldProfileStore.FieldProfile f)
        {
            if (!string.IsNullOrEmpty(f.Label)) return f.Label;
            if (!string.IsNullOrEmpty(f.Id)) return f.Id;
            if (!string.IsNullOrEmpty(f.Name)) return f.Name;
            return "第 " + (f.Index + 1) + " 个框";
        }

        private static string MaskIfPassword(FieldProfileStore.FieldProfile f, string v)
        {
            if (string.Equals(f.AccountField, "password", StringComparison.OrdinalIgnoreCase))
                return "••••••";
            return v;
        }

        // ==================================================================
        // 重新扫描 / 保存
        // ==================================================================

        private void RescanFromPage()
        {
            List<FieldProfileStore.FieldProfile> scanned = ownerRef.ScanWebAuthFields();
            if (scanned == null || scanned.Count == 0)
            {
                MessageBox.Show("当前没扫到输入框。\n\n请先关掉这个窗口，回到「校园网网页认证」等页面完全显示出来，再打开字段档案。",
                    "重新扫描", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 保留用户已经配好的设置：按 name+id 把新扫到的框和旧配置对回去
            var old = fields;
            fields = new List<FieldProfileStore.FieldProfile>();
            foreach (FieldProfileStore.FieldProfile nf in scanned)
            {
                FieldProfileStore.FieldProfile kept = null;
                foreach (FieldProfileStore.FieldProfile of in old)
                {
                    if (Key(of).Length > 0 && Key(of) == Key(nf)) { kept = of; break; }
                }
                if (kept != null)
                {
                    FieldProfileStore.FieldProfile merged = kept.Clone();
                    merged.Label = nf.Label;
                    merged.Index = nf.Index;
                    fields.Add(merged);
                }
                else fields.Add(nf);
            }

            RefreshList(0);
            MessageBox.Show("重新扫描完成，共扫到 " + fields.Count + " 个输入框。",
                "重新扫描", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string Key(FieldProfileStore.FieldProfile f)
        {
            return ((f.Id ?? "") + "|" + (f.Name ?? "")).ToLowerInvariant();
        }

        private void SaveAndClose()
        {
            if (fields.Count == 0)
            {
                MessageBox.Show("没有可保存的字段。先回认证窗口把页面打开，再点「重新扫描这一页」。",
                    "保存档案", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 至少得有一个"会被填"的字段，否则这份档案没有意义
            int active = 0;
            foreach (FieldProfileStore.FieldProfile f in fields)
            {
                if (f.Enabled && f.Kind != FieldProfileStore.KindIgnore) active++;
            }
            if (active == 0)
            {
                MessageBox.Show("所有框都是「不填」—— 这份档案存下来也不会填任何东西。\n\n"
                    + "至少给账号框、密码框指定「从账号取」。",
                    "保存档案", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var p = new FieldProfileStore.Profile { Url = url };
            foreach (FieldProfileStore.FieldProfile f in fields) p.Fields.Add(f);

            string msg;
            if (!FieldProfileStore.Save(p, out msg))
            {
                MessageBox.Show("保存失败：" + msg, "保存档案", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log.Info("字段档案已保存: " + FieldProfileStore.NormalizeUrl(url) + " 共 " + fields.Count + " 个字段");
            Saved = true;
            Close();
        }
    }
}
