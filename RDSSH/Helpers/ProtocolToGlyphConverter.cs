using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using System;

namespace RDSSH.Helpers
{
    public class ProtocolToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;

            var protocol = value.ToString();
            if (string.IsNullOrEmpty(protocol)) return string.Empty;

            // Simple mapping: return short glyph/text for protocols
            return protocol.ToUpperInvariant() switch
            {
                "RDP" => "??",
                "SSH" => "??",
                _ => protocol
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
