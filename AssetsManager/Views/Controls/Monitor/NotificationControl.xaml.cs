using AssetsManager.Services.Core;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class NotificationControl : UserControl
    {
        public NotificationHistoryService NotificationHistoryService { get; set; }

        public NotificationControl()
        {
            InitializeComponent();
            this.Loaded += NotificationControl_Loaded;
        }

        private void NotificationControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (NotificationHistoryService != null)
            {
                this.DataContext = NotificationHistoryService;
            }
        }
    }
}