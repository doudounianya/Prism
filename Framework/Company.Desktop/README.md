# 桌面适配层

承载 WPF 对话框、语言资源操作、Prism 服务注册及桌面服务入口，依赖 Company.Core。Company.Core 不得反向引用本项目。

CoreModule 同时注册 Company.Core 与本程序集中的服务元数据，保持旧配置/缓存的单例注册、PrismProvider 的自动初始化和原语言服务语义。宿主仍显式加入 CoreModule。

ApplicationShutdownBridge 是旧 CloseAllHardwareEvent 的兼容入口，只转交 IApplicationLifecycle。PLC、历史、运动卡各自登记到同一个协调器，停止顺序不再取决于事件订阅顺序。

为减少既有界面迁移，本轮保留移动类型的 Company.Core 命名空间。使用这些桌面类型的项目必须显式引用 Company.Desktop。PopupBox 的 BAML 地址为 `/Company.Desktop;component/Dialog/PopupBox.xaml`。新 API 使用 Company.Desktop 命名空间。
