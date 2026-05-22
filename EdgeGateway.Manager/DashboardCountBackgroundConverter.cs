using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EdgeGateway.Manager;

internal sealed class DashboardCountBackgroundConverter : IValueConverter
{
    private static readonly Brush ClearBrush = Brushes.Transparent;
    private static readonly Brush QueueBrush = new SolidColorBrush(Color.FromRgb(254, 243, 199));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(254, 226, 226));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue => (int)longValue,
            _ => 0,
        };

        if (count <= 0)
        {
            return ClearBrush;
        }

        return string.Equals(parameter?.ToString(), "error", StringComparison.OrdinalIgnoreCase)
            ? ErrorBrush
            : QueueBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
