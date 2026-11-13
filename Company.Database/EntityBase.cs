using Company.Core.Extension.Sql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Database
{
    /// <summary>
    /// 所有表的实体的抽象基类
    /// </summary>
    public abstract class EntityBase
    {
        /// <summary>
        /// 主键ID（自增）
        /// </summary>
        [PrimaryKey(IsIdentity = true)] // 标记为主键，且自增
        public int Id { get; set; } // 与 IRepository<TEntity> 的 GetEntity(int id) 对应
    }
}
