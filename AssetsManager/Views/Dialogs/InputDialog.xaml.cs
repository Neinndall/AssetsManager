using System.Windows;
using AssetsManager.Views.Helpers;
using Material.Icons;

namespace AssetsManager.Views.Dialogs
{
    public partial class InputDialog : HudWindow
    {
        public string InputText
        {
            get => textBoxInput.Text;
            set => textBoxInput.Text = value;
        }

        public InputDialog()
        {
            InitializeComponent();
            HeaderIcon = MaterialIconKind.TextBoxEdit;
            textBoxInput.Focus();
        }

        public void Initialize(string title, string question, string defaultAnswer = "", bool isMultiLine = false)
        {
            HeaderTitle = title;
            textBlockQuestion.Text = question;
            InputText = defaultAnswer;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
