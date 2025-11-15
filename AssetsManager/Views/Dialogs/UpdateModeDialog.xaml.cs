using System.Windows;

namespace AssetsManager.Views.Dialogs
{
  public enum UpdateMode
  {
    None,
    CleanWithoutSaving,
    CleanWithSaving
  }

  public partial class UpdateModeDialog : Window
  {
    public UpdateMode SelectedMode { get; private set; } = UpdateMode.None;

    public UpdateModeDialog()
    {
      InitializeComponent();
    }

    private void CleanUpdateNoSaveButton_Click(object sender, RoutedEventArgs e)
    {
      SelectedMode = UpdateMode.CleanWithoutSaving;
      DialogResult = true;
    }

    private void CleanUpdateWithSaveButton_Click(object sender, RoutedEventArgs e)
    {
      SelectedMode = UpdateMode.CleanWithSaving;
      DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
      DialogResult = false;
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        this.DragMove();
    }
  }
}
