using Company.PlcHmi.Login.Views;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.PlcHmi.Login
{
    /// <summary>
    /// 按需加载登录与账号管理功能，向 Prism 注册启动登录页和三个账号相关弹窗。
    /// 模块注册只建立导航/对话框映射，实际认证由各视图模型调用账号服务完成。
    /// </summary>
    [Module(ModuleName = ModuleNames.PlcHmiLoginModule, OnDemand = true)]
    [ModuleDependency("PlcHmiPlatformModule")]
    public class PlcHmiLoginModule : IModule   //2025/09/17 查询1小时  因为public  导致加载不成功
    {
        /// <summary>扫描当前程序集的服务声明，并分别注册导航页与对话框入口。</summary>
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());

            containerRegistry.RegisterForNavigation<LoginView>();
            containerRegistry.RegisterDialog<UserManagementView>();
            containerRegistry.RegisterDialog<ChangePasswordView>();
            containerRegistry.RegisterDialog<SignInView>();
        }
        /// <summary>保留 Prism 初始化回调；当前模块没有额外的初始化动作。</summary>
        public void OnInitialized(IContainerProvider containerProvider)
        {

        }
    }
}
