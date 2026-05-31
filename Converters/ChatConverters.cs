using System.Globalization;

namespace AIAgentLocal.Converters;

/// <summary>
/// Converts IsUser bool to HorizontalOptions (End for user, Start for AI).
/// </summary>
public class BoolToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? LayoutOptions.End : LayoutOptions.Start;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts IsUser to background color. Adapts to dark/light mode.
/// </summary>
public class MessageBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        if (values.Length > 0 && values[0] is bool isUser)
        {
            if (isUser)
                return Color.FromArgb("#B06AB3"); // Pink-purple for user (matches logo)
            else
                return isDark ? Color.FromArgb("#2A2040") : Color.FromArgb("#F5E6F7"); // Lavender AI bubble
        }
        return isDark ? Color.FromArgb("#2A2040") : Color.FromArgb("#F5E6F7");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts IsUser to text color. Adapts to dark/light mode.
/// </summary>
public class BoolToTextColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return value is true
            ? Colors.White  // User text always white (on purple bg)
            : isDark ? Color.FromArgb("#E0E0E0") : Color.FromArgb("#1A1A1A"); // AI text
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Returns true if string is not null/empty (for IsVisible binding on stats label).
/// </summary>
public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
