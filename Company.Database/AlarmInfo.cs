using Company.Core.Extension.Sql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Database
{
    public class AlarmInfo : EntityBase
    {

        /// <summary>
        /// 用户名（非空 + 唯一 + 最大长度50）
        /// </summary>
        [Required(Message = "报警信息不能为空")]
        [StringLength(50)]
        public string Info { get; set; }

        /// <summary>
        /// 角色（非空）
        /// </summary>
        [Required(Message = "等级不能为空")]
        public int Role { get; set; }

        /// <summary>
        /// 开始时间（非空）
        /// </summary>
        [Required(Message = "开始时间不能为空")]
        public DateTime CreateTime { get; set; }

        /// <summary>
        /// 结束时间（非空）
        /// </summary>
        [Required(Message = "结束时间不能为空")]
        public DateTime FinishDate { get; set; }
    }
}
