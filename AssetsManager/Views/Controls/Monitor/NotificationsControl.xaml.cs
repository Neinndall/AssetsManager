using AssetsManager.Services.Core;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class NotificationsControl : UserControl
    {
        public NotificationsHistoryService NotificationsHistoryService { get; set; }

        public NotificationsControl()
        {
            InitializeComponent();
            this.Loaded += NotificationsControl_Loaded;
        }

        private void NotificationsControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (NotificationsHistoryService != null)
            {
                this.DataContext = NotificationsHistoryService;
            }
        }
    }
}