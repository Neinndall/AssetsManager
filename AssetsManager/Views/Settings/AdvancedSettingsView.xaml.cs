using AssetsManager.Utils;
using AssetsManager.Views.Models.Shared;
using System;
using System.Windows.Controls;

namespace AssetsManager.Views.Settings
{
    public partial class AdvancedSettingsView : UserControl
    {
        public AdvancedSettingsView()
        {
            InitializeComponent();
        }

        public void ApplySettingsToUI(SettingsModel model)
        {
            this.DataContext = model;
        }

        public void SaveSettings()
        {
        }
    }
}
