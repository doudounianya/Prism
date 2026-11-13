using Company.Database;
using Company.Core.Extension.Sql;
namespace Company.Database
{
    public class Member : EntityBase // 继承实体基类，满足泛型接口约束
    {
        /// <summary>
        /// 用户名（非空 + 唯一 + 最大长度50）
        /// </summary>
        [Required(Message = "用户名不能为空")]
        [Unique(Message = "用户名已存在")]
        [StringLength(50)]
        public string UserName { get; set; }

        /// <summary>
        /// 密码（非空 + 最大长度32，适配MD5 32位加密）
        /// </summary>
        [Required(Message = "密码不能为空")]
        [StringLength(32)]
        public string Password { get; set; }

        /// <summary>
        /// 角色（非空）
        /// </summary>
        [Required(Message = "角色不能为空")]
        public int Role { get; set; }

        /// <summary>
        /// 插入时间（非空）
        /// </summary>
        [Required(Message = "插入时间不能为空")]
        public DateTime InsertDate { get; set; }
    }
}