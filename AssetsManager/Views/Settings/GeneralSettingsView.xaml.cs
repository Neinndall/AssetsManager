using System.Windows.Controls;
using AssetsManager.Views.Models;

namespace AssetsManager.Views.Settings
{
  public partial class GeneralSettingsView : UserControl
  {
    public GeneralSettingsView()
    {
      InitializeComponent();
    }

    public void ApplySettingsToUI(SettingsModel model)
    {
      this.DataContext = model;
    }
  }
}
