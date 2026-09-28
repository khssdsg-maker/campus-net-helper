namespace CampusNetHelper
{
    /// <summary>
    /// 本校专属配置 —— **这是模板文件，可以提交到版本库**。
    ///
    /// 首次构建时，build_check.bat 会自动把这个文件复制成 SiteConfig.cs。
    /// 而 SiteConfig.cs 已被 .gitignore 排除 —— 你自己学校的地址不会进公开仓库。
    ///
    /// 想换成自己学校：直接改 SiteConfig.cs（或改这里再删掉 SiteConfig.cs 重新构建），
    /// 填上地址后重新编译即可。
    /// </summary>
    internal static class SiteConfig
    {
        /// <summary>
        /// 校园网自助服务入口。
        ///
        /// 这类入口通常只有校内网才能访问（比如 10.x / 172.16.x / 192.168.x 开头的地址），
        /// 填在源码里等于把学校的内部地址公开出去，所以单独放到这个文件里。
        ///
        /// 留空 → 主界面「官方入口」里不显示这个按钮（避免给出一个点不开的死链）。
        /// </summary>
        internal const string SelfServiceUrl = "";
    }
}
