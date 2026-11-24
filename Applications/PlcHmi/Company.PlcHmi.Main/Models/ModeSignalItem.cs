using Company.Hmi.Contracts;
using ReactiveUI;

namespace Company.PlcHmi.Main.Models;

/// <summary>
/// 模式页下方的一项只读反馈。操作含义、显示位值和读取状态分开呈现：
/// 有旧值时可保留文字与颜色，但以“缓存”说明其来源；未知或坏质量不能显示成 False。
/// 此模型不包含命令、写入资格或 PLC 访问，不能作为设备操作的判断依据。
/// </summary>
public sealed class ModeSignalItem(string name, string suffix, string plcMember, string trueText, string falseText) : ReactiveObject, IModeRequestRow
{
    public string Name { get; } = name;
    public string Suffix { get; } = suffix;
    public string PlcMember { get; } = plcMember;
    private bool? _displayValue;
    private bool _isCached;
    private string _valueText = "未知", _qualityText = "等待读取", _details = "";
    public bool? DisplayValue
    {
        get => _displayValue;
        private set
        {
            if (_displayValue == value) return;
            this.RaiseAndSetIfChanged(ref _displayValue, value);
            this.RaisePropertyChanged(nameof(IsOn));
        }
    }
    public bool IsOn => DisplayValue == true;
    public bool IsCached { get => _isCached; private set => this.RaiseAndSetIfChanged(ref _isCached, value); }
    public string ValueText { get => _valueText; private set => this.RaiseAndSetIfChanged(ref _valueText, value); }
    public string QualityText { get => _qualityText; private set => this.RaiseAndSetIfChanged(ref _qualityText, value); }
    public string Details { get => _details; private set => this.RaiseAndSetIfChanged(ref _details, value); }

    /// <summary>只更新已有行的属性；contextReason 仅说明页面/通讯状态，不清除缓存颜色。</summary>
    internal void Update(PlcDisplayField? field, string contextReason)
    {
        DisplayValue = field?.DisplayBooleanValue;
        ValueText = DisplayValue switch { true => trueText, false => falseText, _ => "未知" };
        bool cached = false;
        if (field is null || !field.CanRead) QualityText = "未配置／未启用";
        else if (contextReason.Length > 0)
        {
            cached = DisplayValue.HasValue;
            QualityText = (cached ? "缓存 · " : "") + contextReason;
        }
        else if (field.State.StartsWith("写入成功", StringComparison.Ordinal)) QualityText = "写入已确认，待回读";
        else if (field.HasCurrentValue && field.BooleanValue.HasValue) QualityText = "有效读取";
        else if (field.State.Contains("读取失败", StringComparison.Ordinal)) QualityText = "读取失败";
        else if (field.State.Contains("质量不确定", StringComparison.Ordinal)) QualityText = "质量不确定";
        else if (field.State.Contains("数据过期", StringComparison.Ordinal)) QualityText = "数据过期";
        else if (field.HasCurrentValue) QualityText = "值不可用";
        else
        {
            cached = DisplayValue.HasValue;
            QualityText = cached ? "缓存 · 等待刷新" : "等待读取";
        }
        IsCached = cached;
        // 技术下标与更新时间只放在详情提示中，常显区域不再重复堆放时间和原始布尔文字。
        string bit = DisplayValue switch { true => "True", false => "False", _ => "未知" };
        Details = $"PLC 字段：{PlcMember}\n显示位值：{bit}\n读取状态：{QualityText}\n最近有效读取：{field?.Time ?? "—"}";
    }
}
