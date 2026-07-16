using System;
using System.IO;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.BenchmarkTests.Utils
{
    public sealed class AppSettingsTests
    {
        [Fact]
        public void SaveCreatesValidPrimaryAndBackupFiles()
        {
            using var fixture = new SettingsFixture();
            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = "first-value";

            settings.Save(fixture.ConfigPath);

            Assert.Equal("first-value", AppSettings.LoadSettings(fixture.ConfigPath).LolPbeDirectory);
            Assert.True(File.Exists(fixture.ConfigPath + ".bak"));
            Assert.False(File.Exists(fixture.ConfigPath + ".tmp"));
        }

        [Fact]
        public void CorruptPrimaryRecoversLastKnownGoodBackup()
        {
            using var fixture = new SettingsFixture();
            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = "known-good";
            settings.Save(fixture.ConfigPath);
            settings.LolPbeDirectory = "new-primary";
            settings.Save(fixture.ConfigPath);
            File.WriteAllText(fixture.ConfigPath, "{ interrupted");

            AppSettings recovered = AppSettings.LoadSettings(fixture.ConfigPath);

            Assert.Equal("known-good", recovered.LolPbeDirectory);
            Assert.Equal("known-good", AppSettings.LoadSettings(fixture.ConfigPath).LolPbeDirectory);
        }

        [Fact]
        public void InterruptedTemporaryWriteDoesNotReplaceValidPrimary()
        {
            using var fixture = new SettingsFixture();
            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = "preserved";
            settings.Save(fixture.ConfigPath);
            File.WriteAllText(fixture.ConfigPath + ".tmp", "{ partial");

            AppSettings loaded = AppSettings.LoadSettings(fixture.ConfigPath);

            Assert.Equal("preserved", loaded.LolPbeDirectory);
            Assert.False(File.Exists(fixture.ConfigPath + ".tmp"));
        }

        [Fact]
        public void CorruptPrimaryAndBackupFallBackToPersistedDefaults()
        {
            using var fixture = new SettingsFixture();
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConfigPath));
            File.WriteAllText(fixture.ConfigPath, "invalid primary");
            File.WriteAllText(fixture.ConfigPath + ".bak", "invalid backup");

            AppSettings loaded = AppSettings.LoadSettings(fixture.ConfigPath);

            Assert.Equal(10, loaded.UpdateCheckFrequency);
            Assert.Equal(10, AppSettings.LoadSettings(fixture.ConfigPath).UpdateCheckFrequency);
        }

        private sealed class SettingsFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), $"AssetsManager_Settings_{Guid.NewGuid():N}");
            internal string ConfigPath => Path.Combine(_root, "config.json");

            public void Dispose()
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
        }
    }
}
