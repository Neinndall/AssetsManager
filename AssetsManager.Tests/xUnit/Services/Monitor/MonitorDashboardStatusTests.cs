using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using Material.Icons;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class MonitorDashboardStatusTests
{
    [Theory]
    [InlineData("Maintenance started at 14:30", "Maintenance", "System Alert", MaterialIconKind.Wrench)]
    [InlineData("Maintenance detected", "Maintenance", "System Alert", MaterialIconKind.Wrench)]
    [InlineData("Login service unavailable", "Service Alert", "Action Required", MaterialIconKind.ServerNetworkOff)]
    public void ServerAndGlobalStatusUpdateAndRecoverTogether(string message, string serverStatus,
        string globalStatus, MaterialIconKind icon)
    {
        var settings = AppSettings.GetDefaultSettings();
        // Exercise status refresh without starting background monitoring services.
        var model = (MonitorDashboardModel)RuntimeHelpers.GetUninitializedObject(typeof(MonitorDashboardModel));
        typeof(MonitorDashboardModel).GetField("_appSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, settings);
        int notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        settings.LastPbeStatusMessage = message;
        model.RefreshPbeData();
        Assert.Equal(serverStatus, model.PbeStatusText);
        Assert.Equal(globalStatus, model.GlobalStatusText);
        Assert.Equal(icon, model.PbeStatusIconKind);
        Assert.Equal(((SolidColorBrush)model.PbeStatusColor).Color, ((SolidColorBrush)model.GlobalStatusColor).Color);
        Assert.True(notifications > 0);

        settings.LastPbeStatusMessage = "ONLINE";
        model.RefreshPbeData();
        Assert.Equal("Online", model.PbeStatusText);
        Assert.Equal("System Nominal", model.GlobalStatusText);
        Assert.Equal(MaterialIconKind.ServerNetwork, model.PbeStatusIconKind);
        Assert.Equal(MaterialIconKind.ShieldCheckOutline, model.GlobalStatusIconKind);
    }
}
