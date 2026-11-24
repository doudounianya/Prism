using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.PlcHmi.Shared.Prism
{
    /// <summary>跨模块导航键；视图注册、菜单导航和页面访问规则须保持名称一致。</summary>
    public static class ViewNames
    {
        /// <summary>
        /// 登录界面
        /// </summary>
        public const string LoginView = "LoginView";

        /// <summary>
        /// 主界面
        /// </summary>
        public const string MainView = "MainView";

        /// <summary>
        /// 应用初始化界面
        /// </summary>
        public const string InitializeView = "InitializeView";

        /// <summary>
        /// 系统配置界面
        /// </summary>
        public const string ConfigView = "ConfigView";
        /// <summary>管理员账户管理页面。</summary>
        public const string UserManagementView = "UserManagementView";
        /// <summary>当前用户修改密码页面。</summary>
        public const string ChangePasswordView = "ChangePasswordView";
        public const string SignInView = "SignInView";
        /// <summary>设备概览与运行监控页面。</summary>
        public const string DeviceMonitorView = "DeviceMonitorView";
        /// <summary>设备模式控制页面。</summary>
        public const string ModeControlView = "ModeControlView";
        /// <summary>IO 状态监视页面。</summary>
        public const string IoMonitorView = "IoMonitorView";
        /// <summary>轴参数及相关操作页面。</summary>
        public const string AxisParameterView = "AxisParameterView";
        /// <summary>气缸参数及相关操作页面。</summary>
        public const string CylinderParameterView = "CylinderParameterView";
        /// <summary>工艺参数页面。</summary>
        public const string ProcessParameterView = "ProcessParameterView";
        /// <summary>已登录用户查看 PLC 操作审计记录。</summary>
        public const string OperationLogView = "OperationLogView";
        /// <summary>本次运行的实时报警观测页面。</summary>
        public const string RealtimeAlarmView = "RealtimeAlarmView";
        /// <summary>持久化报警历史查询页面。</summary>
        public const string AlarmHistoryView = "AlarmHistoryView";
        /// <summary>报警统计分析页面。</summary>
        public const string AlarmAnalysisView = "AlarmAnalysisView";
        /// <summary>当前生产日产能页面。</summary>
        public const string ProductionTodayView = "ProductionTodayView";
        /// <summary>按生产日查询的产能明细页面。</summary>
        public const string ProductionDailyView = "ProductionDailyView";
        /// <summary>跨生产日的产能趋势页面。</summary>
        public const string ProductionTrendView = "ProductionTrendView";



    }
}
