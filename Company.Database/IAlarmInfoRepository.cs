using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Database
{
    /// <summary>
    /// 报警信息仓储接口（贴合原有 IMemberRepository 规范）
    /// </summary>
    public interface IAlarmInfoRepository : IRepository<AlarmInfo>
    {
        /// <summary>
        /// 报警产生（新增报警记录，自动赋值创建时间）
        /// </summary>
        /// <param name="alarmInfo">报警信息实体（无需手动赋值 CreateTime）</param>
        /// <returns>是否新增成功</returns>
        bool CreateAlarm(AlarmInfo alarmInfo);

        /// <summary>
        /// 报警结束（更新报警结束时间，标记报警完成）
        /// </summary>
        /// <param name="id">报警主键ID（继承 EntityBase 的 Id）</param>
        /// <param name="finishDate">报警结束时间</param>
        /// <returns>是否更新成功</returns>
        bool FinishAlarm(int id, DateTime finishDate);
    }
}