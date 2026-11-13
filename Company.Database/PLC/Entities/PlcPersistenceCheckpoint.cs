namespace Company.Database.PLC.Entities;

/// <summary>PLC 业务持久化实体；不改变旧 EntityBase 和通用仓储。</summary>
public sealed record PlcPersistenceCheckpoint
{
    public string StreamId {get;set;} = "";
    public long LastCommittedSequence {get;set;}
    public DateTime UpdatedUtc {get;set;}
}
