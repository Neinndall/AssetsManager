using AssetsManager.Utils;
using AssetsManager.Views.Models;
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
            AssetTrackerIntervalUnitComboBox.ItemsSource = new string[] { "Minutes", "Hours", "Days" };
            PbeIntervalUnitComboBox.ItemsSource = new string[] { "Minutes", "Hours", "Days" };

            LoadAssetTrackerIntervalSettings();
            LoadPbeIntervalSettings();
        }

        public void SaveSettings()
        {
            var model = this.DataContext as SettingsModel;
            if (model?.Settings == null) return;

            if (model.Settings.AssetTrackerTimer)
            {
                if (int.TryParse(AssetTrackerIntervalValueTextBox.Text, out int assetValue) && assetValue >= 0)
                {
                    string selectedAssetUnit = AssetTrackerIntervalUnitComboBox.SelectedItem as string;
                    if (selectedAssetUnit != null)
                    {
                        model.Settings.AssetTrackerFrequency = ConvertToMinutes(assetValue, selectedAssetUnit);
                    }
                }
            }

            if (model.Settings.CheckPbeStatus)
            {
                if (int.TryParse(PbeIntervalValueTextBox.Text, out int pbeValue) && pbeValue >= 0)
                {
                    string selectedPbeUnit = PbeIntervalUnitComboBox.SelectedItem as string;
                    if (selectedPbeUnit != null)
                    {
                        model.Settings.PbeStatusFrequency = ConvertToMinutes(pbeValue, selectedPbeUnit);
                    }
                }
            }
        }

        private int ConvertToMinutes(int value, string unit)
        {
            switch (unit)
            {
                case "Days":
                    return value * 1440;
                case "Hours":
                    return value * 60;
                case "Minutes":
                default:
                    return value;
            }
        }

        private void LoadAssetTrackerIntervalSettings()
        {
            var model = this.DataContext as SettingsModel;
            if (model?.Settings == null) return;

            int totalMinutes = model.Settings.AssetTrackerFrequency;
            var (value, unit) = ConvertFromMinutes(totalMinutes);
            AssetTrackerIntervalValueTextBox.Text = value.ToString();
            AssetTrackerIntervalUnitComboBox.SelectedItem = unit;
        }

        private void LoadPbeIntervalSettings()
        {
            var model = this.DataContext as SettingsModel;
            if (model?.Settings == null) return;

            int totalMinutes = model.Settings.PbeStatusFrequency;
            var (value, unit) = ConvertFromMinutes(totalMinutes);
            PbeIntervalValueTextBox.Text = value.ToString();
            PbeIntervalUnitComboBox.SelectedItem = unit;
        }

        private (int, string) ConvertFromMinutes(int totalMinutes)
        {
            if (totalMinutes <= 0)
            {
                return (0, "Minutes");
            }

            if (totalMinutes > 0 && totalMinutes % 1440 == 0)
            {
                return (totalMinutes / 1440, "Days");
            }
            else if (totalMinutes > 0 && totalMinutes % 60 == 0)
            {
                return (totalMinutes / 60, "Hours");
            }
            else
            {
                return (totalMinutes, "Minutes");
            }
        }
    }
}