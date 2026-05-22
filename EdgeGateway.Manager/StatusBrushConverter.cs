using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Manager;

internal sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            DeviceConnectionStatus.Connected => ConnectedBrush,
            DeviceConnectionStatus.Listening => ConnectedBrush,
            DeviceConnectionStatus.Error => ErrorBrush,
            DeviceConnectionStatus.Disconnected => Brushes.Transparent,
            _ => WarningBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
