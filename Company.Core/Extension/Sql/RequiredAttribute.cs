using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension.Sql
{
    /// <summary>
    /// 自定义非空特性（非 SqlSugar，仅用于标记非空字段）
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class RequiredAttribute : Attribute
    {
        /// <summary>
        /// 非空提示信息（可选）
        /// </summary>
        public string Message { get; set; }

        public RequiredAttribute() { }

        public RequiredAttribute(string message)
        {
            Message = message;
        }
    }
}
