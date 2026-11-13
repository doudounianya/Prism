using SqlSugar;
using Company.Database.PLC.Entities;
namespace Company.Database.SqlSugar.PLC;

/// <summary>只创建独立工作会话；从不调用 CodeFirst 或执行建表脚本。</summary>
public sealed class PlcHistoryDbSessionFactory(ISqlSugarClient root)
{
    public ISqlSugarClient Open()
    {
        var db=root.CopyNew();
        // 历史实体用 string 保存 CHAR(36) 标识；禁止驱动自动转为 Guid，避免 ORM 读取失败。
        // 只设置独立工作会话，不更改共享客户端或既有业务连接。
        var connection=new System.Data.Common.DbConnectionStringBuilder{ConnectionString=db.CurrentConnectionConfig.ConnectionString};
        connection["GuidFormat"]="None";
        db.CurrentConnectionConfig.ConnectionString=connection.ConnectionString;
        db.Ado.CommandTimeOut=5;
        return db;
    }
    public async Task<long> GetCommittedAsync(string streamId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db=Open();
        var row=await db.Queryable<PlcPersistenceCheckpoint>().AS("plc_persistence_checkpoint")
            .Where(x=>x.StreamId==streamId).FirstAsync();
        return row?.LastCommittedSequence??0;
    }
    public async Task<bool> ApplyAsync(string streamId,long sequence,Func<ISqlSugarClient,Task> apply,CancellationToken token)
    {
        if(!Guid.TryParse(streamId,out _)||sequence<1)throw new ArgumentException("日志流或序号无效。");
        token.ThrowIfCancellationRequested();using var db=Open();
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
        catch {await db.Ado.RollbackTranAsync();throw;}
    }
}
