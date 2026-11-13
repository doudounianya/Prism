/*
 * 文件职责与维护说明：
 * 工业数字键盘弹窗只负责文本输入和类型/范围校验。
 * 输入确认返回一个值，由原页面携带打开弹窗时的上下文提交；取消或试输入本身不会写 PLC。
 */
using Company.Application.Main.Models;
using Company.Application.PLC.Writing;
using Company.Application.Share.PLC.Configuration;
using System.Windows;
using System.Windows.Controls;

namespace Company.Application.Main.Views;

/// <summary>只负责输入及本地校验；关闭确认后由轴页面提交一个参数。</summary>
public partial class AxisParameterInputDialog : Window
{
    private readonly PlcVariableBinding _binding;
    private readonly string? _writeBlockReason;
    public string InputValue { get; private set; } = "";
    public AxisParameterInputDialog(PlcDisplayField field, string? writeBlockReason = null, string? parameterName = null)
    {
        _binding = field.Binding;
        _writeBlockReason = writeBlockReason;
        InitializeComponent();
        ParameterName.Text = parameterName ?? field.Name; Unit.Text = field.Unit;
        Input.Text = field.Edit.Draft;
        if (writeBlockReason is not null)
        {
            WriteAvailability.Text = writeBlockReason + " 可试输入，暂不能写入。";
            WriteAvailability.Visibility = Visibility.Visible;
            ConfirmButton.IsEnabled = false;
        }
        if (_binding.DataType == PlcDataType.String)
        { Keypad.Visibility = Visibility.Collapsed; EditKeys.Visibility = Visibility.Collapsed; Title = "名称输入"; }
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }
    // 复用实际写入的值转换器验证数据类型和上下限，避免键盘与下发采用两套规则。
    // 此检查不访问 PLC，也不表示已具备身份和连接权限。
    public static bool TryValidate(PlcVariableBinding binding, string input, out string error)
    {
        try { PlcWriteValueConverter.ConvertValue(binding, input); error = ""; return true; }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { error = ex is OverflowException ? "数值超出PLC数据类型范围。" : ex.Message; return false; }
    }
    private void InputChanged(object sender, TextChangedEventArgs e)
    { if (InputError is not null) InputError.Text = ""; }
    private void DigitClicked(object sender, RoutedEventArgs e)
    { if (sender is Button { Content: string value }) { int start = Input.SelectionStart; Input.SelectedText = value; Input.CaretIndex = start + value.Length; Input.Focus(); } }
    private void SignClicked(object sender, RoutedEventArgs e)
    { Input.Text = Input.Text.StartsWith('-') ? Input.Text[1..] : "-" + Input.Text; Input.CaretIndex = Input.Text.Length; Input.Focus(); }
    private void ClearClicked(object sender, RoutedEventArgs e) { Input.Clear(); Input.Focus(); }
    private void BackspaceClicked(object sender, RoutedEventArgs e)
    {
        int start = Input.SelectionStart;
        if (Input.SelectionLength > 0) Input.SelectedText = "";
        else if (start > 0) { Input.Select(start - 1, 1); Input.SelectedText = ""; }
        Input.Focus();
    }
    // 只有本地校验成功且未被只读原因阻止才返回 DialogResult=true。
    // 写入由调用页面在弹窗关闭后执行，取消路径不会下发。
    private void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_writeBlockReason is not null) { InputError.Text = _writeBlockReason; return; }
        if (!TryValidate(_binding, Input.Text, out string error)) { InputError.Text = error; Input.Focus(); return; }
        InputValue = Input.Text; DialogResult = true;
    }
}
