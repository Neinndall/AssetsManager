using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class InputDialogTests
    {
        [Fact]
        public void ApplyMultiLineModeConfiguresAcceptsReturnAndWrapping()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var textBox = new TextBox();

                    InputDialog.ApplyMultiLineMode(textBox, isMultiLine: false);
                    Assert.False(textBox.AcceptsReturn);
                    Assert.Equal(TextWrapping.NoWrap, textBox.TextWrapping);

                    InputDialog.ApplyMultiLineMode(textBox, isMultiLine: true);
                    Assert.True(textBox.AcceptsReturn);
                    Assert.Equal(TextWrapping.Wrap, textBox.TextWrapping);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
