using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 网络体检。8 项通用检查，适用于任何有线校园网 / 家庭宽带环境。
    ///
    /// 设计原则：
    ///   · 所有检查项基于**通用网络概念**（网卡载波、IP 配置、网关可达、DNS 解析、外网连通、
    ///     延迟、拨号状态、系统快照），不依赖任何特定学校的内网地址。
    ///   · 每项均可单独调用（target 参数复用已有步骤对象），便于流式刷新 UI。
    ///   · 探测失败一律 fail-open：宁可少报一个错，也不能因为探测异常误导用户。
    /// </summary>
    public static class HealthReport
    {
        /// <summary>检查外网连通性时使用的目标（公共 DNS，不涉及任何学校内网）。</summary>
        private const string ExternalProbeHost = "223.5.5.5";

        /// <summary>DNS 解析测试域名。</summary>
        private const string DnsProbeHost = "www.baidu.com";

        /// <summary>执行完整体检，返回纯文本报告。</summary>
        public static string Run(string accountName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========== 校园网助手 · 网络体检报告 ==========");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("账号: " + (string.IsNullOrEmpty(accountName) ? "(未填写)" : accountName));
            sb.AppendLine();

            RunCore(sb, accountName, null);
            return sb.ToString();
        }

        /// <summary>
        /// 流式体检：每完成一项就回调一次，便于 UI 逐步刷新。
        /// onStep 参数：(已完成项数, 总项数, 步骤对象)。
        /// </summary>
        public static void RunStreaming(string accountName, Action<int, int, HealthCheckStep> onStep)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========== 校园网助手 · 网络体检报告 ==========");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("账号: " + (string.IsNullOrEmpty(accountName) ? "(未填写)" : accountName));
            sb.AppendLine();

            RunCore(sb, accountName, onStep);
        }

        private static void RunCore(StringBuilder sb, string accountName, Action<int, int, HealthCheckStep> onStep)
        {
            const int Total = 8;
            int done = 0;

            bool wiredUp = IsWiredMediaConnected();
            string wiredDetail;
            IsWiredMediaConnected(out wiredDetail);

            done++; Emit(onStep, done, Total, CheckAdapter(sb, null, wiredUp, wiredDetail));
            done++; Emit(onStep, done, Total, CheckIpConfig(sb, null));
            done++; Emit(onStep, done, Total, CheckGateway(sb, null));
            done++; Emit(onStep, done, Total, CheckDns(sb, null));
            done++; Emit(onStep, done, Total, CheckInternet(sb, null));
            done++; Emit(onStep, done, Total, CheckLatency(sb, null));
            done++; Emit(onStep, done, Total, CheckDialState(sb, null, accountName));
            done++; Emit(onStep, done, Total, CheckSystemInfo(sb, null));
        }

        private static void Emit(Action<int, int, HealthCheckStep> onStep, int done, int total, HealthCheckStep step)
        {
            if (onStep != null) onStep(done, total, step);
        }

        // ------------------------------------------------------------------
        // [1] 网卡状态
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckAdapter(StringBuilder sb, HealthCheckStep target,
            bool wiredUp, string wiredDetail)
        {
            var s = target ?? new HealthCheckStep { Title = "[1] 有线网卡状态" };
            if (sb != null) sb.Append(s.Title + ": ");

            if (wiredUp)
            {
                s.Status = "OK";
                s.StatusBadge = "正常";
                s.Summary = "有线网卡已识别到载波（网线已插好）";
                s.Details.Add(wiredDetail);
            }
            else
            {
                s.Status = "ERROR";
                s.StatusBadge = "异常";
                s.Summary = "没有检测到可用的有线网络连接";
                s.Details.Add(wiredDetail);
                s.Details.Add("请检查：① 网线是否插紧 ② 宿舍网口是否有信号 ③ 网卡驱动是否正常");
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [2] IP 配置
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckIpConfig(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[2] IP 地址配置" };
            if (sb != null) sb.Append(s.Title + ": ");

            var infos = GetIpv4Infos();
            if (infos.Count == 0)
            {
                s.Status = "ERROR";
                s.StatusBadge = "异常";
                s.Summary = "没有获取到 IPv4 地址";
                s.Details.Add("若网络要求自动获取地址，请确认「自动获得 IP 地址」已开启");
            }
            else
            {
                bool anyValid = false;
                foreach (KeyValuePair<string, string> kv in infos)
                {
                    s.Details.Add(kv.Key + " → " + kv.Value);
                    if (!kv.Value.StartsWith("169.254.") && kv.Value != "0.0.0.0") anyValid = true;
                }

                if (anyValid)
                {
                    s.Status = "OK";
                    s.StatusBadge = "正常";
                    s.Summary = "已获取到有效 IP 地址";
                }
                else
                {
                    s.Status = "WARN";
                    s.StatusBadge = "注意";
                    s.Summary = "只拿到 169.254.x.x 自动私有地址，说明未从网络获取到 IP";
                    s.Details.Add("常见原因：网线未插好、网络侧未开启 DHCP、或需要手动配置静态 IP");
                }
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [3] 网关可达性
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckGateway(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[3] 默认网关可达性" };
            if (sb != null) sb.Append(s.Title + ": ");

            List<string> gateways = GetDefaultGateways();
            if (gateways.Count == 0)
            {
                s.Status = "WARN";
                s.StatusBadge = "注意";
                s.Summary = "没有找到默认网关（可能是拨号模式，或尚未获取网络配置）";
            }
            else
            {
                bool anyOk = false;
                foreach (string gw in gateways)
                {
                    bool ok = PingHost(gw, 1500);
                    s.Details.Add(gw + " → " + (ok ? "可达" : "不可达"));
                    if (ok) anyOk = true;
                }
                if (anyOk)
                {
                    s.Status = "OK";
                    s.StatusBadge = "正常";
                    s.Summary = "默认网关可以连通";
                }
                else
                {
                    s.Status = "ERROR";
                    s.StatusBadge = "异常";
                    s.Summary = "默认网关无法连通，本地网络这一段有问题";
                    s.Details.Add("请检查网线、网口，或尝试重新插拔后再体检");
                }
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [4] DNS 解析
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckDns(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[4] DNS 域名解析" };
            if (sb != null) sb.Append(s.Title + ": ");

            bool ok = false;
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(DnsProbeHost);
                ok = addrs != null && addrs.Length > 0;
                if (ok)
                {
                    s.Details.Add("解析 " + DnsProbeHost + " → " + addrs[0]);
                }
            }
            catch (Exception ex)
            {
                s.Details.Add("解析失败: " + ex.Message);
            }

            List<string> dnsServers = GetDnsServers();
            if (dnsServers.Count > 0)
            {
                s.Details.Add("当前 DNS: " + string.Join(", ", dnsServers.ToArray()));
            }
            else
            {
                s.Details.Add("当前 DNS: (未配置)");
            }

            if (ok)
            {
                s.Status = "OK";
                s.StatusBadge = "正常";
                s.Summary = "域名解析工作正常";
            }
            else
            {
                s.Status = "WARN";
                s.StatusBadge = "注意";
                s.Summary = "域名解析不成功，网页可能打不开（但聊天软件可能仍可用）";
                s.Details.Add("可尝试：把 DNS 改为 223.5.5.5 / 119.29.29.29，或断开重连");
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [5] 外网连通
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckInternet(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[5] 外网连通性" };
            if (sb != null) sb.Append(s.Title + ": ");

            bool ok = PingHost(ExternalProbeHost, 2500);
            s.Details.Add(ExternalProbeHost + " → " + (ok ? "可达" : "不可达"));

            if (ok)
            {
                s.Status = "OK";
                s.StatusBadge = "正常";
                s.Summary = "可以访问外网";
            }
            else
            {
                s.Status = "ERROR";
                s.StatusBadge = "异常";
                s.Summary = "访问不了外网，通常是尚未拨号上网或账号未认证";
                s.Details.Add("请确认已完成拨号 / 网页认证登录");
                s.Details.Add("部分网络会屏蔽 ping，若网页能打开则忽略此项");
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [6] 延迟测试
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckLatency(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[6] 网络延迟" };
            if (sb != null) sb.Append(s.Title + ": ");

            long rtt = PingRtt(ExternalProbeHost, 2500);
            if (rtt >= 0)
            {
                s.Details.Add("往返延迟: " + rtt + " ms");
                if (rtt < 60)
                {
                    s.Status = "OK"; s.StatusBadge = "优秀";
                    s.Summary = "延迟很低（" + rtt + " ms）";
                }
                else if (rtt < 150)
                {
                    s.Status = "OK"; s.StatusBadge = "正常";
                    s.Summary = "延迟正常（" + rtt + " ms）";
                }
                else
                {
                    s.Status = "WARN"; s.StatusBadge = "偏高";
                    s.Summary = "延迟偏高（" + rtt + " ms），游戏或视频通话可能卡顿";
                }
            }
            else
            {
                s.Status = "INFO";
                s.StatusBadge = "未测到";
                s.Summary = "无法测量延迟（目标不可达或被屏蔽）";
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [7] 拨号状态
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckDialState(StringBuilder sb, HealthCheckStep target, string accountName)
        {
            var s = target ?? new HealthCheckStep { Title = "[7] 宽带拨号状态" };
            if (sb != null) sb.Append(s.Title + ": ");

            string dialName = DialEngine.GetConnectedDialName();
            if (!string.IsNullOrEmpty(dialName))
            {
                s.Status = "OK";
                s.StatusBadge = "已连接";
                s.Summary = "宽带拨号处于已连接状态";
                s.Details.Add("拨号接口: " + dialName);
                string ip = DialEngine.GetDialIpAddress();
                if (!string.IsNullOrEmpty(ip)) s.Details.Add("拨号获得的地址: " + ip);
            }
            else
            {
                s.Status = "INFO";
                s.StatusBadge = "未拨号";
                s.Summary = "当前没有检测到已连接的拨号会话";
                if (!string.IsNullOrEmpty(accountName)) s.Details.Add("当前账号: " + accountName);
                s.Details.Add("若使用「网页认证」方式上网，此项显示未拨号属正常现象");
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ------------------------------------------------------------------
        // [8] 系统信息
        // ------------------------------------------------------------------

        private static HealthCheckStep CheckSystemInfo(StringBuilder sb, HealthCheckStep target)
        {
            var s = target ?? new HealthCheckStep { Title = "[8] 系统与网卡清单" };
            if (sb != null) sb.Append(s.Title + ": ");

            s.Status = "INFO";
            s.StatusBadge = "环境信息";
            s.Summary = "已收集本机网络环境信息";

            try
            {
                s.Details.Add("系统: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (64位)" : " (32位)"));
                int n = 0;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    n++;
                    if (n > 12) { s.Details.Add("... 其余网卡已省略"); break; }
                    string ip = "";
                    try
                    {
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                    s.Details.Add(ni.Name + " | " + ni.OperationalStatus + " | "
                        + (ip.Length > 0 ? ip : "无 IPv4") + " | " + ni.NetworkInterfaceType);
                }
            }
            catch (Exception ex)
            {
                s.Details.Add("枚举失败: " + ex.Message);
            }

            if (sb != null) sb.AppendLine(s.StatusBadge + " " + s.Summary);
            return s;
        }

        // ==================================================================
        // 基础探测工具
        // ==================================================================

        /// <summary>
        /// 判断有线网卡是否已接入（有载波）。
        /// 可靠依据：
        ///   · PNPDeviceID 前缀 —— `PCI\` / `USB\` 为真实硬件，`ROOT\` 为虚拟设备；
        ///   · .NET NetworkInterfaceType.Ethernet —— 不随系统语言变化，可排除 Wi-Fi；
        ///   · NetConnectionStatus —— 2=已连接(有载波)，7=媒体已断开(网线未插)。
        /// </summary>
        public static bool IsWiredMediaConnected()
        {
            string detail;
            return IsWiredMediaConnected(out detail);
        }

        /// <summary>同上，并输出判定依据。探测失败时 fail-open 返回 true。</summary>
        public static bool IsWiredMediaConnected(out string detail)
        {
            detail = "";
            try
            {
                Dictionary<string, NetworkInterfaceType> typeByName =
                    new Dictionary<string, NetworkInterfaceType>(StringComparer.OrdinalIgnoreCase);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != null && !typeByName.ContainsKey(ni.Name)) typeByName[ni.Name] = ni.NetworkInterfaceType;
                }

                bool sawWiredAdapter = false;
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT NetConnectionID, NetConnectionStatus, PNPDeviceID, Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"))
                {
                    foreach (var mo in searcher.Get())
                    {
                        string connId = mo["NetConnectionID"] as string ?? "";
                        string pnp = (mo["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                        string drvName = mo["Name"] as string ?? "";

                        uint status = 0;
                        try { status = Convert.ToUInt32(mo["NetConnectionStatus"]); } catch { }

                        if (!pnp.StartsWith("PCI\\") && !pnp.StartsWith("USB\\")) continue;

                        NetworkInterfaceType nit;
                        if (!typeByName.TryGetValue(connId, out nit)) continue;
                        if (nit != NetworkInterfaceType.Ethernet) continue;

                        sawWiredAdapter = true;
                        if (status == 2)
                        {
                            detail = connId + " / " + drvName + " 已连接";
                            return true;
                        }
                    }
                }
                detail = sawWiredAdapter ? "检测到有线网卡，但网线未插好或未接入" : "未找到有线物理网卡";
                return false;
            }
            catch
            {
                detail = "网卡探测失败，按已插入处理";
                return true;
            }
        }

        private static List<KeyValuePair<string, string>> GetIpv4Infos()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            list.Add(new KeyValuePair<string, string>(ni.Name, ua.Address.ToString()));
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<string> GetDefaultGateways()
        {
            var list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string a = gw.Address.ToString();
                            if (a != "0.0.0.0" && !list.Contains(a)) list.Add(a);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<string> GetDnsServers()
        {
            var list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string a = dns.ToString();
                            if (!list.Contains(a)) list.Add(a);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static bool PingHost(string host, int timeoutMs)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(host, timeoutMs);
                    return r != null && r.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        private static long PingRtt(string host, int timeoutMs)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(host, timeoutMs);
                    if (r != null && r.Status == IPStatus.Success) return r.RoundtripTime;
                }
            }
            catch { }
            return -1;
        }

        // ==================================================================
        // 诊断包导出
        // ==================================================================

        /// <summary>
        /// 生成诊断包（体检报告 + 日志 + 系统信息）到桌面 zip，返回完整路径。
        /// 诊断包**不含任何密码**，仅体检结论与网络环境快照，可安全外发求助。
        /// </summary>
        public static string ExportDiagPackage(string healthReportText)
        {
            string appData = ConfigStore.AppDataDir;
            string tmpDir = Path.Combine(Path.GetTempPath(), "CampusNetDiag_" + DateTime.Now.ToString("HHmmss"));

            try
            {
                Directory.CreateDirectory(tmpDir);

                File.WriteAllText(Path.Combine(tmpDir, "health_report.txt"),
                    healthReportText ?? "(未提供体检报告)", Encoding.UTF8);

                string logDir = Path.Combine(appData, "logs");
                if (Directory.Exists(logDir))
                {
                    foreach (string f in Directory.GetFiles(logDir, "*.log"))
                    {
                        File.Copy(f, Path.Combine(tmpDir, "logs_" + Path.GetFileName(f)), true);
                    }
                }

                File.WriteAllText(Path.Combine(tmpDir, "system_info.txt"), BuildSystemInfo(), Encoding.UTF8);

                File.WriteAllText(Path.Combine(tmpDir, "README.txt"),
                    "校园网助手 诊断包 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n"
                    + "· health_report.txt  网络体检报告\r\n"
                    + "· logs_*.log         运行日志\r\n"
                    + "· system_info.txt    系统与网卡快照\r\n"
                    + "本包不含任何密码，可安全外发用于求助。\r\n"
                    + "（注意：日志里会包含连接名、账号显示名和内网地址，请发给信任的人，不要公开贴出。）\r\n",
                    Encoding.UTF8);

                string zipPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "校园网诊断包_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tmpDir, zipPath);
                return zipPath;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private static string BuildSystemInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("OS: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (x64)" : ""));
            sb.AppendLine("程序路径: " + System.Reflection.Assembly.GetExecutingAssembly().Location);
            sb.AppendLine();
            sb.AppendLine("== 网络适配器 ==");
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string ip = "";
                    try
                    {
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ip = ua.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch { }
                    sb.AppendLine(ni.Name + " | " + ni.OperationalStatus + " | " + ip + " | " + ni.Description);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("适配器枚举失败: " + ex.Message);
            }
            return sb.ToString();
        }
    }
}
