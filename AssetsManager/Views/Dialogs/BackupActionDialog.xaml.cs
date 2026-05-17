using System.Windows;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs
{
    public enum BackupAction
    {
        None,
        Overwrite,
        Clone
    }

    public partial class BackupActionDialog : HudWindow
    {
        public BackupAction SelectedAction { get; private set; } = BackupAction.None;

        public BackupActionDialog()
        {
            InitializeComponent();
        }

        private void OverwriteButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = BackupAction.Overwrite;
            DialogResult = true;
        }

        private void CloneButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = BackupAction.Clone;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
