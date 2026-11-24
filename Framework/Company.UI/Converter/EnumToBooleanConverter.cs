using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace Company.UI.Converter
{
    /// <summary>
    /// 枚举值与指定参数匹配时返回 true，支持枚举参数和 XAML 字符串参数。
    /// </summary>
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Matches(value, parameter);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true && TryGetParameter(targetType, parameter, out var result)
                ? result
                : Binding.DoNothing;
        }

        internal static bool Matches(object value, object parameter)
        {
            if (value == null || value == DependencyProperty.UnsetValue || value == Binding.DoNothing)
                return false;

            if (value.GetType().IsEnum)
                return TryGetParameter(value.GetType(), parameter, out var result) && value.Equals(result);

            return value.Equals(parameter);
        }

        private static bool TryGetParameter(Type targetType, object parameter, out object result)
        {
            result = Binding.DoNothing;
            if (targetType == null) return false;
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (!enumType.IsEnum) return false;
            if (parameter != null && parameter.GetType() == enumType)
            {
                result = parameter;
                return true;
            }
            if (parameter is string text && Enum.TryParse(enumType, text, true, out var parsed))
            {
                result = parsed!;
                return true;
            }
            return false;
        }
    }
}
