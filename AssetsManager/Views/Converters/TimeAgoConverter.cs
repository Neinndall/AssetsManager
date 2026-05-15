using System;
using System.Globalization;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class TimeAgoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                var timeSpan = DateTime.Now - dateTime.ToLocalTime();

                if (timeSpan.TotalMinutes < 1)
                    return "just now";
                if (timeSpan.TotalMinutes < 60)
                    return $"{(int)timeSpan.TotalMinutes}m ago";
                if (timeSpan.TotalHours < 24)
                    return $"{(int)timeSpan.TotalHours}h ago";
                if (timeSpan.TotalDays < 7)
                    return $"{(int)timeSpan.TotalDays}d ago";
                if (timeSpan.TotalDays < 30)
                    return $"{(int)(timeSpan.TotalDays / 7)}w ago";
                
                return dateTime.ToString("MMM dd, yyyy");
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
