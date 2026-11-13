// 无需修改任何代码，保持原有逻辑即可
using Company.Database;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Company.Database.SqlSugar
{
    /// <summary>
    /// 会员仓储 SqlSugar 实现类（实现 IMemberRepository，内部耦合 SqlSugar）
    /// </summary>
    public class MemberRepository : IMemberRepository
    {
        private readonly ISqlSugarClient _sqlSugarClient;

        // 构造函数注入单例 ISqlSugarClient
        public MemberRepository(ISqlSugarClient sqlSugarClient)
        {
            _sqlSugarClient = sqlSugarClient ?? throw new ArgumentNullException(nameof(sqlSugarClient));
            // 自动应用约束（主键、非空、唯一等），无需额外配置
            _sqlSugarClient.CodeFirst.InitTables<Member>();
        }
        #region 实现通用 IRepository<Member> 接口方法（内部用 SqlSugar 实现）
        public bool Insert(Member entity)
        {
            if (entity == null) return false;
            var affectedRows = _sqlSugarClient.Insertable(entity).ExecuteCommand();
            return affectedRows > 0;
        }

        public bool Update(Member entity)
        {
            if (entity == null || entity.Id <= 0) return false;
            // 忽略 Id 和 InsertDate 字段，避免更新主键和创建时间
            var affectedRows = _sqlSugarClient.Updateable(entity)
                .IgnoreColumns(m => new { m.Id, m.InsertDate })
                .ExecuteCommand();
            return affectedRows > 0;
        }

        public bool Delete(Member entity)
        {
            if (entity == null || entity.Id <= 0) return false;
            var affectedRows = _sqlSugarClient.Deleteable(entity).ExecuteCommand();
            return affectedRows > 0;
        }

        public List<Member> GetAll()
        {
            return _sqlSugarClient.Queryable<Member>()
                .OrderByDescending(m => m.InsertDate)
                .ToList();
        }

        public Member GetEntity(int id)
        {
            if (id <= 0) return null;
            return _sqlSugarClient.Queryable<Member>().InSingle(id);
        }
        #endregion

        #region 实现 IMemberRepository 扩展的业务方法
        public bool Login(string username, string password, out Member member)
        {
            member = null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return false;

            // 内部使用 SqlSugar 查询，对外暴露业务结果
            member = _sqlSugarClient.Queryable<Member>()
                                    .Where(m => m.UserName == username && m.Password == password)
                                    .First();
            return member != null;
        }
        #endregion
    }
}