namespace Company.PlcHmi.Shared.Prism
{
    /// <summary>Prism 区域契约；视图宿主声明与导航调用共用这些名称，避免区域字符串不一致。</summary>
    public class RegionNames
    {
        public const string MainRegion = "MainRegion";
        public const string MenuRegion = "MenuRegion";
        /// <summary>主工作区区域名称，用于承载业务页面导航。</summary>
        public const string WorkspaceRegion = "WorkspaceRegion";
    }
}
