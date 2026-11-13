/*
 * 文件职责与维护说明：
 * 参数编辑草稿与轮询快照分离：用户编辑期间，采样不会覆盖草稿。
 * 首次编辑记录来源连接代次，Revision 标识草稿版本，写入确认只能接收与提交时同版本的结果。
 */
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime.Models;
using ReactiveUI;
using System.Globalization;

namespace Company.Application.Main.Models;

/// <summary>编辑草稿独立于轮询值；第一次修改时记录连接代次，不将旧草稿写入新会话。</summary>
public sealed class PlcParameterEdit(PlcVariableBinding binding) : ReactiveObject
{
    private string _draft = "", _message = "";
    private bool _dirty, _allowed;
    private long _epoch;
    public long DraftEpoch { get; private set; }
    public long Revision { get; private set; }
    public bool IsDirty { get => _dirty; private set => this.RaiseAndSetIfChanged(ref _dirty, value); }
    public bool CanEdit => _allowed && _epoch > 0 && binding.Access.HasFlag(PlcAccess.Write) && binding.WriteMode == "参数暂存";
    public string Result { get => _message; set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string Draft
    {
        get => _draft;
        set
        {
            if (_draft == value) return;
            if (!IsDirty) DraftEpoch = _epoch;
            Revision++; this.RaiseAndSetIfChanged(ref _draft, value); IsDirty = true; Result = "待下发";
        }
    }
    public void SetAllowed(bool allowed)
    { if (_allowed == allowed) return; _allowed = allowed; this.RaisePropertyChanged(nameof(CanEdit)); }
    // 只有草稿未修改时才随有效轮询值更新文本；编辑中保留用户输入。
    // 当前读取失效时移除可编辑依据，但不隐式丢弃尚未提交的草稿。
    public void Observe(PlcVariableSnapshot? snapshot)
    {
        var epoch = snapshot is { State: PlcValueState.Valid } ? snapshot.ConnectionEpoch : 0;
        if (_epoch != epoch) { _epoch = epoch; this.RaisePropertyChanged(nameof(CanEdit)); }
        if (!IsDirty && snapshot is { State: PlcValueState.Valid })
            this.RaiseAndSetIfChanged(ref _draft, Convert.ToString(snapshot.EngineeringValue, CultureInfo.InvariantCulture) ?? "", nameof(Draft));
    }
    public void Undo() { IsDirty = false; Result = ""; Revision++; }
    // 接受的是提交时记录的草稿版本；完成期间若再次编辑，旧返回不能清除新修改。
    public void Accept(long revision)
    { if (revision == Revision) { IsDirty = false; Result = "写入并回读一致"; } }
}
