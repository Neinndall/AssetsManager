using System.Windows;
using System.Windows.Input;

namespace AssetsManager.Views.Dialogs
{
    public partial class InputDialog : Window
    {
        public string InputText
        {
            get { return textBoxInput.Text; }
            set { textBoxInput.Text = value; }
        }

        public InputDialog()
        {
            InitializeComponent();
            textBoxInput.Focus();
        }

        public void Initialize(string title, string question, string defaultAnswer = "", bool isMultiLine = false)
        {
            Title = title;
            textBlockQuestion.Text = question;
            InputText = defaultAnswer;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }
    }
}
