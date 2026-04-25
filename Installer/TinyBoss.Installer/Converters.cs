using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TinyBoss.Installer;

public class PageConverter : IValueConverter
{
    public static readonly PageConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is WizardPage page && parameter is string name)
            return Enum.TryParse<WizardPage>(name, out var target) && page == target;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StatusToIconConverter : IValueConverter
{
    public static readonly StatusToIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CheckStatus s ? s switch
        {
            CheckStatus.NotChecked => "⬜",
            CheckStatus.Checking   => "🔍",
            CheckStatus.Found      => "✅",
            CheckStatus.Missing    => "❌",
            CheckStatus.Installing => "⏳",
            CheckStatus.Installed  => "✅",
            CheckStatus.Failed     => "⚠️",
            CheckStatus.Skipped    => "⏭",
            _ => "?",
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StatusToBoolConverter : IValueConverter
{
    public static readonly StatusToBoolConverter IsInstalling = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CheckStatus.Installing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
