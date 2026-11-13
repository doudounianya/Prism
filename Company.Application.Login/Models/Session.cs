using Company.Application.Share.Login;
using Company.Application.Share.Model;
using Company.Core.Ioc;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Application.Login.Models
{
    /// <summary>
    /// 保存应用内共享的当前用户对象，供登录、菜单和业务页面读取同一会话身份。
    /// 替换 CurrentUser 时通过 ReactiveUI 发布属性变化，驱动权限服务刷新；本类不执行认证或持久化。
    /// </summary>
    [ExposedService(Lifetime.Singleton, typeof(ISession))]
    public class Session : ReactiveObject, ISession
    {
        /// <summary>
        /// 当前登录的用户
        /// </summary>
        private CurrentUser _currentUser = new();
        // 通知围绕用户对象的替换发生；直接修改对象内部字段不经过此 setter。
        public CurrentUser CurrentUser { get => _currentUser; set => this.RaiseAndSetIfChanged(ref _currentUser, value); }
    }
}
