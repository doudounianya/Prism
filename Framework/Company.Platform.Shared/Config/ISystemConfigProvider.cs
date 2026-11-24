namespace Company.Platform.Shared.Config
{
    /// <summary>系统配置提供者。</summary>
    /// <remarks>继承软件配置读取和变更通知，用独立服务类型区分系统配置的依赖注入入口。</remarks>
    public interface ISystemConfigProvider : ISoftwareConfigProvider
    {
    }
}
