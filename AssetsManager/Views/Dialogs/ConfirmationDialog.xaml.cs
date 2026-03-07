using System.Windows;
using AssetsManager.Views.Helpers;
using Material.Icons;

namespace AssetsManager.Views.Dialogs
{
    public enum CustomMessageBoxIcon
    {
        None,
        Info,
        Question,
        Warning,
        Error,
        Success
    }

    public enum CustomMessageBoxButtons
    {
        YesNo,
        OK
    }

    public partial class ConfirmationDialog : HudWindow
    {
        public ConfirmationDialog()
        {
            InitializeComponent();
        }

        public void Initialize(string title, string message, CustomMessageBoxButtons buttons = CustomMessageBoxButtons.YesNo, CustomMessageBoxIcon icon = CustomMessageBoxIcon.None)
        {
            HeaderTitle = title;
            textBlockMessage.Text = message;

            if (buttons == CustomMessageBoxButtons.OK)
            {
                YesNoButtons.Visibility = Visibility.Collapsed;
                btnOk.Visibility = Visibility.Visible;
            }
            else
            {
                YesNoButtons.Visibility = Visibility.Visible;
                btnOk.Visibility = Visibility.Collapsed;
            }

            switch (icon)
            {
                case CustomMessageBoxIcon.Info:
                    HeaderIcon = MaterialIconKind.Information;
                    break;
                case CustomMessageBoxIcon.Question:
                    HeaderIcon = MaterialIconKind.QuestionMarkCircle;
                    break;
                case CustomMessageBoxIcon.Warning:
                    HeaderIcon = MaterialIconKind.Warning;
                    break;
                case CustomMessageBoxIcon.Error:
                    HeaderIcon = MaterialIconKind.Error;
                    break;
                case CustomMessageBoxIcon.Success:
                    HeaderIcon = MaterialIconKind.CheckCircle;
                    break;
                default:
                    HeaderIcon = MaterialIconKind.Information; // Default icon
                    break;
            }
        }

        private void btnYes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void btnNo_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void btnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
