using System;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Comparator;

namespace AssetsManager.Views
{
    public partial class ComparatorWindow : UserControl
    {
        private readonly WadComparisonModel _viewModel;

        public ComparatorWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();

            _viewModel = new WadComparisonModel();
            WadComparisonControl.DataContext = _viewModel;

            // Set services for WadComparisonControl
            WadComparisonControl.CustomMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            WadComparisonControl.WadComparatorService = serviceProvider.GetRequiredService<WadComparatorService>();
            WadComparisonControl.LogService = serviceProvider.GetRequiredService<LogService>();
            WadComparisonControl.AppSettings = serviceProvider.GetRequiredService<AppSettings>();
            WadComparisonControl.TaskCancellationManager = serviceProvider.GetRequiredService<TaskCancellationManager>();
            WadComparisonControl.BackupManager = serviceProvider.GetRequiredService<BackupManager>();
            WadComparisonControl.VersionService = serviceProvider.GetRequiredService<VersionService>();
        }
    }
}
