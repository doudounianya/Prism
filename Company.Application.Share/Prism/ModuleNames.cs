namespace Company.Application.Share.Prism
{
    /// <summary>Prism 模块标识的共享常量；声明模块、依赖关系和按需加载时须使用同一名称。</summary>
    public class ModuleNames
    {
        /// <summary>模块程序集发现目录，相对于应用启动位置使用。</summary>
        public const string ModulePath = @".\Modules";
        public const string ApplicationLoginModule = "ApplicationLoginModule";
        public const string ApplicationMainModule = "ApplicationMainModule";
        public const string ApplicationMenuModule = "ApplicationMenuModule";
        public const string ApplicationInitializeModule = "ApplicationInitializeModule";
        public const string ApplicationPlcModule = "ApplicationPlcModule";
        public const string ApplicationConfigModule = "ApplicationConfigModule";
    }
}
