namespace CampusNetHelper
{
    /// <summary>
    /// 本校专属默认值 —— **这是模板文件，可以提交到版本库**。
    ///
    /// 首次构建时，build_check.bat 会自动把这个文件复制成 SiteConfig.cs。
    /// 而 SiteConfig.cs 已被 .gitignore 排除 —— 你自己的学校信息不会进公开仓库。
    ///
    /// 为什么要留这么一个文件：
    ///   这个项目刻意保持**通用** —— 源码里不放任何学校的具体信息
    ///   （校徽、校名、学校官网、校内地址都已经移除）。
    ///   但你自己用的时候，肯定希望默认值已经填好，不用每次手输。
    ///   两者兼顾的办法就是：通用逻辑写在源码里，本校专属的值放这里。
    /// </summary>
    internal static class SiteConfig
    {
        /// <summary>
        /// 网页认证的默认网址。
        ///
        /// 留空 → 界面上就是空的，由使用者自己填（公开仓库构建出来的就是这个状态）。
        /// 填上 → 编译出来的版本打开就是预填好的，适合发给自己学校的同学。
        ///
        /// 例如："http://10.0.0.1/srun_portal_pc.php"
        /// </summary>
        internal const string DefaultWebAuthUrl = "";
    }
}
