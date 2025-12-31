using System.Windows.Controls;
using AssetsManager.Info;

namespace AssetsManager.Views.Help
{
    public partial class AboutView : UserControl
    {
        public string ApplicationVersion => ApplicationInfos.Version;

        public AboutView()
        {
            InitializeComponent();
            DataContext = this;
        }
    }
}