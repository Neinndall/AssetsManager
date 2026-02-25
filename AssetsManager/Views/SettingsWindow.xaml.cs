using System;
using System.Windows;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using MahApps.Metro.Controls;

namespace AssetsManager.Views
{
    public class SettingsChangedEventArgs : EventArgs
    {
        public bool WasResetToDefaults { get; set; }
    }

    public partial class SettingsWindow : MetroWindow
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly AppSettings _appSettings;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly GeneralSettingsView _generalSettingsView;
        private readonly DefaultPathsSettingsView _defaultPathsSettingsView;
        private readonly AdvancedSettingsView _advancedSettingsView;
        private readonly SettingsModel _settingsModel;

        public event EventHandler<SettingsChangedEventArgs> SettingsChanged;

        public SettingsWindow(
            AppSettings appSettings,
            IServiceProvider serviceProvider,
            CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();

            _appSettings = appSettings;
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;

            _settingsModel = new SettingsModel { Settings = _appSettings };

            _generalSettingsView = _serviceProvider.GetRequiredService<GeneralSettingsView>();
            _defaultPathsSettingsView = _serviceProvider.GetRequiredService<DefaultPathsSettingsView>();
            _advancedSettingsView = _serviceProvider.GetRequiredService<AdvancedSettingsView>();

            _generalSettingsView.ApplySettingsToUI(_settingsModel);
            _defaultPathsSettingsView.ApplySettingsToUI(_settingsModel);
            _advancedSettingsView.ApplySettingsToUI(_settingsModel);

            SetupNavigation();
            NavigateToView(_generalSettingsView);
        }

        private void SetupNavigation()
        {
            NavGeneral.Checked += NavGeneral_Checked;
            NavDefaultPaths.Checked += NavDefaultPaths_Checked;
            NavAdvanced.Checked += NavAdvanced_Checked;
        }

        private void NavGeneral_Checked(object sender, RoutedEventArgs e) => NavigateToView(_generalSettingsView);
        private void NavDefaultPaths_Checked(object sender, RoutedEventArgs e) => NavigateToView(_defaultPathsSettingsView);
        private void NavAdvanced_Checked(object sender, RoutedEventArgs e) => NavigateToView(_advancedSettingsView);

        private void NavigateToView(object view)
        {
            SettingsContentArea.Content = view;
        }

        private void SettingsWindow_Closed(object sender, EventArgs e)
        {
            NavGeneral.Checked -= NavGeneral_Checked;
            NavDefaultPaths.Checked -= NavDefaultPaths_Checked;
            NavAdvanced.Checked -= NavAdvanced_Checked;
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            bool? result = _customMessageBoxService.ShowYesNo("Confirm Reset", "Are you sure you want to reset all settings to default values?", this);

            if (result == true)
            {
                _appSettings.ResetToDefaults();
                _appSettings.Save();
                _customMessageBoxService.ShowInfo("Info", "Settings have been reset to default values.", this);
                _settingsModel.Settings = _appSettings;
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { WasResetToDefaults = true });
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            _advancedSettingsView.SaveSettings();
            _appSettings.Save();
            _customMessageBoxService.ShowSuccess("Success", "Settings have been saved successfully.", this);
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { WasResetToDefaults = false });
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
    }
}
