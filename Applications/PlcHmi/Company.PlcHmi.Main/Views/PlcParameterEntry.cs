using Company.Hmi.Contracts;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Shared.PLC.Configuration;

namespace Company.PlcHmi.Main.Views;

internal static class PlcParameterEntry
{
    public static ParameterEntry Create(PlcDisplayField field, string name, Func<string, Task> commit) =>
        new(name, field.Unit, field.Edit.Draft, field.Binding.DataType == PlcDataType.String,
            value => Validate(field.Binding, value), commit);

    private static string? Validate(PlcVariableBinding binding, string input)
    {
        try
        {
            PlcWriteValueConverter.ConvertValue(binding, input);
            return null;
        }
        catch (OverflowException) { return "数值超出PLC数据类型范围。"; }
        catch (ArgumentException exception) { return exception.Message; }
    }
}
