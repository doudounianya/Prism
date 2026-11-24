using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Platform.Shared.Model
{
    /// <summary>会话共享的可变用户信息；身份、角色与登录时间供界面和权限服务使用。</summary>
    public class CurrentUser
    {
        /// <summary>账户编号；访问规则要求大于零才视为有效登录用户。</summary>
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        /// <summary>兼容既有用户模型的字段；属于敏感信息，不应作为显示内容或写入日志。</summary>
        public string Password { get; set; } = "";
        public string Email { get; set; } = "";
        /// <summary>持久化角色编号，按 UserRole 解释；不能用用户名代替角色校验。</summary>
        public int Role { get; set; }
        /// <summary>会话登录时间，默认使用创建对象时的本地时间。</summary>
        public DateTime LoginTime { get; set; } = DateTime.Now;
    }
}
