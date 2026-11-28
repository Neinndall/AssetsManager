using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public sealed class AppSettingsResetTests
    {
        [Fact]
        public void ResetToDefaultsRestoresDataExportFormatAndAssetTrackerFrequency()
        {
            var settings = AppSettings.GetDefaultSettings();
            var defaultDataFormat = settings.DataExportFormat;
            var defaultFrequency = settings.AssetTrackerFrequency;

            settings.DataExportFormat = defaultDataFormat == DataExportFormat.Json
                ? DataExportFormat.Original
                : DataExportFormat.Json;
            settings.AssetTrackerFrequency = 5;

            settings.ResetToDefaults();

            Assert.Equal(defaultDataFormat, settings.DataExportFormat);
            Assert.Equal(defaultFrequency, settings.AssetTrackerFrequency);
            Assert.Equal(60, settings.AssetTrackerFrequency);
        }
    }
}
