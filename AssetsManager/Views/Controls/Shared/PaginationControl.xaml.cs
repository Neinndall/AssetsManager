using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Controls.Shared
{
    public partial class PaginationControl : UserControl
    {
        public static readonly DependencyProperty PaginatorProperty =
            DependencyProperty.Register(
                nameof(Paginator),
                typeof(IPaginationModel),
                typeof(PaginationControl),
                new PropertyMetadata(null));

        public IPaginationModel Paginator
        {
            get => (IPaginationModel)GetValue(PaginatorProperty);
            set => SetValue(PaginatorProperty, value);
        }

        public PaginationControl()
        {
            InitializeComponent();
        }

        private void FirstPage_Click(object sender, RoutedEventArgs e)
        {
            Paginator?.GoToFirstPage();
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            Paginator?.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            Paginator?.NextPage();
        }

        private void LastPage_Click(object sender, RoutedEventArgs e)
        {
            Paginator?.GoToLastPage();
        }
    }
}
