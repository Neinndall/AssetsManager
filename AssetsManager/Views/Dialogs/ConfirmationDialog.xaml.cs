using System.Windows;
using System.Windows.Input;
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

    public partial class ConfirmationDialog : Window
    {
        public ConfirmationDialog()
        {
            InitializeComponent();
        }

        public void Initialize(string title, string message, CustomMessageBoxButtons buttons = CustomMessageBoxButtons.YesNo, CustomMessageBoxIcon icon = CustomMessageBoxIcon.None)
        {
            Title = title;
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
                    iconType.Kind = MaterialIconKind.Information;
                    break;
                case CustomMessageBoxIcon.Question:
                    iconType.Kind = MaterialIconKind.QuestionMarkCircle;
                    break;
                case CustomMessageBoxIcon.Warning:
                    iconType.Kind = MaterialIconKind.Warning;
                    break;
                case CustomMessageBoxIcon.Error:
                    iconType.Kind = MaterialIconKind.Error;
                    break;
                case CustomMessageBoxIcon.Success:
                    iconType.Kind = MaterialIconKind.CheckCircle;
                    break;
                default:
                    iconType.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void btnYes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void btnNo_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }
    }
}
