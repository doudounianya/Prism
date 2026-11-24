# PLC HMI 应用装配

这是 PLC HMI 选择具体实现的位置，允许引用后台库、数据库适配器和所需运动驱动。后台库不引用此项目。

七个 IModule 使用原来的类名、命名空间、模块名称、依赖关系及按需加载设置，便于现有 Shell 的 DirectoryModuleCatalog 继续发现：

- DatabaseSqlSugarModule：数据库配置、会话及账户仓储。
- PlcHmiPersistenceModule：报警/产能仓储和历史事务。
- PlcHmiPlcModule：扫描 Company.PlcHmi.Plc 中的服务并按原时机初始化。
- MotionControllerModule：同一个运动工厂单例，注入 Prism 命名解析函数。
- NoneControlCardModule、INOVANCEControlCardModule：仅注册仿真与汇川命名驱动。

扫描注册和工厂解析均不等于连接硬件。真实连接仍由已有启动流程及显式配置触发。其它软件应建立自己的装配入口，按实际机型选择驱动。
