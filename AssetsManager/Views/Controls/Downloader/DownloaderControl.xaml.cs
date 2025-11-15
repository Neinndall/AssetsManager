using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Utils;
using System;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Downloader
{
  public partial class DownloaderControl : UserControl
  {
    public LogService LogService { get; set; }
    public AppSettings AppSettings { get; set; }
    public ExtractionService ExtractionService { get; set; }
    public CustomMessageBoxService CustomMessageBoxService { get; set; }
    public DirectoriesCreator DirectoriesCreator { get; set; }

    public string NewHashesPath => newHashesTextBox.Text;
    public string OldHashesPath => oldHashesTextBox.Text;

    public DownloaderControl()
    {
      InitializeComponent();
      this.Loaded += DirectorySelectionControl_Loaded;
    }

    private void DirectorySelectionControl_Loaded(object sender, RoutedEventArgs e)
    {
      // Initialize paths from saved settings
      newHashesTextBox.Text = AppSettings.NewHashesPath ?? "";
      oldHashesTextBox.Text = AppSettings.OldHashesPath ?? "";

      // Store the initial loaded path in the Tag property for later comparison
      newHashesTextBox.Tag = newHashesTextBox.Text;
      oldHashesTextBox.Tag = oldHashesTextBox.Text;
    }

    public void UpdateSettings(AppSettings newSettings, bool wasResetToDefaults)
    {
      AppSettings = newSettings;

      UpdatePathTextBox(newHashesTextBox, AppSettings.NewHashesPath, wasResetToDefaults);
      UpdatePathTextBox(oldHashesTextBox, AppSettings.OldHashesPath, wasResetToDefaults);
    }

    private void UpdatePathTextBox(TextBox textBox, string newSettingPath, bool wasResetToDefaults)
    {
      bool isPathChangedInSession = (textBox.Text != (textBox.Tag as string));

      if (wasResetToDefaults || !isPathChangedInSession)
      {
        textBox.Text = newSettingPath ?? "";
        textBox.Tag = textBox.Text;
      }
    }

    private void btnSelectNewHashesDirectory_Click(object sender, RoutedEventArgs e)
    {
      using (var folderBrowserDialog = new CommonOpenFileDialog
      {
        IsFolderPicker = true,
        Title = "Select new hashes directory",
        InitialDirectory = DirectoriesCreator.HashesNewPath

      })
      {
        if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
          newHashesTextBox.Text = folderBrowserDialog.FileName;
          LogService.LogDebug($"New Hashes Directory selected: {folderBrowserDialog.FileName}");
        }
      }
    }

    private void btnSelectOldHashesDirectory_Click(object sender, RoutedEventArgs e)
    {
      using (var folderBrowserDialog = new CommonOpenFileDialog
      {
        IsFolderPicker = true,
        Title = "Select old hashes directory",
        InitialDirectory = DirectoriesCreator.HashesOldPath
      })
      {
        if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
          oldHashesTextBox.Text = folderBrowserDialog.FileName;
          LogService.LogDebug($"Old Hashes Directory selected: {folderBrowserDialog.FileName}");
        }
      }
    }

    private async void startButton_Click(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(OldHashesPath) || string.IsNullOrEmpty(NewHashesPath))
      {
        LogService.LogWarning("Please select both hash directories.");
        CustomMessageBoxService.ShowWarning("Warning", "Please select both hash directories.", Window.GetWindow(this));
        return;
      }

      await ExtractionService.ExecuteAsync(OldHashesPath, NewHashesPath);
    }
  }
}
