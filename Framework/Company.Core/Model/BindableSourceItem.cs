using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Model
{
    /// <summary>
    /// 绑定源的元素实体
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class BindableSourceItem<T>
    {
        /// <summary>
        /// 描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 值
        /// </summary>
        public T Value { get; set; }

        public BindableSourceItem()
        {

        }
        public BindableSourceItem(string description, T value)
        {
            Description = description;
            Value = value;
        }
    }
}
