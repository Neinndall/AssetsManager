using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class NotepadWindow : MetroWindow
    {
        public NotepadWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
    }
}
