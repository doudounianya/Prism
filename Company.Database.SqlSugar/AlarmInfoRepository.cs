using Company.Database;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Company.Database.SqlSugar
{
    /// <summary>
    /// 报警信息仓储 SqlSugar 实现类（贴合 MemberRepository 风格）
    /// </summary>
    public class AlarmInfoRepository : IAlarmInfoRepository
    {
        private readonly ISqlSugarClient _sqlSugarClient;

        // 构造函数注入 ISqlSugarClient，与 MemberRepository 一致
        public AlarmInfoRepository(ISqlSugarClient sqlSugarClient)
        {
            _sqlSugarClient = sqlSugarClient ?? throw new ArgumentNullException(nameof(sqlSugarClient));
            // 自动初始化表结构，应用实体约束
            _sqlSugarClient.CodeFirst.InitTables<AlarmInfo>();
        }

        #region 实现通用 IRepository<AlarmInfo> 接口方法（与 MemberRepository 逻辑完全一致）
        public bool Insert(AlarmInfo entity)
        {
            if (entity == null) return false;
            var affectedRows = _sqlSugarClient.Insertable(entity).ExecuteCommand();
            return affectedRows > 0;
        }

        public bool Update(AlarmInfo entity)
        {
            if (entity == null || entity.Id <= 0) return false;
            // 忽略主键 Id 和 无需更新的字段（与 MemberRepository 一致风格）
            var affectedRows = _sqlSugarClient.Updateable(entity)
                .IgnoreColumns(a => new { a.Id })
                .ExecuteCommand();
            return affectedRows > 0;
        }

        public bool Delete(AlarmInfo entity)
        {
            if (entity == null || entity.Id <= 0) return false;
            var affectedRows = _sqlSugarClient.Deleteable(entity).ExecuteCommand();
            return affectedRows > 0;
        }

        public List<AlarmInfo> GetAll()
        {
            // 按创建时间倒序查询，与 MemberRepository 一致
            return _sqlSugarClient.Queryable<AlarmInfo>()
                .OrderByDescending(a => a.CreateTime)
                .ToList();
        }

        public AlarmInfo GetEntity(int id)
        {
            if (id <= 0) return null;
            return _sqlSugarClient.Queryable<AlarmInfo>().InSingle(id);
        }
        #endregion

        #region 实现 IAlarmInfoRepository 扩展的业务方法（报警产生 + 报警结束）
        public bool CreateAlarm(AlarmInfo alarmInfo)
        {
            if (alarmInfo == null) return false;

            // 自动赋值创建时间（无需业务层手动传入，简化调用）
            // 确保满足 [Required] 特性要求，避免插入空值
            alarmInfo.CreateTime = DateTime.Now;

            // 复用 SqlSugar 插入逻辑，与通用 Insert 方法一致
            var affectedRows = _sqlSugarClient.Insertable(alarmInfo).ExecuteCommand();
            return affectedRows > 0;
        }

        public bool FinishAlarm(int id, DateTime finishDate)
        {
            if (id <= 0) return false;

            // 先查询报警是否存在，避免更新无效数据
            var existAlarm = GetEntity(id);
            if (existAlarm == null)
            {
                return false;
            }

            // 构造更新对象，仅更新结束时间（FinishDate），保持原有字段值不变
            var updateAlarm = new AlarmInfo
            {
                Id = id, // 主键定位更新记录
                Info = existAlarm.Info, // 保留原有报警信息
                Role = existAlarm.Role, // 保留原有报警等级
                CreateTime = existAlarm.CreateTime, // 保留原有创建时间
                FinishDate = finishDate // 赋值报警结束时间
            };

            // 调用通用 Update 方法完成更新，与原有逻辑保持一致
            return Update(updateAlarm);
        }
        #endregion
    }
}