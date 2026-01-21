using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Models.Controls
{
    public class StatusBarViewModel : INotifyPropertyChanged
    {
        private readonly NotificationService _notificationService;
        private string _statusText = "";
        private string _progressPercentage = null; // Default null -> Hidden via Converter

        public event PropertyChangedEventHandler PropertyChanged;

        public StatusBarViewModel()
        {
            // Resolve service manually since this ViewModel is created by the View
            _notificationService = App.ServiceProvider.GetService<NotificationService>();
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                if (_progressPercentage != value)
                {
                    _progressPercentage = value;
                    OnPropertyChanged();
                }
            }
        }

        // Método obsoleto mantenido por compatibilidad si algo externo lo llama, pero redirigido
        public void ShowNotification(bool show, string message = "")
        {
            if (show && _notificationService != null)
            {
                 _notificationService.AddNotification("Notification", message, AssetsManager.Views.Models.Notifications.NotificationType.Info);
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
