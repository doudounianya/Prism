using Company.Platform.Shared.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Platform.Shared.Login
{
    /// <summary>跨模块共享的当前登录会话；登录、退出和用户信息变更通过 CurrentUser 反映。</summary>
    public interface ISession
    {
        /// <summary>当前用户对象；页面应结合访问服务判断是否已登录，不能仅因该属性非空就授予权限。</summary>
        CurrentUser CurrentUser { get; set; }
    }
}
