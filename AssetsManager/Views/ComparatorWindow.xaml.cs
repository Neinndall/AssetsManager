using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using System.Windows.Controls;
using AssetsManager.Views.Models.Comparator;

namespace AssetsManager.Views
{
    public partial class ComparatorWindow : UserControl
    {
        private readonly WadComparisonModel _viewModel;

        public ComparatorWindow(
            CustomMessageBoxService customMessageBoxService,
            WadComparatorService wadComparatorService,
            LogService logService,
            AppSettings appSettings,
            TaskCancellationManager taskCancellationManager
            )
        {
            InitializeComponent();

            _viewModel = new WadComparisonModel();
            WadComparisonControl.DataContext = _viewModel;

            // Set services for WadComparisonControl
            WadComparisonControl.CustomMessageBoxService = customMessageBoxService;
            WadComparisonControl.WadComparatorService = wadComparatorService;
            WadComparisonControl.LogService = logService;
            WadComparisonControl.AppSettings = appSettings;
            WadComparisonControl.TaskCancellationManager = taskCancellationManager;
        }
    }
}
