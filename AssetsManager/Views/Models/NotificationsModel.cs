
using System;
using System.ComponentModel;

namespace AssetsManager.Views.Models
{
    public class NotificationsModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public DateTime Timestamp { get; set; }
        public string Message { get; set; }

        public bool IsNew => (DateTime.Now - Timestamp).TotalMinutes < 5;

        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
