using SqlSugar;
using Company.Database.SqlSugar.Infrastructure;
using Company.PlcHmi.Shared.Persistence.Entities;
namespace Company.PlcHmi.Persistence.Journal;

/// <summary>只创建独立工作会话；从不调用 CodeFirst 或执行建表脚本。</summary>
public sealed class HistoryTransaction(ISqlSugarSessionFactory sessions)
{
    public ISqlSugarClient Open()
        => Open(CancellationToken.None);

    /// <summary>取消传给本次独立会话的 SQL 执行器，不影响其他查询或账户服务。</summary>
    public ISqlSugarClient Open(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var db=sessions.Open();
        if(db.CurrentConnectionConfig.DbType!=DbType.MySql)
        {
            db.Dispose();
            throw new NotSupportedException("PLC 历史存储使用 MySQL 专用 SQL，不能切换为其他数据库驱动。");
        }
        // 历史实体用 string 保存 CHAR(36) 标识；禁止驱动自动转为 Guid，避免 ORM 读取失败。
        // 只设置独立工作会话，不更改共享客户端或既有业务连接。
        try
        {
            var connection=new System.Data.Common.DbConnectionStringBuilder{ConnectionString=db.CurrentConnectionConfig.ConnectionString};
            connection["GuidFormat"]="None";
            db.CurrentConnectionConfig.ConnectionString=connection.ConnectionString;
            db.Ado.CommandTimeOut=5;
            db.Ado.CancellationToken=token;
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    public async Task<long> GetCommittedAsync(string streamId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db=Open(token);
        var row=await db.Queryable<PlcPersistenceCheckpoint>().AS("plc_persistence_checkpoint")
            .Where(x=>x.StreamId==streamId).FirstAsync();
        return row?.LastCommittedSequence??0;
    }
    public async Task<bool> ApplyAsync(string streamId,long sequence,Func<ISqlSugarClient,Task> apply,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if(!Guid.TryParse(streamId,out _)||sequence<1)throw new ArgumentException("日志流或序号无效。");
        token.ThrowIfCancellationRequested();using var db=Open(token);
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Ado.ExecuteCommandAsync("INSERT IGNORE INTO plc_persistence_checkpoint (StreamId,LastCommittedSequence,UpdatedUtc) VALUES (@stream,0,@now)",
                new SugarParameter("@stream",streamId),new SugarParameter("@now",DateTime.UtcNow));
            var checkpoint=(await db.Ado.SqlQueryAsync<PlcPersistenceCheckpoint>(
                "SELECT StreamId,LastCommittedSequence,UpdatedUtc FROM plc_persistence_checkpoint WHERE StreamId=@stream FOR UPDATE",
                new SugarParameter("@stream",streamId))).Single();
            if(sequence<=checkpoint.LastCommittedSequence){await db.Ado.RollbackTranAsync();return true;}
            if(sequence!=checkpoint.LastCommittedSequence+1)throw new InvalidOperationException("提交位置不连续；请保留日志核对数据库恢复点。");
            token.ThrowIfCancellationRequested();await apply(db);token.ThrowIfCancellationRequested();
            await db.Updateable(new PlcPersistenceCheckpoint{StreamId=streamId,LastCommittedSequence=sequence,UpdatedUtc=DateTime.UtcNow})
                .AS("plc_persistence_checkpoint").Where(x=>x.StreamId==streamId).ExecuteCommandAsync();
            await db.Ado.CommitTranAsync();return false;
        }
        catch (Exception operationError)
        {
            // 回滚本身也可能因断线失败；保留提交异常和回滚异常，不能把最初的故障覆盖掉。
            // 调用方据此继续保留本地日志，恢复后仍按检查点决定是否重放。
            // 原操作已取消时，回滚使用独立令牌；不能因取消跳过事务清理。
            try { db.Ado.CancellationToken=CancellationToken.None; await db.Ado.RollbackTranAsync(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("历史提交与回滚均失败，请保留本地日志并核对数据库恢复点。", operationError, rollbackError);
            }
            throw;
        }
    }
}
