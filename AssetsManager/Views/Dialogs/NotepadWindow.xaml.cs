using System.Windows;

namespace AssetsManager.Views.Dialogs
{
    public partial class NotepadWindow : Window
    {
        public NotepadWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
    }
}
