using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Company.Database.Accounts;

/// <summary>
/// 账户存储记录。表名及列名显式映射到既有 Member 表，重命名 C# 类型不要求迁移现场数据。
/// 这是仓储与账户服务之间的内部数据，不应直接绑定用户列表或作为日志载荷。
/// </summary>
[Table("Member")]
public sealed class UserAccount
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, StringLength(50)]
    public string UserName { get; set; } = "";

    /// <summary>保留现有 32 位密码摘要格式；存储字段继续使用 Password，仓储不接收明文密码。</summary>
    [Required, StringLength(32), Column("Password")]
    public string PasswordHash { get; set; } = "";

    /// <summary>保存既有角色编号；界面名称和操作授权由账户/权限服务解释。</summary>
    public int Role { get; set; }

    /// <summary>保留原 InsertDate 字段及时间语义，更新密码时不得改写创建时间。</summary>
    [Column("InsertDate")]
    public DateTime CreatedAt { get; set; }

    public override string ToString() => $"UserAccount(Id={Id}, Role={Role})";
}
