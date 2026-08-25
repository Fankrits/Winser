using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Winser.Helpers;

namespace Winser.Converters;

/// <summary>true -&gt; Visible. Pass "Invert" as the converter parameter to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (IsInverted(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var visible = value is Visibility.Visible;
        return IsInverted(parameter) ? !visible : visible;
    }

    internal static bool IsInverted(object parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Collapses when the bound value is null, blank, an empty collection, or a zero count.
/// Bind to <c>Collection.Count</c> rather than the collection itself when the list can change:
/// ObservableCollection raises PropertyChanged for Count, but the collection reference never does.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasContent = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int count => count > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (BoolToVisibilityConverter.IsInverted(parameter))
        {
            hasContent = !hasContent;
        }

        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DateTimeOffset when parameter is string p && p.Equals("Day", StringComparison.OrdinalIgnoreCase) =>
            Format.DayBucket((DateTimeOffset)value),
        DateTimeOffset stamp => Format.RelativeTime(stamp),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
