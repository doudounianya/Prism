using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension.Sql
{
    /// <summary>
    /// 自定义唯一特性（非 SqlSugar，仅用于标记唯一字段）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class UniqueAttribute : Attribute
    {
        /// <summary>
        /// 唯一约束提示信息（可选）
        /// </summary>
        public string Message { get; set; }

        public UniqueAttribute() { }

        public UniqueAttribute(string message)
        {
            Message = message;
        }
    }
}
