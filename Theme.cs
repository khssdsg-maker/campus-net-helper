using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CampusNetHelper
{
public enum ThemeMode { Light, Dark }

/// <summary>用户的主题偏好：跟随系统 / 固定浅色 / 固定深色。</summary>
public enum ThemePreference { System, Light, Dark }

/// <summary>
/// 主题系统：磨砂玻璃风格。
///
/// 支持三种偏好（在「设置」里切换）：
///   · 跟随系统 —— 读 Windows 的浅色 / 深色设置（默认）
///   · 固定浅色
///   · 固定深色
///
/// 系统主题的判定依据是注册表
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme
/// 值为 1 表示浅色、0 表示深色（Windows 10 1809 之后的标准位置）。
///
/// 变化监听使用 3 秒轮询：注册表的 UserPreferenceChanged 事件在部分机器上不可靠，
/// 而轮询一次只读一个注册表值，开销可以忽略。
/// </summary>
public static class Theme
{
    private static DispatcherTimer _watch;
    private const string PrefKey = "ThemePreference";

    /// <summary>当前生效的主题（已按偏好解析过）。</summary>
    public static ThemeMode Current { get; private set; }

    /// <summary>用户的主题偏好。</summary>
    public static ThemePreference Preference { get; private set; }

    /// <summary>主题发生变化时触发（用于让界面重绘）。</summary>
    public static event EventHandler Changed;

    /// <summary>初始化：读偏好 + 解析出实际主题，并开始监听系统变化。</summary>
    public static void Init()
    {
        Preference = LoadPreference();
        Current = Resolve(Preference);

        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += OnWatchTick;
        _watch.Start();

        Log.Info("主题初始化: 偏好=" + PrefName(Preference) + " 实际=" + Name(Current));
    }

    private static void OnWatchTick(object sender, EventArgs e)
    {
        // 只有「跟随系统」时才需要盯着注册表
        if (Preference != ThemePreference.System) return;

        ThemeMode m = Resolve(ThemePreference.System);
        if (m != Current)
        {
            Current = m;
            Log.Info("系统主题已切换 → " + Name(m));
            if (Changed != null) Changed(null, EventArgs.Empty);
        }
    }

    /// <summary>切换主题偏好（设置窗口调用）。</summary>
    public static void SetPreference(ThemePreference p)
    {
        Preference = p;
        SavePreference(p);

        ThemeMode m = Resolve(p);
        Log.Info("主题偏好改为: " + PrefName(p) + " 实际=" + Name(m));

        if (m != Current)
        {
            Current = m;
            if (Changed != null) Changed(null, EventArgs.Empty);
        }
    }

    /// <summary>强制按当前偏好重新解析并广播。</summary>
    public static void Refresh()
    {
        ThemeMode m = Resolve(Preference);
        if (m != Current)
        {
            Current = m;
            if (Changed != null) Changed(null, EventArgs.Empty);
        }
    }

    /// <summary>把偏好解析成实际主题。</summary>
    private static ThemeMode Resolve(ThemePreference p)
    {
        if (p == ThemePreference.Light) return ThemeMode.Light;
        if (p == ThemePreference.Dark) return ThemeMode.Dark;
        return DetectSystemTheme();
    }

    private static ThemePreference LoadPreference()
    {
        try
        {
            Dictionary<string, string> s = ConfigStore.LoadSettings();
            string v = ConfigStore.GetString(s, PrefKey, "System");
            if (string.Equals(v, "Light", StringComparison.OrdinalIgnoreCase)) return ThemePreference.Light;
            if (string.Equals(v, "Dark", StringComparison.OrdinalIgnoreCase)) return ThemePreference.Dark;
        }
        catch { }
        return ThemePreference.System;
    }

    private static void SavePreference(ThemePreference p)
    {
        try
        {
            Dictionary<string, string> s = ConfigStore.LoadSettings();
            s[PrefKey] = PrefName(p);
            string msg;
            ConfigStore.SaveSettings(s, out msg);
        }
        catch (Exception ex)
        {
            Log.Warn("保存主题偏好失败: " + ex.Message);
        }
    }

    public static string PrefName(ThemePreference p)
    {
        if (p == ThemePreference.Light) return "Light";
        if (p == ThemePreference.Dark) return "Dark";
        return "System";
    }

    public static string PrefLabel(ThemePreference p)
    {
        if (p == ThemePreference.Light) return "固定浅色";
        if (p == ThemePreference.Dark) return "固定深色";
        return "跟随系统";
    }

        public static string Name(ThemeMode m)
        {
            return m == ThemeMode.Dark ? "深色" : "浅色";
        }

        public static string CurrentName
        {
            get { return Name(Current); }
        }

        /// <summary>读取 Windows 当前的浅色 / 深色设置。读不到时按浅色处理。</summary>
        public static ThemeMode DetectSystemTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v != null)
                        {
                            return Convert.ToInt32(v) == 0 ? ThemeMode.Dark : ThemeMode.Light;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取系统主题失败: " + ex.Message);
            }
            return ThemeMode.Light;
        }

        // ==================================================================
        // 色板
        // ==================================================================

        private static bool IsDark { get { return Current == ThemeMode.Dark; } }

        private static Color C(byte r, byte g, byte b)
        {
            return Color.FromRgb(r, g, b);
        }

        private static Color CA(byte r, byte g, byte b, byte a)
        {
            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>窗口底色（玻璃后面的"桌面"）。</summary>
        public static Color WindowBg
        {
            get { return IsDark ? C(0x0B, 0x0F, 0x17) : C(0xEA, 0xF0, 0xF7); }
        }

        /// <summary>外层玻璃面板（半透明）。</summary>
        public static Color GlassPanel
        {
            get { return IsDark ? CA(0xFF, 0xFF, 0xFF, 0x0C) : CA(0xFF, 0xFF, 0xFF, 0xA6); }
        }

        /// <summary>
        /// 卡片玻璃。
        ///
        /// ⚠️ 这里的透明度直接决定界面的层次感：太透会让卡片和背景糊成一片，
        /// 用户会抱怨"功能块看不出来"（浅色主题尤甚，因为背景本来就浅）。
        /// 浅色主题给到 0xE8，基本是实心卡片，才能和背景拉开。
        /// </summary>
        public static Color GlassCard
        {
            get { return IsDark ? CA(0xFF, 0xFF, 0xFF, 0x18) : CA(0xFF, 0xFF, 0xFF, 0xE8); }
        }

        /// <summary>
        /// 卡片描边。
        ///
        /// ⚠️ 曾经浅色主题用的是 **白色** 描边（0xFFFFFF），
        /// 而背景和卡片都是白的 —— 结果边框完全看不见，"框不明显"就是这么来的。
        /// 浅色主题必须用**深色**低透明描边，深色主题才用白色。
        /// </summary>
        public static Color GlassBorder
        {
            get { return IsDark ? CA(0xFF, 0xFF, 0xFF, 0x33) : CA(0x0F, 0x17, 0x2A, 0x24); }
        }

        /// <summary>分隔线。</summary>
        public static Color Divider
        {
            get { return IsDark ? CA(0xFF, 0xFF, 0xFF, 0x1A) : CA(0x1F, 0x29, 0x37, 0x1A); }
        }

        /// <summary>输入框/下拉框底色。</summary>
        public static Color FieldBg
        {
            get { return IsDark ? CA(0xFF, 0xFF, 0xFF, 0x14) : CA(0xFF, 0xFF, 0xFF, 0xB3); }
        }

        // ---------- 文字 ----------

        public static Color TextPrimary
        {
            get { return IsDark ? C(0xE8, 0xEE, 0xF7) : C(0x1F, 0x29, 0x37); }
        }

        public static Color TextMuted
        {
            get { return IsDark ? C(0x94, 0xA3, 0xB8) : C(0x6B, 0x72, 0x80); }
        }

        public static Color TextFaint
        {
            get { return IsDark ? C(0x64, 0x74, 0x8B) : C(0x9C, 0xA3, 0xAF); }
        }

        // ---------- 状态色 ----------

        public static Color Accent
        {
            get { return IsDark ? C(0x60, 0xA5, 0xFA) : C(0x3B, 0x82, 0xF6); }
        }

        public static Color Ok
        {
            get { return IsDark ? C(0x34, 0xD3, 0x99) : C(0x10, 0xB9, 0x81); }
        }

        public static Color Warn
        {
            get { return IsDark ? C(0xFB, 0xBF, 0x24) : C(0xF5, 0x9E, 0x0B); }
        }

        public static Color Err
        {
            get { return IsDark ? C(0xF8, 0x71, 0x71) : C(0xEF, 0x44, 0x44); }
        }

        public static Color Idle
        {
            get { return IsDark ? C(0x64, 0x74, 0x8B) : C(0x9C, 0xA3, 0xAF); }
        }

        // ---------- 背景光斑（磨砂玻璃后面的彩色光晕） ----------

        public static Color Glow1
        {
            get { return IsDark ? CA(0x3B, 0x82, 0xF6, 0x80) : CA(0x60, 0xA5, 0xFA, 0x8C); }
        }

        public static Color Glow2
        {
            get { return IsDark ? CA(0x8B, 0x5C, 0xF6, 0x73) : CA(0xA7, 0x8B, 0xFA, 0x80); }
        }

        public static Color Glow3
        {
            get { return IsDark ? CA(0x22, 0xD3, 0xEE, 0x4D) : CA(0x5E, 0xEA, 0xD4, 0x6B); }
        }

        // ---------- 便捷工厂 ----------

        public static SolidColorBrush Br(Color c) { return new SolidColorBrush(c); }

        public static SolidColorBrush BrA(Color c, byte alpha)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        /// <summary>用于按钮等元素：主色前景下的文字色。</summary>
        public static Color OnAccent
        {
            get { return IsDark ? C(0x0B, 0x0F, 0x17) : Colors.White; }
        }
    }
}
