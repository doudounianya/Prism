using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension.Sql
{
    /// <summary>
    /// 自定义字符串长度特性（非 SqlSugar，仅用于标记字段长度）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class StringLengthAttribute : Attribute
    {
        /// <summary>
        /// 最大长度
        /// </summary>
        public int MaxLength { get; set; }

        public StringLengthAttribute(int maxLength)
        {
            MaxLength = maxLength;
        }
    }
}
