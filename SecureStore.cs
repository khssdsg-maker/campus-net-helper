using System;
using System.Security.Cryptography;
using System.Text;

namespace CampusNetHelper
{
    /// <summary>
    /// 密码的本地加密存储（Windows DPAPI）。
    ///
    /// 背景：原先 accounts.txt 里的密码是**明文**的 —— 任何能读你用户目录的程序、
    /// 或者你把 %AppData%\CampusNetHelper 备份/发给别人，密码就直接暴露了。
    /// 2026-09-29 对比海辰另一个工具时确认这是个真实短板，本版补上。
    ///
    /// 用 DPAPI 的 CurrentUser 作用域：密文只有**同一台机器的同一个用户**能解开，
    /// 拷到别的电脑、别的账户上都是一堆无意义的字符串 —— 所以配置可以放心备份/外发。
    ///
    /// ⚠️ 兼容与迁移：
    ///   · 老版本写的明文密码照常能读（不带前缀就是明文），读进来之后**下次保存自动加密**；
    ///   · 加密值带 "enc:" 前缀，一眼能看出是密文；
    ///   · 解不开时（比如配置是从别的电脑拷来的）返回空串 —— 宁可让你重填一次，
    ///     也不能拿一串密文当密码去拨号（那只会得到莫名其妙的 691）。
    ///
    /// ⚠️ 降级提醒：本版之后如果把程序换回旧版本，旧版本会把 "enc:xxx" 当成字面密码，
    ///   于是拨号失败 —— 需要重新填一次密码。（发布说明里已写明。）
    /// </summary>
    internal static class SecureStore
    {
        /// <summary>密文前缀。带它 = 已加密；不带 = 老版本的明文。</summary>
        public const string Prefix = "enc:";

        /// <summary>这个值是不是密文。</summary>
        public static bool IsProtected(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>加密。失败（极少见）时返回原文并写日志 —— 绝不能因此把密码弄丢。</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (IsProtected(plain)) return plain;      // 已经是密文，别二次加密

            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                Log.Warn("密码加密失败，本次按明文保存（仅影响本机）: " + ex.Message);
                return plain;
            }
        }

        /// <summary>解密。明文原样返回；解不开返回空串（让用户重填，而不是拿密文去拨号）。</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!IsProtected(stored)) return stored;   // 老版本的明文，原样用

            try
            {
                string b64 = stored.Substring(Prefix.Length);
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                // 最常见的原因：配置是从别的电脑/别的账户拷过来的，DPAPI 解不开
                Log.Warn("密码解密失败（可能是从别的电脑拷来的配置），需要重新填一次密码: " + ex.Message);
                return "";
            }
        }
    }
}
