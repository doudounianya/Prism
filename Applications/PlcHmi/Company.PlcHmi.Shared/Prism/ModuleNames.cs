namespace Company.PlcHmi.Shared.Prism
{
    /// <summary>Prism 模块标识的共享常量；声明模块、依赖关系和按需加载时须使用同一名称。</summary>
    public class ModuleNames
    {
        /// <summary>模块程序集发现目录，相对于应用启动位置使用。</summary>
        public const string ModulePath = @".\Modules";
        public const string PlcHmiLoginModule = "PlcHmiLoginModule";
        public const string PlcHmiMainModule = "PlcHmiMainModule";
        public const string PlcHmiMenuModule = "PlcHmiMenuModule";
        public const string PlcHmiStartupModule = "PlcHmiStartupModule";
        public const string PlcHmiPlcModule = "PlcHmiPlcModule";
        public const string PlcHmiConfigurationModule = "PlcHmiConfigurationModule";
    }
}
