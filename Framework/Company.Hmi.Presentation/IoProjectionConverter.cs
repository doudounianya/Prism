using Company.Hmi.Contracts;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Company.Hmi.Presentation;

public sealed class IoProjectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var items = values[0] as IReadOnlyList<IIoSignal> ?? [];
        var known = items.Count(item => item.Value.HasValue);
        return parameter as string == "ioOn"
            ? known == 0 ? "—" : $"{items.Count(item => item.Value == true)} / {items.Count}"
            : items.Count == 0 ? "未采集" : $"{known} 有效 / {items.Count - known} 未知";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class IoIndicatorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        IoIndicator.On => Brushes.SeaGreen,
        IoIndicator.ReadFailure => Brushes.DarkOrange,
        _ => Brushes.SlateGray
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
