using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Services.Core
{
    public class CustomMessageBoxService
    {
        private readonly IServiceProvider _serviceProvider;

        public CustomMessageBoxService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        private void SetOwnerSafely(Window dialog, Window owner)
        {
            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }
        }

        public bool? ShowYesNo(
            string title,
            string message,
            Window owner,
            CustomMessageBoxIcon icon = CustomMessageBoxIcon.Question)
        {
            var dialog = _serviceProvider.GetRequiredService<ConfirmationDialog>();
            dialog.Initialize(title, message, CustomMessageBoxButtons.YesNo, icon);
            SetOwnerSafely(dialog, owner);
            return dialog.ShowDialog();
        }

        public void ShowInfo(
            string title,
            string message,
            Window owner,
            CustomMessageBoxIcon icon = CustomMessageBoxIcon.Info)
        {
            var dialog = _serviceProvider.GetRequiredService<ConfirmationDialog>();
            dialog.Initialize(title, message, CustomMessageBoxButtons.OK, icon);
            SetOwnerSafely(dialog, owner);
            dialog.ShowDialog();
        }

        public void ShowSuccess(
            string title,
            string message,
            Window owner,
            CustomMessageBoxIcon icon = CustomMessageBoxIcon.Success)
        {
            var dialog = _serviceProvider.GetRequiredService<ConfirmationDialog>();
            dialog.Initialize(title, message, CustomMessageBoxButtons.OK, icon);
            SetOwnerSafely(dialog, owner);
            dialog.ShowDialog();
        }

        public void ShowError(
            string title,
            string message,
            Window owner,
            CustomMessageBoxIcon icon = CustomMessageBoxIcon.Error)
        {
            var dialog = _serviceProvider.GetRequiredService<ConfirmationDialog>();
            dialog.Initialize(title, message, CustomMessageBoxButtons.OK, icon);
            SetOwnerSafely(dialog, owner);
            dialog.ShowDialog();
        }

        public void ShowWarning(
            string title,
            string message,
            Window owner,
            CustomMessageBoxIcon icon = CustomMessageBoxIcon.Warning)
        {
            var dialog = _serviceProvider.GetRequiredService<ConfirmationDialog>();
            dialog.Initialize(title, message, CustomMessageBoxButtons.OK, icon);
            SetOwnerSafely(dialog, owner);
            dialog.ShowDialog();
        }
    }
}
