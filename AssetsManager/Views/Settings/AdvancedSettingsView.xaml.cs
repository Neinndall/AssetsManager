using AssetsManager.Utils;
using System;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Settings
{
    public partial class AdvancedSettingsView : UserControl
    {
        private AppSettings _settings;

        public AdvancedSettingsView()
        {
            InitializeComponent();
        }

        public void ApplySettingsToUI(AppSettings settings)
        {
            _settings = settings;
            AssetTrackerIntervalUnitComboBox.ItemsSource = new string[] { "Minutes", "Hours", "Days" };
            PbeIntervalUnitComboBox.ItemsSource = new string[] { "Minutes", "Hours", "Days" };

            EnableAssetTrackerCheckBox.IsChecked = _settings.AssetTrackerTimer;
            EnablePbeStatusCheckBox.IsChecked = _settings.CheckPbeStatus;

            LoadAssetTrackerIntervalSettings();
            LoadPbeIntervalSettings();
            checkBoxMinimizeToTray.IsChecked = _settings.MinimizeToTrayOnClose;
        }

        public void SaveSettings()
        {
            if (_settings == null) return;

            // Asset Tracker Settings
            _settings.AssetTrackerTimer = EnableAssetTrackerCheckBox.IsChecked ?? false;
            if (_settings.AssetTrackerTimer)
            {
                if (int.TryParse(AssetTrackerIntervalValueTextBox.Text, out int assetValue) && assetValue >= 0)
                {
                    string selectedAssetUnit = AssetTrackerIntervalUnitComboBox.SelectedItem as string;
                    if (selectedAssetUnit != null)
                    {
                        _settings.AssetTrackerFrequency = ConvertToMinutes(assetValue, selectedAssetUnit);
                    }
                }
            }

            // PBE Status Settings
            _settings.CheckPbeStatus = EnablePbeStatusCheckBox.IsChecked ?? false;
            if (_settings.CheckPbeStatus)
            {
                if (int.TryParse(PbeIntervalValueTextBox.Text, out int pbeValue) && pbeValue >= 0)
                {
                    string selectedPbeUnit = PbeIntervalUnitComboBox.SelectedItem as string;
                    if (selectedPbeUnit != null)
                    {
                        _settings.PbeStatusFrequency = ConvertToMinutes(pbeValue, selectedPbeUnit);
                    }
                }
            }

            _settings.MinimizeToTrayOnClose = checkBoxMinimizeToTray.IsChecked ?? false;
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
            int totalMinutes = _settings.AssetTrackerFrequency;
            var (value, unit) = ConvertFromMinutes(totalMinutes);
            AssetTrackerIntervalValueTextBox.Text = value.ToString();
            AssetTrackerIntervalUnitComboBox.SelectedItem = unit;
        }

        private void LoadPbeIntervalSettings()
        {
            int totalMinutes = _settings.PbeStatusFrequency;
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