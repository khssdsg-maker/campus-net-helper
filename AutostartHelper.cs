using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 提权开机自启（计划任务方案）。
    ///
    /// **为什么需要**：重启网卡、写入系统级网络配置等操作需要管理员权限，
    /// 而从启动文件夹或桌面快捷方式启动的进程是非提权的，相关功能不会真正生效。
    ///
    /// **为什么用计划任务**：
    ///   · exe 清单 requireAdministrator → 每次启动都弹 UAC，不可接受；
    ///   · 启动文件夹里的 .lnk 勾"以管理员身份运行" → 每次登录都弹 UAC，同样不可接受；
    ///   · 计划任务 /RL HIGHEST → 只有**创建时**需要一次提权，之后每次登录静默以管理员运行。
    ///
    /// ⚠️ **与启动文件夹副本互斥**：两者并存会在登录时启动第二个实例，导致重复弹窗。
    /// 故 Install 时必须删除启动文件夹副本。
    ///
    /// 所有成败判定一律依据子进程**退出码**——schtasks 与 netsh 的输出都随系统语言本地化，
    /// 解析文本在中文 Windows 上不可靠。
    /// </summary>
    public static class AutostartHelper
    {
        /// <summary>计划任务名。</summary>
        public const string TaskName = "CampusNetHelper Logon";

        /// <summary>提权子进程参数：装完即退，不建 UI。</summary>
        public const string InstallArg = "/install-autostart";

        /// <summary>提权子进程参数：卸完即退，不建 UI。</summary>
        public const string UninstallArg = "/uninstall-autostart";

        private const string ResultFileName = "autostart_result.txt";

        public static string AppDataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusNetHelper");
            }
        }

        /// <summary>提权子进程把结果写到这里，非提权父进程读它来汇报（避免弹第二个对话框）。</summary>
        public static string ResultFilePath
        {
            get { return Path.Combine(AppDataDir, ResultFileName); }
        }

        public static string CurrentExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static string StartupCopyPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup",
                    "CampusNetHelper.exe");
            }
        }

        public static bool IsProcessElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static bool StartupCopyExists()
        {
            try { return File.Exists(StartupCopyPath); }
            catch { return false; }
        }

        private const int TaskStateCacheSeconds = 30;
        private static bool taskStateKnown = false;
        private static bool taskStateCached = false;
        private static DateTime taskStateProbed = DateTime.MinValue;

        /// <summary>
        /// 计划任务是否已安装。依据 `schtasks /Query /TN` 的退出码（存在=0，不存在≠0）。
        /// 带 30s TTL 缓存：每次真查都要 spawn 一个 schtasks 进程(约 100-300ms)，
        /// 而 SaveSettings() 在每次点开关时都会走到这里，不缓存会让 UI 发卡。
        /// </summary>
        public static bool IsTaskInstalled()
        {
            if (taskStateKnown && (DateTime.Now - taskStateProbed).TotalSeconds < TaskStateCacheSeconds)
                return taskStateCached;

            string output;
            int code = RunSchtasks("/Query /TN \"" + TaskName + "\"", out output);
            taskStateCached = (code == 0);
            taskStateProbed = DateTime.Now;
            taskStateKnown = true;
            return taskStateCached;
        }

        /// <summary>任务状态可能被外部改变（用户在任务计划程序里手动删了）时强制下次真查。</summary>
        public static void InvalidateTaskState()
        {
            taskStateKnown = false;
        }

        /// <summary>
        /// 取任务的关键配置供诊断展示（RunLevel / 触发器 / 动作）。读不到时返回说明文字，不抛异常。
        /// </summary>
        public static string GetTaskDetail()
        {
            string xml;
            int code = RunSchtasks("/Query /TN \"" + TaskName + "\" /XML", out xml);
            if (code != 0 || string.IsNullOrEmpty(xml)) return "(查询失败，退出码 " + code + ")";

            string runLevel = TagValue(xml, "RunLevel");
            string command = TagValue(xml, "Command");
            string arguments = TagValue(xml, "Arguments");
            string trigger = xml.IndexOf("<LogonTrigger", StringComparison.Ordinal) >= 0 ? "登录时" : "(非登录触发)";
            return "触发=" + trigger + " · RunLevel=" + (runLevel == "" ? "(未取到)" : runLevel)
                + " · 动作=" + (command == "" ? "(未取到)" : command)
                + (arguments == "" ? "" : " " + arguments);
        }

        /// <summary>
        /// 安装提权自启：建计划任务 → 校验 → 删除 Startup 副本（避免登录时双实例弹框）。
        /// 必须在提权进程中调用。
        /// </summary>
        public static bool Install(out string message)
        {
            message = "";
            if (!IsProcessElevated())
            {
                message = "当前进程非管理员，无法创建计划任务";
                return false;
            }

            string exe = CurrentExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                message = "无法定位自身 exe 路径: " + (exe ?? "(空)");
                return false;
            }

            // 前置守卫：若当前进程就是从 Startup 副本启动的，那份 exe 是正在运行的映像、
            // Windows 不允许删除，任务建好后旧副本删不掉 → 登录时仍会双实例弹框，等于装了一半。
            // 与其留下半成品状态，不如在创建任务之前就拒绝并给出可操作的指引。
            if (string.Equals(exe, StartupCopyPath, StringComparison.OrdinalIgnoreCase))
            {
                message = "当前进程正是从 Startup 副本启动的，该文件正被占用无法删除，装了计划任务也会导致登录时双实例弹框。"
                    + "请先从托盘退出程序，改由桌面快捷方式（或直接双击安装目录里的 exe）打开后再点一次本按钮。";
                return false;
            }

            // /TR 的值本身要加引号，其中 exe 路径的引号需转义成 \" 才能被 schtasks 原样存下
            string tr = "\"\\\"" + exe + "\\\" /silent\"";
            string args = "/Create /TN \"" + TaskName + "\" /TR " + tr + " /SC ONLOGON /RL HIGHEST /F";

            string output;
            int code = RunSchtasks(args, out output);
            if (code != 0)
            {
                message = "schtasks /Create 失败，退出码 " + code;
                return false;
            }

            // 不轻信退出码，回查一次确认任务真的在了（缓存会读到操作前的旧值，必须先失效）
            InvalidateTaskState();
            if (!IsTaskInstalled())
            {
                message = "schtasks 返回成功但回查不到任务";
                return false;
            }

            // ⚠️ schtasks /Create 的默认 ExecutionTimeLimit 是 **PT72H** —— 任务会在 72 小时后
            // 强制终止程序，对 7x24 常驻的网络守护等于每 3 天静默死一次。必须改掉；
            // 改不掉就回滚删除刚建的任务，不留"装了一半且会自杀"的状态。
            string fixMsg;
            if (!FixTaskSettings(out fixMsg))
            {
                string delOut;
                RunSchtasks("/Delete /TN \"" + TaskName + "\" /F", out delOut);
                InvalidateTaskState();
                message = "已回滚（删除刚创建的计划任务），未留下会 72 小时自杀的半成品。原因：" + fixMsg;
                return false;
            }

            string rmMsg;
            if (StartupCopyExists() && !RemoveStartupCopy(out rmMsg))
            {
                // 任务已建好，只是旧副本没删掉 —— 这会导致登录时双实例，必须报失败让用户处理
                message = "计划任务已创建，但删除 Startup 副本失败（会导致登录时双实例弹框）: " + rmMsg;
                return false;
            }

            message = "已创建计划任务「" + TaskName + "」，指向 " + exe + " /silent；" + fixMsg + "；"
                + (StartupCopyExists() ? "" : "Startup 副本已移除，")
                + "下次登录起静默以管理员身份运行。" + GetTaskDetail();
            return true;
        }

        /// <summary>
        /// 卸载提权自启：删计划任务 → 恢复 Startup 副本，保证降级后仍有（非提权的）开机自启，
        /// 不会让用户直接失去自启能力。必须在提权进程中调用。
        /// </summary>
        public static bool Uninstall(out string message)
        {
            message = "";
            if (!IsProcessElevated())
            {
                message = "当前进程非管理员，无法删除计划任务";
                return false;
            }

            string output;
            int code = RunSchtasks("/Delete /TN \"" + TaskName + "\" /F", out output);
            InvalidateTaskState();
            if (code != 0 && IsTaskInstalled())
            {
                message = "schtasks /Delete 失败，退出码 " + code;
                return false;
            }

            string restoreMsg;
            bool restored = RestoreStartupCopy(out restoreMsg);
            message = "已删除计划任务「" + TaskName + "」。"
                + (restored ? "已恢复 Startup 副本（下次登录起为普通权限自启，防火墙类功能将不再生效）: " + restoreMsg
                            : "⚠ 恢复 Startup 副本失败，开机自启已丢失，请手动处理: " + restoreMsg);
            return restored;
        }

        /// <summary>
        /// 修正 `schtasks /Create` 留下的危险默认值。
        ///
        /// ⚠️ 最关键的是 **ExecutionTimeLimit 默认 PT72H**：计划任务会在 72 小时后**强制终止**程序。
        /// 对 7x24 常驻的网络守护来说，等于每 3 天静默死一次（2026-09-12 实测确认默认值就是 PT72H）。
        /// `schtasks.exe` 没有关闭该限制的开关（`/ET` 是每日结束时刻，不是时长），
        /// 只能走 Task Scheduler 的 COM 接口。
        /// </summary>
        private static bool FixTaskSettings(out string message)
        {
            // 首选：原生 COM（进程内调用，不起子进程）。实测重注册一次约 16ms，
            // 而起 powershell.exe 光是冷启动就要数百毫秒 —— 差一个数量级。
            string nativeMsg;
            if (FixTaskSettingsNative(out nativeMsg))
            {
                message = nativeMsg;
                return true;
            }

            // 回落：旧的 PowerShell 实现。**只在 COM 不可用时才走到这里**，
            // 保留它是为了"退化回原来能用的行为"，不是重新依赖 PowerShell。
            string psMsg;
            if (FixTaskSettingsByPowerShell(out psMsg))
            {
                message = psMsg + "（原生 COM 未成功：" + nativeMsg + "）";
                return true;
            }

            message = "修正计划任务设置失败。原生 COM: " + nativeMsg + "；PowerShell 回落: " + psMsg;
            return false;
        }

        // ================= 原生 COM 访问（替代起 powershell.exe 子进程）=================
        //
        // 2026-10-01 实测踩坑，两条都很难从报错里看出来：
        //
        // ① **不能用 `null` 占位**：`root.RegisterTaskDefinition(a, b, c, d, null, e, null)`
        //    会抛 `ArgumentException: 未能将调用的参数 5 转换为 RegisterTaskDefinition`。
        //    必须传 `Type.Missing`。`password` 与 `sddl` 两个位置都是如此。
        // ② **definition 实参必须是真正的 ITaskDefinition 对象**：传 XML 字符串会得到
        //    `COMException 0x80020005 (DISP_E_TYPEMISMATCH)`。
        //    → 从 `root.GetTask(name).Definition` 拿，或用 `svc.NewTask(0)` 新建。
        //
        // 传 `Type.Missing` 占位是 COM 晚绑定的既定用法（DISP_E_PARAMNOTFOUND 等价于"没给"），
        // 这里单独抽成常量，免得以后又被"顺手改成 null"。

        private const int TaskCreateOrUpdate = 6;

        /// <summary>COM 可选参数的"未提供"占位符。⚠️ 绝不要换成 `null`，见上方说明。</summary>
        private static readonly object ComMissing = Type.Missing;

        /// <summary>连到任务计划服务并取回根目录 `\` 的 ITaskFolder。失败直接抛，由调用方兜。</summary>
        private static dynamic OpenTaskFolder()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) throw new InvalidOperationException("取不到 ProgID Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc.GetFolder("\\");
        }

        /// <summary>原生 COM 改计划任务设置。成功返回 true；任何异常都在内部兜住并写进 message。</summary>
        private static bool FixTaskSettingsNative(out string message)
        {
            try
            {
                dynamic folder = OpenTaskFolder();
                dynamic def = folder.GetTask(TaskName).Definition;

                def.Settings.ExecutionTimeLimit = "PT0S";   // PT0S = 无限制
                def.Settings.MultipleInstances = 2;         // TASK_INSTANCES_IGNORE_NEW
                def.Settings.StartWhenAvailable = true;

                // 回填原有的账户与登录方式：重新注册会整体覆盖任务定义，
                // 不带上这两项就会把运行身份改掉（本机实测该值为 InteractiveToken / 当前用户）。
                string userId = Convert.ToString(def.Principal.UserId);
                if (string.IsNullOrEmpty(userId)) userId = null;
                int logonType = Convert.ToInt32(def.Principal.LogonType);

                folder.RegisterTaskDefinition(TaskName, def, TaskCreateOrUpdate,
                    userId == null ? ComMissing : (object)userId,
                    ComMissing, logonType, ComMissing);

                message = "ExecutionTimeLimit 已设为 PT0S(无限制)［原生 COM］";
                return true;
            }
            catch (Exception ex)
            {
                message = "原生 COM 改计划任务失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>原生 COM 只读一次 ExecutionTimeLimit。读不到返回 ""（不起子进程）。</summary>
        private static string GetTaskExecutionTimeLimitNative()
        {
            try
            {
                dynamic folder = OpenTaskFolder();
                object v = folder.GetTask(TaskName).Definition.Settings.ExecutionTimeLimit;
                return v == null ? "" : Convert.ToString(v).Trim();
            }
            catch { return ""; }
        }

        // ================= 以下为回落实现（仅在原生 COM 不可用时才会执行）=================

        /// <summary>旧的 PowerShell 实现，作为原生 COM 失败时的回落。</summary>
        private static bool FixTaskSettingsByPowerShell(out string message)
        {
            string cmd = "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) "
                + "-MultipleInstances IgnoreNew -StartWhenAvailable; "
                + "Set-ScheduledTask -TaskName '" + TaskName + "' -Settings $s | Out-Null; "
                + "Write-Output 'DONE'";

            string output;
            int code = RunProcess("powershell",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"", out output);

            string limit = GetTaskExecutionTimeLimit();
            if (limit == "PT0S")
            {
                message = "ExecutionTimeLimit 已设为 PT0S(无限制)［原 PowerShell 路径］";
                return true;
            }
            message = "ExecutionTimeLimit 仍为 " + (limit == "" ? "(取不到)" : limit)
                + "，powershell 退出码 " + code + "。该值默认 PT72H 会在 72 小时后强制终止程序，必须修掉。输出: " + output;
            return false;
        }

        /// <summary>
        /// 查任务的 ExecutionTimeLimit。
        /// 先用原生 COM（只读、不起子进程）；COM 读不到时才回落 PowerShell。
        /// 返回值恒为 `PT0S` / `PT72H` 这类 ASCII 值，避开 `schtasks /Query /XML`
        /// 的 UTF-16 输出与 .NET 解码不一致问题。
        /// </summary>
        public static string GetTaskExecutionTimeLimit()
        {
            string native = GetTaskExecutionTimeLimitNative();
            if (native.Length > 0) return native;

            string output;
            int code = RunProcess("powershell",
                "-NoProfile -ExecutionPolicy Bypass -Command \"(Get-ScheduledTask -TaskName '" + TaskName
                + "' -ErrorAction SilentlyContinue).Settings.ExecutionTimeLimit\"", out output);
            if (code != 0) return "";
            return (output ?? "").Trim();
        }

        /// <summary>
        /// 把自身复制到「启动」文件夹，实现**非提权**的开机自启。已有副本则视为成功（幂等）。
        ///
        /// 与计划任务的分工：
        ///   · 本方法 = 普通用户档。登录时以普通权限静默启动，够日常上网用（不需要任何提权）；
        ///   · 计划任务 = 需要改防火墙 / 重启网卡等提权功能时才装。
        /// 两者**必须互斥** —— 并存会在登录时拉起两个实例、弹两次框，调用方负责把关。
        /// </summary>
        public static bool EnsureStartupCopy(out string message)
        {
            if (StartupCopyExists())
            {
                message = "启动副本已存在，无需重复创建";
                return true;
            }
            return RestoreStartupCopy(out message);
        }

        public static bool RemoveStartupCopy(out string message)
        {
            message = "";
            try
            {
                string p = StartupCopyPath;
                if (!File.Exists(p)) { message = "Startup 副本本就不存在"; return true; }
                File.Delete(p);
                bool gone = !File.Exists(p);
                message = gone ? "已删除 " + p : "删除后仍存在: " + p;
                return gone;
            }
            catch (Exception ex)
            {
                message = "删除 Startup 副本异常: " + ex.Message;
                return false;
            }
        }

        public static bool RestoreStartupCopy(out string message)
        {
            message = "";
            try
            {
                string src = CurrentExePath;
                string dst = StartupCopyPath;
                if (!File.Exists(src)) { message = "源 exe 不存在: " + src; return false; }
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    message = "自身就在 Startup 目录，无需复制";
                    return true;
                }
                File.Copy(src, dst, true);
                bool ok = File.Exists(dst);
                message = ok ? "已复制 " + src + " → " + dst : "复制后目标不存在";
                return ok;
            }
            catch (Exception ex)
            {
                message = "恢复 Startup 副本异常: " + ex.Message;
                return false;
            }
        }

        /// <summary>提权子进程写结果；父进程读完即删，避免下次读到旧结果。</summary>
        public static void WriteResult(bool ok, string message)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                string path = ResultFilePath;
                if (File.Exists(path)) File.Delete(path);
                // 原子写：父进程会立刻读这个文件，别让它读到半截
                SafeFile.WriteAtomic(path, (ok ? "OK" : "FAIL") + "\r\n" + (message ?? ""),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>读取并删除结果文件。返回 false 表示没有结果（子进程可能没跑起来）。</summary>
        public static bool TryReadResult(out bool ok, out string message)
        {
            ok = false;
            message = "";
            try
            {
                string path = ResultFilePath;
                if (!File.Exists(path)) return false;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                try { File.Delete(path); } catch { }
                if (lines.Length == 0) return false;
                ok = lines[0].Trim() == "OK";
                message = lines.Length > 1 ? lines[1] : "";
                return true;
            }
            catch { return false; }
        }

        /// <summary>父进程调用前先清掉可能残留的旧结果，避免误读上一次的结论。</summary>
        public static void ClearResult()
        {
            try
            {
                string path = ResultFilePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static int RunSchtasks(string args, out string output)
        {
            return RunProcess("schtasks", args, out output);
        }

        /// <summary>
        /// 跑一个子进程并按**退出码**判定成败。
        /// 输出只用于诊断记录，绝不用于判定——schtasks 与 netsh 的输出都随系统语言本地化，
        /// 且中文 Windows 上还存在 GBK 与 .NET 解码不一致的问题（本会话已两次踩坑）。
        /// </summary>
        private static int RunProcess(string fileName, string args, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) { output = "(进程启动失败)"; return -1; }

                    // ⚠️ 改成异步读（2026-10-01 顺手加固）：
                    //    原来是顺序 ReadToEnd(StdOut) → ReadToEnd(StdErr)，
                    //    子进程输出超过管道缓冲区（约 4KB）时可能互相死锁 ——
                    //    它在写 stderr 时写满阻塞，而我们在等 stdout 的 EOF，两边一起等。
                    //    schtasks 输出很小、现实中碰不到，但这是个标准坑，顺手堵上。
                    StringBuilder so = new StringBuilder();
                    StringBuilder se = new StringBuilder();
                    proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) so.AppendLine(e.Data);
                    };
                    proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) se.AppendLine(e.Data);
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        try { proc.WaitForExit(3000); } catch { }
                        output = "(超时 20s 已终止)";
                        return -2;
                    }

                    // ⚠️ WaitForExit(int) **不保证**异步读的回调已经跑完，
                    //    必须再调一次无参的 WaitForExit() 才算收齐输出。
                    try { proc.WaitForExit(); } catch { }

                    output = (so.ToString() + se.ToString()).Trim();
                    return proc.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = "(异常: " + ex.Message + ")";
                return -3;
            }
        }

        /// <summary>从 XML 里取第一个指定元素的文本值，取不到返回空串。避免为一个值引入 XML 依赖。</summary>
        private static string TagValue(string xml, string tag)
        {
            try
            {
                string open = "<" + tag + ">";
                string close = "</" + tag + ">";
                int i = xml.IndexOf(open, StringComparison.Ordinal);
                if (i < 0) return "";
                i += open.Length;
                int j = xml.IndexOf(close, i, StringComparison.Ordinal);
                if (j < 0) return "";
                return xml.Substring(i, j - i).Trim();
            }
            catch { return ""; }
        }
    }
}
