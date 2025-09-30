using System;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Downloads
{
    public class ExtractionService
    {
        private readonly HashesManager _hashesManager;
        private readonly Resources _resources;
        private readonly DirectoryCleaner _directoryCleaner;
        private readonly BackupManager _backupManager;
        private readonly HashCopier _hashCopier;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AppSettings _appSettings;
        private readonly CustomMessageBoxService _customMessageBoxService;

        public ExtractionService(
            HashesManager hashesManager,
            Resources resources,
            DirectoryCleaner directoryCleaner,
            BackupManager backupManager,
            HashCopier hashCopier,
            LogService logService,
            DirectoriesCreator directoriesCreator,
            AppSettings appSettings,
            CustomMessageBoxService customMessageBoxService)
        {
            _hashesManager = hashesManager;
            _resources = resources;
            _directoryCleaner = directoryCleaner;
            _backupManager = backupManager;
            _hashCopier = hashCopier;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _appSettings = appSettings;
            _customMessageBoxService = customMessageBoxService;
        }

        public async Task ExecuteAsync(string oldHashesPath, string newHashesPath)
        {
            try
            {
                int diffCount = await _hashesManager.CompareHashesAsync(oldHashesPath, newHashesPath);

                if (diffCount == 0)
                {
                    return;
                }

                await _resources.GetResourcesFiles();
                _directoryCleaner.CleanEmptyDirectories();
                await _backupManager.HandleBackUpAsync(_appSettings.CreateBackUpOldHashes);
                await _hashCopier.HandleCopyAsync(_appSettings.AutoCopyHashes);
                // No pondremos ningun log aqui
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "A critical error occurred during the extraction process");
            }
        }
    }
}
