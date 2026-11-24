# 公共平台服务

目标 net8.0，无 WPF、Prism 容器、PLC HMI、运动卡和 ORM 实现依赖。服务通过构造参数接收账户仓储、会话、缓存及应用策略；Core 的 ExposedService 元数据只描述默认单例注册，不自动执行认证或设备连接。

| 目录 | 职责 |
| --- | --- |
| Accounts | 账户认证、管理员创建/删除/重置、本人原密码验证、数据库角色重验及缓存失效 |
| Identity | 标准 INotifyPropertyChanged/Changing 会话，与界面框架无关 |
| Authorization | 公共授权服务、权限版本、策略通知及基本角色策略 |
| Login | 登录偏好恢复、旧格式迁移、受保护缓存及异常降级；实际加解密由保护器提供 |

PLC 的手动调试、人员锁和页面范围不能写进这里。见根目录 PlatformServices.md 的组合方式及兼容说明。
