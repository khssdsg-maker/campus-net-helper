using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CampusNetHelper
{
    /// <summary>
    /// 账号管理窗口：左侧列表，右侧编辑表单。
    ///
    /// 支持「从系统已有连接导入」：如果电脑上已经用学校提供的客户端连接过，
    /// Windows 电话簿里会留下那个连接名，可以从下拉框直接选出来填入。
    /// </summary>
    public class AccountWindow : Window
    {
        private readonly MainWindow ownerRef;
        private List<ConfigStore.Account> accounts;
        private ListBox lst;
        private TextBox txtName;
        private TextBox txtUser;
        private PasswordBox txtPass;
        private TextBox txtPassPlain;
        private CheckBox chkShowPass;
        private ComboBox cmbSystem;
        private bool suppress;

        public AccountWindow(MainWindow owner, List<ConfigStore.Account> source)
        {
            ownerRef = owner;

            accounts = new List<ConfigStore.Account>();
            foreach (ConfigStore.Account a in source)
            {
                accounts.Add(new ConfigStore.Account { Name = a.Name, User = a.User, Password = a.Password });
            }

            Title = "管理账号";
            Width = 740;
            Height = 560;
            MinWidth = 660;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.WindowBg);
            FontFamily = new FontFamily(MainWindow.FontUi);
            FontSize = 13;

            BuildUi();
            RefreshList(-1);
        }

        // ---------- 列表 / 下拉项样式（否则深色主题下看不见字） ----------

        private static Style ItemStyle(string typeName)
        {
            return MainWindow.MakeItemStyle(typeName);
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(20) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---------------- 左：列表 ----------------
            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = "账号列表",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary)
            });

            lst = new ListBox
            {
                Height = 330,
                Margin = new Thickness(0, 10, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                ItemContainerStyle = ItemStyle("ListBoxItem")
            };
            lst.SelectionChanged += Lst_SelectionChanged;
            left.Children.Add(lst);

            var btnNew = MainWindow.MakeButton("新建账号", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder,
                delegate() { lst.SelectedIndex = -1; ClearForm(); });
            btnNew.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnNew.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(btnNew);

            root.Children.Add(left);

            // ---------------- 右：表单 ----------------
            var right = new StackPanel();

            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headGrid.Children.Add(new TextBlock
            {
                Text = "账号信息",
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            });

            var headBtns = new StackPanel { Orientation = Orientation.Horizontal };
            headBtns.Children.Add(MainWindow.MakeButton("保存", Theme.Accent, Theme.OnAccent, Theme.Accent, SaveCurrent));
            headBtns.Children.Add(MainWindow.MakeButton("删除", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder, DeleteCurrent));
            ((FrameworkElement)headBtns.Children[1]).Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(headBtns, 1);
            headGrid.Children.Add(headBtns);
            right.Children.Add(headGrid);

            right.Children.Add(Label("连接名称"));
            txtName = Field(false);
            right.Children.Add(txtName);

            right.Children.Add(Label("宽带账号"));
            txtUser = Field(false);
            right.Children.Add(txtUser);

            right.Children.Add(Label("上网密码"));
            txtPass = new PasswordBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary)
            };
            right.Children.Add(txtPass);

            txtPassPlain = Field(false);
            txtPassPlain.Visibility = Visibility.Collapsed;
            txtPassPlain.TextChanged += delegate(object s, TextChangedEventArgs e)
            {
                if (chkShowPass != null && chkShowPass.IsChecked == true) txtPass.Password = txtPassPlain.Text;
            };
            right.Children.Add(txtPassPlain);

            chkShowPass = new CheckBox
            {
                Content = "显示密码",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(Theme.TextMuted),
                FontSize = 11
            };
            chkShowPass.Checked += delegate(object s, RoutedEventArgs e) { TogglePassVisible(true); };
            chkShowPass.Unchecked += delegate(object s, RoutedEventArgs e) { TogglePassVisible(false); };
            right.Children.Add(chkShowPass);

            // 从电话簿里已有的连接导入
            right.Children.Add(Label("从已有宽带连接导入（可选）"));
            var importRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            cmbSystem = MainWindow.MakeDarkCombo(260);
            importRow.Children.Add(cmbSystem);
            var btnImport = MainWindow.MakeButton("导入", Theme.GlassCard, Theme.TextPrimary, Theme.GlassBorder,
                delegate() { ImportFromSystem(); });
            btnImport.Margin = new Thickness(8, 0, 0, 0);
            importRow.Children.Add(btnImport);
            right.Children.Add(importRow);

            var tip = new TextBlock
            {
                Text = "如果电脑上已经建过宽带连接（学校客户端建的那个也会列出来），"
                     + "从上面选一个点「导入」即可填入连接名称，再补上上网账号与密码。\n\n"
                     + "保存后连接会写入 Windows 的电话簿，在「网络和共享中心」里也能看到，两边是同一个。\n\n"
                     + "账号与密码保存在本机：" + ConfigStore.AppDataDir + "\n"
                     + "本工具不会向任何服务器查询或上传你的账号信息。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextFaint),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 16, 0, 0)
            };
            right.Children.Add(tip);

            Grid.SetColumn(right, 2);
            root.Children.Add(right);

            Content = root;
            LoadSystemEntries();
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Theme.TextMuted),
                Margin = new Thickness(0, 14, 0, 4)
            };
        }

        private static TextBox Field(bool multiline)
        {
            return new TextBox
            {
                Margin = new Thickness(0, 0, 0, 0),
                Background = new SolidColorBrush(Theme.FieldBg),
                Foreground = new SolidColorBrush(Theme.TextPrimary),
                BorderBrush = new SolidColorBrush(Theme.GlassBorder),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CaretBrush = new SolidColorBrush(Theme.TextPrimary),
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline
            };
        }

        private void LoadSystemEntries()
        {
            try
            {
                List<string> sys = DialEngine.ListEntries();
                cmbSystem.Items.Clear();
                if (sys.Count == 0)
                {
                    cmbSystem.Items.Add("(没有找到已有连接)");
                    cmbSystem.IsEnabled = false;
                }
                else
                {
                    foreach (string s in sys) cmbSystem.Items.Add(s);
                    cmbSystem.SelectedIndex = 0;
                    cmbSystem.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取电话簿失败: " + ex.Message);
            }
        }

        private void ImportFromSystem()
        {
            string sel = cmbSystem != null ? cmbSystem.SelectedItem as string : null;
            if (string.IsNullOrEmpty(sel) || sel.StartsWith("("))
            {
                MessageBox.Show("下拉框里没有可导入的连接。\n\n可以先用学校提供的客户端连接一次网络，"
                    + "Windows 就会记住这个连接，然后回到这里导入。",
                    "导入", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            txtName.Text = sel;

            // 1) 先看本工具自己存过的账号（用户改过密码时以这里为准）
            string user = "";
            string pass = "";
            foreach (ConfigStore.Account a in accounts)
            {
                if (string.Equals(a.Name, sel, StringComparison.Ordinal))
                {
                    user = a.User;
                    pass = a.Password;
                    break;
                }
            }

            // 2) 再从电话簿条目里读 Windows 保存的账号密码，补上空缺的那部分
            string pbkUser, pbkPass;
            DialEngine.ReadEntryCredentials(sel, out pbkUser, out pbkPass);
            if (string.IsNullOrEmpty(user)) user = pbkUser;
            if (string.IsNullOrEmpty(pass)) pass = pbkPass;

            txtUser.Text = user;
            txtPass.Password = pass;
            txtPassPlain.Text = pass;

            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                MessageBox.Show("已导入连接「" + sel + "」的账号与密码。\n\n"
                    + "确认无误后点「保存」即可。",
                    "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!string.IsNullOrEmpty(user))
            {
                MessageBox.Show("已导入连接名称与账号：「" + sel + "」\n\n"
                    + "这个连接在本机没有保存密码，请补填「上网密码」后点「保存」。",
                    "部分导入", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("已填入连接名称：「" + sel + "」\n\n"
                    + "这个连接在本机没有保存账号密码（可能由系统的凭据管理器保管），"
                    + "请手动填写「宽带账号」与「上网密码」后点「保存」。",
                    "导入", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void TogglePassVisible(bool visible)
        {
            if (visible)
            {
                txtPassPlain.Text = txtPass.Password;
                txtPassPlain.Visibility = Visibility.Visible;
                txtPass.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtPass.Password = txtPassPlain.Text;
                txtPassPlain.Visibility = Visibility.Collapsed;
                txtPass.Visibility = Visibility.Visible;
            }
        }

        private string CurrentPassword()
        {
            return (chkShowPass != null && chkShowPass.IsChecked == true) ? txtPassPlain.Text : txtPass.Password;
        }

        private void RefreshList(int selectIndex)
        {
            suppress = true;
            try
            {
                lst.Items.Clear();
                foreach (ConfigStore.Account a in accounts) lst.Items.Add(a.Name);
                if (selectIndex >= 0 && selectIndex < accounts.Count)
                {
                    lst.SelectedIndex = selectIndex;
                    LoadForm(accounts[selectIndex]);
                }
                else
                {
                    ClearForm();
                }
            }
            finally { suppress = false; }
        }

        private void LoadForm(ConfigStore.Account a)
        {
            suppress = true;
            try
            {
                txtName.Text = a.Name;
                txtUser.Text = a.User;
                txtPass.Password = a.Password;
                txtPassPlain.Text = a.Password;
            }
            finally { suppress = false; }
        }

        private void ClearForm()
        {
            suppress = true;
            try
            {
                txtName.Text = "";
                txtUser.Text = "";
                txtPass.Password = "";
                txtPassPlain.Text = "";
            }
            finally { suppress = false; }
        }

        private void Lst_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress) return;
            int i = lst.SelectedIndex;
            if (i >= 0 && i < accounts.Count) LoadForm(accounts[i]);
        }

        private void SaveCurrent()
        {
            string name = (txtName.Text ?? "").Trim();
            string user = (txtUser.Text ?? "").Trim();
            string pass = CurrentPassword() ?? "";

            if (name.Length == 0)
            {
                MessageBox.Show("请填写连接名称。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (user.Length == 0)
            {
                MessageBox.Show("请填写宽带账号。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int sel = lst.SelectedIndex;
            int existIdx = -1;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (string.Equals(accounts[i].Name, name, StringComparison.Ordinal)) { existIdx = i; break; }
            }
            if (existIdx >= 0 && existIdx != sel)
            {
                MessageBox.Show("已经有一个叫「" + name + "」的账号了，请换个名称。",
                    "账号", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var item = new ConfigStore.Account { Name = name, User = user, Password = pass };

            if (sel >= 0 && sel < accounts.Count) accounts[sel] = item;
            else accounts.Add(item);

            string msg;
            if (!ConfigStore.SaveAccounts(accounts, out msg))
            {
                MessageBox.Show("保存失败: " + msg, "账号", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log.Info("账号已保存: " + name);

            try
            {
                string pm;
                DialEngine.EnsureEntry(name, user, pass, out pm);
            }
            catch (Exception ex) { Log.Warn("同步电话簿条目失败: " + ex.Message); }

            RefreshList(accounts.IndexOf(item));
            ownerRef.RefreshAccountList();
        }

        private void DeleteCurrent()
        {
            int sel = lst.SelectedIndex;
            if (sel < 0 || sel >= accounts.Count)
            {
                MessageBox.Show("请先在左侧选中要删除的账号。", "账号",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = accounts[sel].Name;
            MessageBoxResult r = MessageBox.Show("确定要删除账号「" + name + "」吗？", "删除账号",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            accounts.RemoveAt(sel);

            string msg;
            ConfigStore.SaveAccounts(accounts, out msg);

            string rm;
            DialEngine.RemoveEntry(name, out rm);
            Log.Info("账号已删除: " + name + " (" + rm + ")");

            RefreshList(-1);
            ownerRef.RefreshAccountList();
        }
    }
}
