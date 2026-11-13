/*
 * 文件职责与维护说明：
 * 实时报警表的一行：保存当前报警快照，并通过属性变更通知更新已有行。
 * 报警标识是复用行对象的业务键；重新收到快照时更新字段，不需要清空整张表。
 */
using Company.Application.Share.PLC.Monitoring.Models;
using Company.Application.Share.PLC.Runtime.Models;
using ReactiveUI;

namespace Company.Application.Main.Models;

/// <summary>保留行实例与选择，仅通知屏幕上确实改变的字段。</summary>
public sealed class AlarmDisplayRow(AlarmRuntimeSnapshot snapshot) : ReactiveObject
{
    private AlarmRuntimeSnapshot _snapshot = snapshot;
    public PlcVariableKey Key => _snapshot.Key;
    public string StateText => _snapshot.StateText;
    public string DefinitionText => _snapshot.DefinitionText;
    public string StartText => _snapshot.StartText;
    public string EndText => _snapshot.EndText;
    public string UpdatedText => _snapshot.UpdatedText;
    public string BoundarySource => _snapshot.BoundarySource;
    public void Apply(AlarmRuntimeSnapshot snapshot)
    {
        var old = _snapshot;
        _snapshot = snapshot;
        if (old.StateText != StateText) this.RaisePropertyChanged(nameof(StateText));
        if (old.DefinitionText != DefinitionText) this.RaisePropertyChanged(nameof(DefinitionText));
        if (old.StartText != StartText) this.RaisePropertyChanged(nameof(StartText));
        if (old.EndText != EndText) this.RaisePropertyChanged(nameof(EndText));
        if (old.UpdatedText != UpdatedText) this.RaisePropertyChanged(nameof(UpdatedText));
        if (old.BoundarySource != BoundarySource) this.RaisePropertyChanged(nameof(BoundarySource));
    }
}
