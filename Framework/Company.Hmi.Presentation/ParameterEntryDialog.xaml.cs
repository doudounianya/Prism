using Company.Hmi.Contracts;
using System.Windows;
using System.Windows.Controls;

namespace Company.Hmi.Presentation;

public partial class ParameterEntryDialog : Window
{
    private readonly ParameterEntry _entry;
    public string InputValue { get; private set; } = "";

    public ParameterEntryDialog(ParameterEntry entry)
    {
        _entry = entry;
        InitializeComponent();
        ParameterName.Text = entry.Name;
        Unit.Text = entry.Unit;
        Input.Text = entry.Draft;
        if (entry.IsText)
        {
            Keypad.Visibility = Visibility.Collapsed;
            EditKeys.Visibility = Visibility.Collapsed;
            Title = "名称输入";
        }
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
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
        if (_entry.Validate(Input.Text) is { } error) { InputError.Text = error; Input.Focus(); return; }
        InputValue = Input.Text; DialogResult = true;
    }
}
