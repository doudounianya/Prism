using Prism.Events;
using Company.Platform.Shared.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Platform.Shared.Events
{
    /// <summary>
    /// 用户登录成功时
    /// </summary>
    /// <remarks>载荷为当前登录用户；订阅方据此更新会话或界面，角色权限仍通过访问服务判断。</remarks>
    public class LoginSuccessEvent : PubSubEvent<CurrentUser>
    {
    }
}
