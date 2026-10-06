using System;
using System.Windows;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private BrowseProjectControl _projectFiles;
        private double _projectFilesHeight = 240;

        private void UpdateProjectFiles()
        {
            if (ProjectFilesRow == null || _isCleanedUp) return;
            if (_model.IsProjectFilesVisible)
            {
                if (_projectFiles == null)
                {
                    _projectFiles = new BrowseProjectControl { LogService = LogService };
                    _projectFiles.CloseRequested += OnProjectFilesCloseRequested;
                    ProjectFilesHost.Content = _projectFiles;
                }
                _projectFiles.SetProject(_model.RootPath);
                ProjectFilesRow.MinHeight = 150;
                ProjectFilesRow.Height = new GridLength(_projectFilesHeight);
            }
            else
            {
                if (ProjectFilesRow.ActualHeight >= 150) _projectFilesHeight = ProjectFilesRow.ActualHeight;
                ProjectFilesRow.MinHeight = 0;
                ProjectFilesRow.Height = new GridLength(0);
                _projectFiles?.SetProject(_model.RootPath);
            }
        }

        private void OnProjectFilesCloseRequested(object sender, EventArgs e) => _model.IsProjectFilesVisible = false;

        private void ReleaseProjectFiles()
        {
            if (_projectFiles == null) return;
            _projectFiles.CloseRequested -= OnProjectFilesCloseRequested;
            _projectFiles.Clear();
            ProjectFilesHost.Content = null;
            _projectFiles = null;
        }
    }
}
