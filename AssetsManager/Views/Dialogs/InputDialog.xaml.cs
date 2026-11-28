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
            Loaded += (_, _) =>
            {
                textBoxInput.Focus();
                textBoxInput.SelectAll();
            };
        }

        internal static void ApplyMultiLineMode(System.Windows.Controls.TextBox textBox, bool isMultiLine)
        {
            textBox.AcceptsReturn = isMultiLine;
            textBox.TextWrapping = isMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }

        public void Initialize(string title, string question, string defaultAnswer = "", bool isMultiLine = false)
        {
            HeaderTitle = title;
            textBlockQuestion.Text = question;
            ApplyMultiLineMode(textBoxInput, isMultiLine);
            InputText = defaultAnswer;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
