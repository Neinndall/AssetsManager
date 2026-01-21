using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace AssetsManager.Views.Help
{
    public partial class DocumentationView : UserControl
    {
        public DocumentationView()
        {
            InitializeComponent();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}