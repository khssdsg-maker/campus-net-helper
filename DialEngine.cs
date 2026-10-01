using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 拨号引擎：基于 Windows 自带 rasdial 的 PPPoE 宽带拨号。
    ///
    /// 设计要点：
    ///   · 不解析 rasdial 的本地化输出文本（中文/英文 Windows 文案不同），成败一律看**退出码**。
    ///   · 电话簿（.pbk）按 UTF-16 LE 写入，凭据随条目保留，在系统的「网络和共享中心」里
    ///     也能看到这个连接。
    ///   · 支持直接使用本机已存在的宽带连接（例如学校官方客户端创建的那个）。
    /// </summary>
    public static class DialEngine
    {
        /// <summary>
        /// 电话簿路径 —— 刻意使用 **Windows 系统电话簿**（与「网络和共享中心」同一个文件）。
        ///
        /// 这样带来三个好处：
        ///   ① rasdial.exe 默认就读这个文件，创建完立即可拨；
        ///   ② 系统「网络和共享中心」里能看到同一个连接，两边始终一致；
        ///   ③ 学校官方客户端建的连接也能被本工具读到并直接使用。
        /// </summary>
        public static string PhonebookPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Network\Connections\Pbk\rasphone.pbk");
            }
        }

        public static string PhonebookDir
        {
            get { return Path.GetDirectoryName(PhonebookPath); }
        }

        // ==================================================================
        // 电话簿编码处理
        //
        // rasphone.pbk 的编码并不统一：微软文档写的是 UTF-16，但实测本机这份是
        // UTF-8（文件头就是 `[GUI` 两个 ASCII 字节）。不同 Windows 版本 / 不同程序
        // 写出来的格式可能不同，所以读取时统一做一次探测，写入时沿用原文件编码。
        // ==================================================================

        private static Encoding DetectEncoding(byte[] raw)
        {
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return Encoding.Unicode;
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) return Encoding.BigEndianUnicode;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                return new UTF8Encoding(true);
            }
            return new UTF8Encoding(false);
        }

        /// <summary>读出电话簿全文（自动识别编码）。文件不存在返回空串。</summary>
        private static string ReadPhonebook()
        {
            try
            {
                string p = PhonebookPath;
                if (!File.Exists(p)) return "";
                byte[] raw = File.ReadAllBytes(p);
                if (raw.Length == 0) return "";
                return DetectEncoding(raw).GetString(raw, 0, raw.Length).TrimStart('\uFEFF');
            }
            catch (Exception ex)
            {
                Log.Warn("读取电话簿失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 写回电话簿全文（沿用原文件编码；文件不存在则用 UTF-8 无 BOM）。
        ///
        /// ⚠️ 这个文件是**系统级的** —— rasphone.pbk 里装着这台机器上所有的宽带连接，
        /// 不只是本程序建的。写坏了别的东西也一起坏。
        /// 所以这里必须用 SafeFile：先留一份 .bak（万一出事能对照/恢复），
        /// 再走"写临时文件 → 整体替换"的原子写 —— 不会再出现"清空之后写了一半"的半截文件。
        /// （2026-09-29 自查时发现：原来用的是 File.WriteAllText，和把账号写丢的是同一套危险写法。）
        /// </summary>
        private static bool WritePhonebook(string text, out string message)
        {
            message = "";
            try
            {
                string p = PhonebookPath;
                Directory.CreateDirectory(Path.GetDirectoryName(p));

                Encoding enc = new UTF8Encoding(false);
                if (File.Exists(p))
                {
                    byte[] old = File.ReadAllBytes(p);
                    if (old.Length > 0) enc = DetectEncoding(old);
                }

                SafeFile.KeepBackup(p);
                SafeFile.WriteAtomic(p, text, enc);
                return true;
            }
            catch (Exception ex)
            {
                message = "写入电话簿失败: " + ex.Message;
                return false;
            }
        }

        // ==================================================================
        // 拨号 / 断开
        // ==================================================================

        /// <summary>拨号结果。</summary>
        public class DialResult
        {
            public bool Success;
            public int ExitCode;
            public string Message;   // 面向用户的中文描述
            public string RawOutput; // rasdial 原始输出（仅用于日志）
            public List<string> Hints = new List<string>();
        }

        /// <summary>
        /// 发起拨号。entryName 为电话簿条目名。
        /// user/password 留空表示使用系统已保存的凭据。
        /// 本方法会阻塞至拨号结束，请在后台线程调用。
        /// </summary>
        public static DialResult Dial(string entryName, string user, string password)
        {
            var r = new DialResult();
            if (string.IsNullOrEmpty(entryName))
            {
                r.Message = "连接名称为空";
                return r;
            }

            try
            {
                string args = EscapeArg(entryName);
                if (!string.IsNullOrEmpty(user))
                {
                    args += " " + EscapeArg(user) + " " + EscapeArg(password ?? "");
                }

                // ⚠️ 必须 using：Process 持有进程句柄，不 Dispose 就是每次拨号泄漏一个，
                //    长期挂机会慢慢累积（2026-10-01 审计确认）。
                using (Process proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "rasdial.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (proc == null)
                    {
                        r.Message = "无法启动拨号程序";
                        return r;
                    }

                    string outp = proc.StandardOutput.ReadToEnd();
                    string errp = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(60000))
                    {
                        try { proc.Kill(); } catch { }
                        // Kill 之后要再等一次退出 —— 不等的话句柄还没真正释放，
                        // 可能留下一个"已 Kill 但仍在系统里"的残留进程。
                        try { proc.WaitForExit(3000); } catch { }
                        r.Message = "拨号超时（60 秒未返回）";
                        r.RawOutput = outp + " " + errp;
                        return r;
                    }

                    r.ExitCode = proc.ExitCode;
                    r.RawOutput = (outp + " " + errp).Trim();
                    r.Success = proc.ExitCode == 0;

                    if (r.Success)
                    {
                        r.Message = "拨号成功，已连接";
                    }
                    else
                    {
                        r.Message = DescribeError(proc.ExitCode, r.RawOutput, r.Hints);
                    }
                }
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.ExitCode = -1;
                r.Message = "拨号过程出错: " + ex.Message;
            }

            return r;
        }

        /// <summary>断开指定连接。</summary>
        public static bool Disconnect(string entryName, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(entryName)) { message = "连接名称为空"; return false; }

            try
            {
                // ⚠️ 同上：using 保证句柄释放（2026-10-01 审计确认）。
                using (Process proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "rasdial.exe",
                    Arguments = EscapeArg(entryName) + " /disconnect",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (proc == null) { message = "无法启动断开程序"; return false; }

                    proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        try { proc.WaitForExit(3000); } catch { }
                        message = "断开超时";
                        return false;
                    }

                    if (proc.ExitCode == 0)
                    {
                        message = "已断开连接";
                        return true;
                    }

                    // 704 = 该连接不存在/未建立，对用户而言等同"已经断开了"
                    if (proc.ExitCode == 704)
                    {
                        message = "当前未连接";
                        return true;
                    }

                    message = "断开失败（错误码 " + proc.ExitCode + "）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = "断开过程出错: " + ex.Message;
                return false;
            }
        }

        // ==================================================================
        // 状态检测
        // ==================================================================

        /// <summary>
        /// 内存级在线判定：存在任意 Up 状态的拨号（PPP）接口即视为已连接。
        /// 相比调用 rasdial 查询，这种方式零进程开销，适合定时轮询。
        /// </summary>
        public static bool IsDialConnected()
        {
            return GetConnectedDialName() != null;
        }

        /// <summary>取当前已连接拨号接口的名称；未连接返回 null。</summary>
        public static string GetConnectedDialName()
        {
            try
            {
                foreach (NetworkInterface ni in NicCache.GetAll())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) return ni.Name;
                    if (ni.Name != null)
                    {
                        if (ni.Name.IndexOf("PPP", StringComparison.OrdinalIgnoreCase) >= 0) return ni.Name;
                        if (ni.Name.IndexOf("宽带", StringComparison.Ordinal) >= 0) return ni.Name;
                        if (ni.Name.IndexOf("Broadband", StringComparison.OrdinalIgnoreCase) >= 0) return ni.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>取当前拨号接口的 IPv4 地址；未连接返回空串。</summary>
        public static string GetDialIpAddress()
        {
            try
            {
                foreach (NetworkInterface ni in NicCache.GetAll())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ppp) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            return ua.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        // ==================================================================
        // 电话簿条目
        // ==================================================================

        /// <summary>确保电话簿里存在指定条目；不存在时创建。</summary>
        public static bool EnsureEntry(string entryName, string user, string password, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(entryName)) { message = "连接名称为空"; return false; }

            try
            {
                string text = ReadPhonebook();

                if (text.IndexOf("[" + entryName + "]", StringComparison.Ordinal) >= 0)
                {
                    message = "连接已存在";
                    return true;
                }

                string entry = BuildEntry(entryName, user, password);
                text = text.TrimEnd('\r', '\n') + "\r\n" + entry;
                if (!WritePhonebook(text, out message)) return false;

                message = "已创建连接 " + entryName;
                return true;
            }
            catch (Exception ex)
            {
                message = "创建连接失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>电话簿里是否已有该条目（含学校客户端创建的连接）。</summary>
        public static bool EntryExists(string entryName)
        {
            string t = ReadPhonebook();
            return t.IndexOf("[" + entryName + "]", StringComparison.Ordinal) >= 0;
        }

        /// <summary>删除电话簿中的指定条目。</summary>
        public static bool RemoveEntry(string entryName, out string message)
        {
            message = "";
            try
            {
                string text = ReadPhonebook();
                if (text.Length == 0) { message = "电话簿为空"; return false; }

                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                var kept = new List<string>();
                bool skipping = false;
                bool removed = false;

                foreach (string line in lines)
                {
                    string trimmed = (line ?? "").Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        skipping = trimmed.Equals("[" + entryName + "]", StringComparison.Ordinal);
                        if (skipping) { removed = true; continue; }
                    }
                    if (!skipping) kept.Add(line);
                }

                if (!removed) { message = "未找到该连接"; return false; }
                if (!WritePhonebook(string.Join("\r\n", kept.ToArray()), out message)) return false;

                message = "已删除连接 " + entryName;
                return true;
            }
            catch (Exception ex)
            {
                message = "删除失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 列出电话簿里所有连接名。
        /// 因为用的是系统电话簿，所以**学校官方客户端创建的连接也会出现在这里**。
        /// </summary>
        public static List<string> ListEntries()
        {
            var list = new List<string>();
            try
            {
                string text = ReadPhonebook();
                if (text.Length == 0) return list;

                foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = (raw ?? "").Trim();
                    if (t.StartsWith("[") && t.EndsWith("]") && t.Length > 2)
                    {
                        string name = t.Substring(1, t.Length - 2);
                        if (!list.Contains(name)) list.Add(name);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 读出电话簿中某个条目里保存的账号与密码。
        ///
        /// Windows 电话簿的条目里有两个字段：
        ///   ApnInfoUsername / ApnInfoPassword
        /// 学校客户端建的连接通常会把真实宽带账号写在这里（实测本机两项都有值），
        /// 因此可以做到"选一个连接就把账号密码一起带进来"。
        /// 读不到的（例如凭据被存进凭据管理器、字段留空）就返回空串，让用户自己补。
        /// </summary>
        public static void ReadEntryCredentials(string entryName, out string user, out string password)
        {
            user = "";
            password = "";
            if (string.IsNullOrEmpty(entryName)) return;

            try
            {
                string text = ReadPhonebook();
                if (text.Length == 0) return;

                bool inEntry = false;
                foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = (raw ?? "").Trim();

                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        inEntry = t.Equals("[" + entryName + "]", StringComparison.Ordinal);
                        continue;
                    }
                    if (!inEntry) continue;

                    if (t.StartsWith("ApnInfoUsername=", StringComparison.OrdinalIgnoreCase))
                    {
                        user = t.Substring("ApnInfoUsername=".Length).Trim();
                    }
                    else if (t.StartsWith("ApnInfoPassword=", StringComparison.OrdinalIgnoreCase))
                    {
                        password = t.Substring("ApnInfoPassword=".Length).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取连接凭据失败: " + ex.Message);
            }
        }

        /// <summary>构建一个 PPPoE 电话簿条目文本。字段取值沿用 Windows 标准 PPPoE 模板。</summary>
        private static string BuildEntry(string name, string user, string password)
        {
            return "\r\n[" + name + "]\r\n"
                + "Encoding=1\r\nPBVersion=8\r\nType=5\r\nAutoLogon=0\r\nUseRasCredentials=1\r\n"
                + "DialParamsUID=" + new Random().Next(10000000, 99999999).ToString() + "\r\n"
                + "Guid={" + Guid.NewGuid().ToString("D").ToUpper() + "}\r\n"
                + "VpnStrategy=0\r\nExcludedProtocols=0\r\nLcpExtensions=1\r\nDataEncryption=8\r\nSwCompression=0\r\n"
                + "NegotiateMultilinkAlways=0\r\nSkipDoubleDialDialog=0\r\nDialMode=0\r\nOverridePref=15\r\nRedialAttempts=3\r\n"
                + "RedialSeconds=60\r\nIdleDisconnectSeconds=0\r\nRedialOnLinkFailure=1\r\nCallbackMode=0\r\nCustomDialDll=\r\n"
                + "CustomDialFunc=\r\nCustomRasDialDll=\r\nForceSecureCompartment=0\r\nDisableIKENameEkuCheck=0\r\n"
                + "AuthenticateServer=0\r\nShareMsFilePrint=0\r\nBindMsNetClient=0\r\nSharedPhoneNumbers=0\r\n"
                + "GlobalDeviceSettings=0\r\nPrerequisiteEntry=\r\nPrerequisitePbk=\r\nPreferredPort=PPPoE5-0\r\n"
                + "PreferredDevice=WAN Miniport (PPPOE)\r\nPreferredBps=0\r\nPreferredHwFlow=0\r\nPreferredProtocol=0\r\n"
                + "PreferredCompression=0\r\nPreferredSpeaker=0\r\nPreferredMdmProtocol=0\r\nPreviewUserPw=1\r\n"
                + "PreviewDomain=0\r\nPreviewPhoneNumber=0\r\nShowDialingProgress=1\r\nShowMonitorIconInTaskBar=1\r\n"
                + "CustomAuthKey=0\r\nAuthRestrictions=552\r\nIpPrioritizeRemote=1\r\nIpInterfaceMetric=0\r\n"
                + "IpHeaderCompression=0\r\nIpAddress=0.0.0.0\r\nIpDnsAddress=0.0.0.0\r\nIpDns2Address=0.0.0.0\r\n"
                + "IpWinsAddress=0.0.0.0\r\nIpWins2Address=0.0.0.0\r\nIpAssign=1\r\nIpNameAssign=1\r\nIpDnsFlags=0\r\n"
                + "IpNBTFlags=0\r\nTcpWindowSize=0\r\nUseFlags=3\r\nIpSecFlags=0\r\nIpDnsSuffix=\r\nIpv6Assign=1\r\n"
                + "Ipv6Address=::\r\nIpv6PrefixLength=0\r\nIpv6PrioritizeRemote=1\r\nIpv6InterfaceMetric=0\r\n"
                + "Ipv6NameAssign=1\r\nIpv6DnsAddress=::\r\nIpv6Dns2Address=::\r\nIpv6Prefix=0000000000000000\r\n"
                + "Ipv6InterfaceId=0000000000000000\r\nDisableClassBasedDefaultRoute=0\r\nDisableMobility=0\r\n"
                + "NetworkOutageTime=0\r\nIDI=\r\nIDR=\r\nImsConfig=0\r\nIdiType=0\r\nIdrType=0\r\nProvisionType=0\r\n"
                + "PreSharedKey=\r\nCacheCredentials=1\r\nNumCustomPolicy=0\r\nNumEku=0\r\nUseMachineRootCert=0\r\n"
                + "Disable_IKEv2_Fragmentation=0\r\nPlumbIKEv2TSAsRoutes=0\r\nNumServers=0\r\nRouteVersion=1\r\n"
                + "NumRoutes=0\r\nNumNrptRules=0\r\nAutoTiggerCapable=0\r\nNumAppIds=0\r\nNumClassicAppIds=0\r\n"
                + "SecurityDescriptor=\r\nApnInfoProviderId=\r\nApnInfoUsername=" + (user ?? "") + "\r\n"
                + "ApnInfoPassword=" + (password ?? "") + "\r\n"
                + "ApnInfoAccessPoint=\r\nApnInfoAuthentication=1\r\nApnInfoCompression=0\r\nDeviceComplianceEnabled=0\r\n"
                + "DeviceComplianceSsoEnabled=0\r\nDeviceComplianceSsoEku=\r\nDeviceComplianceSsoIssuer=\r\nFlagsSet=0\r\n"
                + "Options=0\r\nDisableDefaultDnsSuffixes=0\r\nNumTrustedNetworks=0\r\nNumDnsSearchSuffixes=0\r\n"
                + "PowershellCreatedProfile=0\r\nProxyFlags=0\r\nProxySettingsModified=0\r\nProvisioningAuthority=\r\n"
                + "AuthTypeOTP=0\r\nGREKeyDefined=0\r\nNumPerAppTrafficFilters=0\r\nAlwaysOnCapable=0\r\nDeviceTunnel=0\r\n"
                + "PrivateNetwork=0\r\nManagementApp=\r\n\r\nNETCOMPONENTS=\r\nms_msclient=0\r\nms_server=0\r\n\r\n"
                + "MEDIA=rastapi\r\nPort=PPPoE5-0\r\nDevice=WAN Miniport (PPPOE)\r\n\r\nDEVICE=PPPoE\r\n"
                + "LastSelectedPhone=0\r\nPromoteAlternates=0\r\nTryNextAlternateOnFail=1\r\n";
        }

        // ==================================================================
        // 错误码词典
        // ==================================================================

        /// <summary>按退出码给出中文说明与排查建议。</summary>
        public static string DescribeError(int code, string rawOutput, List<string> hints)
        {
            string desc;
            switch (code)
            {
                case 691:
                    desc = "账号或密码错误（也可能是在别处已登录）";
                    Add(hints, "核对宽带账号与上网密码是否正确（注意不是校园门户的登录密码）");
                    Add(hints, "如果手机 / 路由器上有同名账号在线，先退出那边再重试");
                    Add(hints, "部分校园网同一账号只允许一台设备在线，等待几分钟后再拨");
                    break;
                case 676:
                    desc = "电话线占线 / 线路忙";
                    Add(hints, "网口或上联设备忙，稍等一会儿再拨");
                    break;
                case 721:
                    desc = "对方无应答";
                    Add(hints, "服务器没响应，多半是临时故障，过几分钟再试");
                    break;
                case 735:
                    desc = "服务器拒绝分配地址";
                    Add(hints, "账号可能已达在线数量上限，先在别的设备上退出登录");
                    break;
                case 797:
                    desc = "找不到调制解调器 / 宽带设备";
                    Add(hints, "网卡驱动或宽带设备异常：检查设备管理器里的网络适配器");
                    break;
                case 815:
                    desc = "宽带连接不可用（协议不匹配）";
                    Add(hints, "连接的协议设置被改动过，可在「网络和共享中心」里重新创建一次宽带连接");
                    break;
                case 692:
                    desc = "调制解调器或端口故障";
                    Add(hints, "可能是网口或线路问题，尝试换一个网口 / 重新插拔网线");
                    break;
                case 678:
                    desc = "没有拨号音 / 远程计算机无响应";
                    Add(hints, "确认网线已插好，且网口有信号（可先做一次网络体检）");
                    break;
                case 680:
                    desc = "没有拨号音";
                    Add(hints, "多数情况是网线未插好或网口无信号");
                    break;
                case 619:
                    desc = "无法建立到远程计算机的连接";
                    Add(hints, "检查网线连接，或稍后重试");
                    break;
                case 629:
                case 651:
                    desc = "连接被远程计算机终止 / 网卡无响应";
                    Add(hints, "尝试禁用再启用有线网卡，然后重新拨号");
                    break;
                case 720:
                    desc = "无法建立 PPPoE 连接（协议层面失败）";
                    Add(hints, "可能是系统网络组件异常，建议重启电脑后再试");
                    break;
                case 734:
                    desc = "PPP 链接控制协议终止";
                    Add(hints, "对方服务器拒绝了本次协商，稍后重试");
                    break;
                case 718:
                    desc = "等待对方应答超时";
                    Add(hints, "服务器繁忙或网络拥堵，稍等片刻后重试");
                    break;
                case 769:
                    desc = "指定的目标不可达";
                    Add(hints, "本机网络地址配置异常，尝试重启网卡或重新获取 IP");
                    break;
                case 789:
                    desc = "安全层协商失败";
                    Add(hints, "通常是加密协议不匹配，稍后重试或联系网络管理员");
                    break;
                case 814:
                    desc = "未找到宽带网络设备";
                    Add(hints, "网卡驱动可能异常，检查设备管理器中的网络适配器");
                    break;
                case 55:
                    desc = "指定的电话簿条目不存在";
                    Add(hints, "连接尚未创建，请先添加账号后再拨号");
                    break;
                default:
                    desc = "拨号失败（错误码 " + code + "）";
                    Add(hints, "建议先执行一次网络体检，确认网卡与线路状态");
                    Add(hints, "将诊断包导出后发给协助者，可更快定位问题");
                    break;
            }

            // 691 是个"大类"：服务器其实会在输出里写明具体原因（密码错、并发超限、
            // 欠费、终端绑定不符……），但 rasdial 只给一个 691。这里按关键词细分，
            // 把"该等 10 分钟"和"该去改密码"分开 —— 否则用户只能一股脑重试。
            //
            // 词条来自公开可查的运营商/BRAS 返回文案（配合本机实测整理），
            // 只做**原因解释与处置建议**，不涉及任何绕过手段。
            if (code == 691)
            {
                desc = DetailOf691(rawOutput, hints) ?? desc;
            }

            return desc;
        }

        /// <summary>
        /// 这次失败是不是"**越重试越糟**"的那一类（限速风控 / 并发超限）。
        ///
        /// ⚠️ 为什么要单独判（2026-10-02 审计确认）：
        ///   这类 691 的机制是"按失败次数加码"—— 你每试一次，等待时间就更长。
        ///   而程序的自动重连默认 30 秒一次，等于一直在给风控续期，
        ///   结果就是"越修越连不上，最后要等十几分钟"。
        ///   所以自动重连遇到这类失败必须拉长间隔、甚至停手；
        ///   而用户**手动**点连接不受限制（人在操作就尊重人）。
        ///
        /// 判定口径与 DetailOf691 的前两类保持一致（限速 + 并发），
        /// 那两类才是有"静置可解"性质的；欠费/密码错/被暂停重试也没用，但不会越试越糟。
        /// </summary>
        public static bool IsRateLimitFailure(string rawOutput)
        {
            string low = (rawOutput ?? "").ToLowerInvariant();
            if (low.Length == 0) return false;

            return Has(low, "so soon", "频繁")
                || Has(low, "too many connections", "limit users", "concurrency",
                        "access number is exceed", "并发", "已在线");
        }

        /// <summary>
        /// 把 691 拆细，返回更贴切的说明；匹配不到关键词时返回 null（沿用通用说明）。
        ///
        /// 命中后**替换**掉通用建议：既然已经判成"被限速了"，再让人核对密码只会把人带偏。
        /// ⚠️ 关键词一律转小写后用 IndexOf 匹配，不用正则（BRAS 文案里符号五花八门）。
        /// </summary>
        private static string DetailOf691(string rawOutput, List<string> hints)
        {
            string low = (rawOutput ?? "").ToLowerInvariant();
            if (low.Length == 0) return null;

            var mine = new List<string>();

            // 顺序有讲究：先"限速 / 并发"这类有明确处置动作的，
            // 最后才是"密码 / 认证失败"这种通用的兜底
            if (Has(low, "so soon", "频繁"))
            {
                Add(mine, "这是学校/运营商的限速风控：别连续重试，静置约 10 分钟再拨");
                Add(mine, "越急着重试，等待时间越长 —— 所以程序也不会自动帮你狂试");
                return Swap(hints, mine, "拨号过于频繁，被服务器限速了");
            }
            if (Has(low, "too many connections", "limit users", "concurrency",
                    "access number is exceed", "并发", "已在线"))
            {
                Add(mine, "同一账号同时在线的设备数超了：把手机 / 路由器 / 别的电脑上的同名连接退掉");
                Add(mine, "有些运营商不允许「WiFi 与宽带同时在线」，先断开手机上的校园 WiFi 再拨");
                Add(mine, "退掉之后等一会儿再试（并发类通常要等 10 到 30 分钟）");
                return Swap(hints, mine, "账号已在别处在线（并发数超限）");
            }
            if (Has(low, "overdue", "charge", "过期", "欠费"))
            {
                Add(mine, "到校园网自助服务 / 计费中心确认账号状态：是否欠费、上网密码是否已过期");
                return Swap(hints, mine, "账号欠费或密码已过期");
            }
            if (Has(low, "suspended", "account pause", "暂停", "锁定"))
            {
                Add(mine, "账号被暂停了，需要联系运营商营业厅或学校网络中心处理");
                return Swap(hints, mine, "宽带账号已被暂停");
            }
            if (Has(low, "can't find user", "user not exist", "username", "invalid",
                    "不存在", "未登记"))
            {
                Add(mine, "账号名可能写错了，或这台设备还没在学校登记");
                Add(mine, "确认填的是运营商给的宽带账号，不是学号 / 门户账号");
                return Swap(hints, mine, "账号不存在 / 未登记");
            }
            if (Has(low, "terminal", "nas-port-id", "bindattr", "invalid location", "绑定", "地点"))
            {
                Add(mine, "学校把账号和登记的设备（MAC）或网口绑定在一起了");
                Add(mine, "换过电脑、换过网口就会出现这个错 —— 换回原来那台设备 / 那个网口拨号即可");
                Add(mine, "确实需要长期换，联系学校网络中心改一次绑定");
                return Swap(hints, mine, "终端 / 上网地点绑定校验没通过");
            }
            if (Has(low, "password", "密码"))
            {
                Add(mine, "密码不对或已过期：到校园网自助服务重置一次上网密码");
                Add(mine, "注意区分「上网密码」和「校园门户密码」，两者通常不一样");
                return Swap(hints, mine, "密码错误");
            }
            if (Has(low, "auth failed", "authentication fail", "认证失败"))
            {
                Add(mine, "认证被拒：优先核对账号本身（别把学号 / 门户号当成宽带账号）");
                Add(mine, "如果账号确认没错，稍后重试或联系学校网络中心");
                return Swap(hints, mine, "认证失败");
            }
            return null;
        }

        /// <summary>用更贴切的建议替换掉通用建议。</summary>
        private static string Swap(List<string> hints, List<string> mine, string desc)
        {
            if (hints != null)
            {
                hints.Clear();
                for (int i = 0; i < mine.Count; i++) hints.Add(mine[i]);
            }
            return desc;
        }

        private static bool Has(string lowText, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (lowText.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static void Add(List<string> list, string item)
        {
            if (list != null && !list.Contains(item)) list.Add(item);
        }

        /// <summary>rasdial 参数转义：含空格时加引号。</summary>
        private static string EscapeArg(string value)
        {
            if (value == null) return "\"\"";
            if (value.IndexOf(' ') >= 0 || value.IndexOf('\t') >= 0)
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }
            return value;
        }
    }
}
