using System.Windows.Controls;
using System.Diagnostics;

namespace AssetsManager.Views.Help
{
  public partial class BugReportsView : UserControl
  {
    public BugReportsView()
    {
      InitializeComponent();
    }

    private void buttonReportBug_Click(object sender, System.Windows.RoutedEventArgs e)
    {
      Process.Start(new ProcessStartInfo
      {
        FileName = "https://github.com/Neinndall/AssetsManager/issues",
        UseShellExecute = true
      });
    }
  }
}
