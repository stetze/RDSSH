using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Xaml;
using System;

namespace RDSSH.Helpers
{
    public class BooleanToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool flag = false;
            if (value is bool b) flag = b;

            if (flag)
            {
                return new SolidColorBrush(Color.FromArgb(0xFF, 0xD6, 0xF5, 0xD6)); // light green
            }
            else
            {
                return new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
