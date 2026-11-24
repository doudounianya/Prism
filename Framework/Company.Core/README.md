# 无界面基础库

目标 net8.0，只依赖 Newtonsoft.Json，提供配置、缓存、JSON/文件处理、基础模型、语言契约和服务标记元数据。ExposedServiceAttribute 只描述生命周期，不引用任何容器；桌面宿主决定如何注册。

Runtime 提供 IApplicationLifecycle/ApplicationLifecycle：服务登记启动与停止步骤，协调器统一持有任务、处理重复调用和失败清理。它不认识 PLC、Prism 或界面，不连接任何硬件。

配置键的完整类型名称、缓存键、序列化字段、文件名生成规则、JSON 原子保存实现均保留。没有改动密码摘要算法。WPF 对话框、PrismProvider、注册扩展和语言资源实现已迁入 Company.Desktop。
