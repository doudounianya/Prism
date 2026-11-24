/*
 * 文件职责与维护说明：
 * 主业务模块入口：通过既有 Prism 容器注册页面、视图模型及业务服务。
 * 页面使用命名导航单例，初始化隐藏准备后反复显示；各页自己的采集范围仍随可见性管理，不因对象常驻而持续采集。
 */
using Company.PlcHmi.Main.Views;
using Company.PlcHmi.Main.ViewModels;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Extension;
using Prism.Ioc;
using Prism.Modularity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.PlcHmi.Main
{
    [Module(ModuleName = ModuleNames.PlcHmiMainModule, OnDemand = true)]
    [ModuleDependency(ModuleNames.PlcHmiMenuModule)]//菜单模块
    [ModuleDependency(ModuleNames.PlcHmiPlcModule)]
    public class PlcHmiMainModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());

            // Initialize 模块在 PLC 启动完成后显式导航主界面，避免提前显示。
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());

            containerRegistry.RegisterForNavigation<MainView>();
            containerRegistry.RegisterSingleton<DeviceMonitorViewModel>();
            containerRegistry.RegisterSingleton<ModeControlViewModel>();
            containerRegistry.RegisterSingleton(typeof(object), typeof(DeviceMonitorView), nameof(DeviceMonitorView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(ModeControlView), nameof(ModeControlView));
            containerRegistry.RegisterSingleton<MainViewModel>();
            containerRegistry.RegisterSingleton(typeof(object), typeof(MainView), nameof(MainView));
            // 业务页使用命名导航单例；初始化准备与后续导航解析同一实例，各页采集/操作会话仍独立。
            containerRegistry.RegisterSingleton<IoMonitorViewModel>();
            containerRegistry.RegisterSingleton<AxisParameterViewModel>();
            containerRegistry.RegisterSingleton<CylinderParameterViewModel>();
            containerRegistry.RegisterSingleton<OperationLogViewModel>();
            containerRegistry.RegisterSingleton(typeof(object), typeof(OperationLogView), nameof(OperationLogView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(IoMonitorView), nameof(IoMonitorView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(AxisParameterView), nameof(AxisParameterView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(CylinderParameterView), nameof(CylinderParameterView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(ProcessParameterView), nameof(ProcessParameterView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(RealtimeAlarmView), nameof(RealtimeAlarmView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(AlarmHistoryView), nameof(AlarmHistoryView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(AlarmAnalysisView), nameof(AlarmAnalysisView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(ProductionTodayView), nameof(ProductionTodayView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(ProductionDailyView), nameof(ProductionDailyView));
            containerRegistry.RegisterSingleton(typeof(object), typeof(ProductionTrendView), nameof(ProductionTrendView));

        }
    }
}
