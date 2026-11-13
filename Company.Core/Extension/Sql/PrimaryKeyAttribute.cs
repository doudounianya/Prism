using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension.Sql
{
    /// <summary>
    /// 自定义主键特性（非 SqlSugar，仅用于标记主键字段）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class PrimaryKeyAttribute : Attribute
    {
        /// <summary>
        /// 是否自增（默认 true，适配 MySQL AUTO_INCREMENT）
        /// </summary>
        public bool IsIdentity { get; set; } = true;

        public PrimaryKeyAttribute() { }

        public PrimaryKeyAttribute(bool isIdentity)
        {
            IsIdentity = isIdentity;
        }
    }
}
