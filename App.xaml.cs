using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace CampusNetHelper
{
    public class App : Application
    {
        [System.STAThread]
        static void Main(string[] args)
        {
            // 提权子进程：由非提权父进程用 RunAs 拉起，只装/卸计划任务，干完即退、不建 UI。
            // 必须放在单实例保护之前 —— 父进程正持有 Mutex，否则子进程会被当成二次启动拦截。
            if (args != null)
            {
                foreach (string arg in args)
                {
                    bool doInstall = arg.Equals(AutostartHelper.InstallArg, StringComparison.OrdinalIgnoreCase);
                    bool doUninstall = arg.Equals(AutostartHelper.UninstallArg, StringComparison.OrdinalIgnoreCase);
                    if (!doInstall && !doUninstall) continue;

                    string resultMsg;
                    bool ok = doInstall
                        ? AutostartHelper.Install(out resultMsg)
                        : AutostartHelper.Uninstall(out resultMsg);
                    AutostartHelper.WriteResult(ok, resultMsg);
                    Log.Info((doInstall ? "提权安装开机自启" : "提权卸载开机自启")
                        + (ok ? "成功: " : "失败: ") + resultMsg);
                    return;
                }
            }

            // 界面自测：跑一遍"模拟用户点击 + 打字"的用例，把结果写文件后退出。
            // 放在单实例保护**之前** —— 自测要能在程序正跑着的时候独立运行。
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                    {
                        // ⚠️ 先关掉文件日志 —— 自测会把网络质量探测、心跳、网速采样
                        //    这些后台子系统真的跑起来，它们会往用户当天的正式日志里灌
                        //    一堆"测试痕迹"。自测结果本身写到 %TEMP% 的独立文件里，
                        //    不依赖日志。（2026-09-30 冒烟测试发现：光"自测结果已写入"
                        //    一天就写了 24 条，把用户自己的运行记录挤掉了。）
                        Log.SelfTestQuiet = true;

                        string text = UiTest.Run();
                        string path = Path.Combine(Path.GetTempPath(), "campusnet-selftest.txt");
                        try { File.WriteAllText(path, text, Encoding.UTF8); } catch { }
                        return;
                    }
                }
            }

            // 单实例保护：多实例并存会导致托盘图标重复、日志交错。
            if (!AcquireSingleInstance())
            {
                if (TryActivateExistingInstance())
                {
                    Log.Info("单实例拦截：已调出已运行实例主窗口，本实例安静退出");
                    return;
                }

                MessageBox.Show(
                    "程序已经在运行了。\n\n如果没看到窗口，请查看屏幕右下角系统托盘区域的图标。",
                    "校园网助手", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var app = new App();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                Log.Error("UI 线程未捕获异常", e.Exception);
                MessageBox.Show("程序发生异常: " + e.Exception.Message
                    + "\n\n详细信息已记录到日志目录。", "校园网助手",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    Exception ex = e.ExceptionObject as Exception;
                    Log.Error("AppDomain 全局未捕获异常", ex);
                }
                catch { }
            };

            // 主题：读取系统浅色/深色设置并开始监听变化
            Theme.Init();

            // 判断是否静默启动：带 /silent 参数，或从启动文件夹运行且设置为静默
            bool silent = false;
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg.Equals("/silent", StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("-silent", StringComparison.OrdinalIgnoreCase)
                        || arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        silent = true;
                        break;
                    }
                }
            }

            if (!silent && IsStartedFromStartupFolder())
            {
                silent = IsSilentSetting();
            }

            var mainWindow = new MainWindow(silent);
            app.MainWindow = mainWindow;

            if (!silent)
            {
                mainWindow.Show();
            }
            else
            {
                // 静默模式不 Show()，但需确保句柄存在，否则二次启动时找不到本程序窗口。
                try
                {
                    new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();
                }
                catch (Exception ex)
                {
                    Log.Warn("静默模式确保主窗口句柄失败: " + ex.Message);
                }
            }

            // 网页认证模式下，开机自启要**弹出认证窗口**而不是静默拨号。
            //
            // 为什么必须分流：网页认证要人填图形验证码，程序没法静默完成 ——
            // 与其让用户开机后自己找托盘、点菜单，不如直接把认证页怼到脸上。
            // 拨号模式则维持原来的静默行为，一个字都不改。
            if (silent && mainWindow.IsPortalMode())
            {
                if (mainWindow.InNightQuiet())
                {
                    // 夜间免打扰时段内不弹窗，避免半夜重启电脑被弹窗打扰。
                    // 次日的常规起点（tick）会跳过拨号逻辑，用户需要时自己点托盘即可。
                    Log.Info("网页认证模式：当前处于夜间免打扰时段，不弹出认证窗口");
                }
                else
                {
                    Log.Info("网页认证模式：开机自启弹出网页认证窗口"
                        + "（行为=" + mainWindow.PortalStartupBehavior() + "）");
                    mainWindow.OpenWebAuthWindowOnStartup();
                }
            }

            Log.Info("程序启动 (silent=" + silent
                + ", elevated=" + AutostartHelper.IsProcessElevated()
                + ", theme=" + Theme.CurrentName
                + ", 自启=" + (AutostartHelper.IsTaskInstalled() ? "计划任务" : "无") + ")");

            app.Run();
        }

        private static System.Threading.Mutex singleMutex;

        /// <summary>获取单实例所有权（Mutex + 同名进程双检测）。</summary>
        private static bool AcquireSingleInstance()
        {
            bool createdNew;
            System.Threading.Mutex m = new System.Threading.Mutex(
                true, "Local\\CampusNetHelper_SingleInstance", out createdNew);
            if (createdNew)
            {
                if (!HasSiblingProcess())
                {
                    singleMutex = m;
                    return true;
                }
                try { m.ReleaseMutex(); } catch { }
                try { m.Dispose(); } catch { }
            }
            return false;
        }

        private static bool HasSiblingProcess()
        {
            try
            {
                int selfId = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    if (p.Id == selfId) continue;
                    string name = (p.ProcessName ?? "").ToLower();
                    if (name == "campusnethelper") return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>当前是否从「启动」文件夹路径运行。</summary>
        private static bool IsStartedFromStartupFolder()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string startupDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup");
                return exePath != null && exePath.StartsWith(startupDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>读取设置里的 Silent 开关。</summary>
        private static bool IsSilentSetting()
        {
            try
            {
                var dict = ConfigStore.LoadSettings();
                return ConfigStore.GetBool(dict, "Silent", false);
            }
            catch { }
            return false;
        }

        // ============ 单实例体验优化：激活已运行实例 ============

        private static bool TryActivateExistingInstance()
        {
            try
            {
                List<IntPtr> found = new List<IntPtr>();
                WinApi.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    StringBuilder sb = new StringBuilder(256);
                    WinApi.GetClassName(hWnd, sb, 256);
                    string cls = sb.ToString();
                    // WPF 顶层窗口类名格式: HwndWrapper[AssemblyName;;GUID]
                    if (cls.StartsWith("HwndWrapper[CampusNetHelper", StringComparison.Ordinal))
                    {
                        found.Add(hWnd);
                    }
                    return true;
                }, IntPtr.Zero);

                if (found.Count == 0) return false;

                uint activateMsg = WinApi.RegisterWindowMessage("CampusNetHelper_Activate");
                foreach (IntPtr hWnd in found)
                {
                    WinApi.ShowWindow(hWnd, WinApi.SW_SHOW);
                    WinApi.SetForegroundWindow(hWnd);
                    if (activateMsg != 0) WinApi.PostMessage(hWnd, activateMsg, IntPtr.Zero, IntPtr.Zero);
                }

                // ShowWindow / PostMessage 被 UIPI 拦截时不会报错，只能靠回查窗口可见性验证。
                for (int i = 0; i < 10; i++)
                {
                    foreach (IntPtr hWnd in found)
                    {
                        if (WinApi.IsWindowVisible(hWnd)) return true;
                    }
                    System.Threading.Thread.Sleep(100);
                }
                Log.Warn("单实例拦截：发出激活消息但 1 秒内窗口未变为可见");
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>单实例激活所需的 Win32 互操作。</summary>
    internal static class WinApi
    {
        public const int SW_SHOW = 5;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);
    }
}
