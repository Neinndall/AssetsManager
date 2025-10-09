using Serilog;
using System;
using System.IO;
using System.Net.Http;
using System.Windows;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views;
using AssetsManager.Services.Updater;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models;
using AssetsManager.Views.Help;
using AssetsManager.Views.Settings;
using AssetsManager.Views.Controls;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Models;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Versions;

namespace AssetsManager
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        public App()
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Logging
            var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.Logger(lc => lc
                    // .Filter.ByIncludingOnly(e => e.Level < LogEventLevel.Fatal) // Information, Warning, Error (Original)
                    .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Information && e.Level < LogEventLevel.Fatal) // Information, Warning, Error (Excludes Debug)
                    .WriteTo.File("logs/application.log", 
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}"))
                .WriteTo.Logger(lc => lc
                    .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Error && e.Exception != null) // Error, Fatal and with an Exception
                    .WriteTo.File("logs/application_errors.log", 
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
                .CreateLogger();

            Log.Logger = logger; // Assign the logger to the static Log.Logger
            services.AddSingleton<ILogger>(logger);
            
            // Core Services
            services.AddSingleton<PbeStatusService>();
            services.AddSingleton<LogService>();
            services.AddSingleton<HttpClient>();
            services.AddSingleton<DirectoriesCreator>();
            services.AddSingleton(provider => AppSettings.LoadSettings());
            services.AddSingleton<Requests>();
            services.AddSingleton<AssetDownloader>();
            services.AddSingleton<JsonDataService>();
            services.AddSingleton<Status>();
            services.AddSingleton<UpdateManager>();
            services.AddSingleton<UpdateExtractor>();
            services.AddSingleton<Resources>();
            services.AddSingleton<DirectoryCleaner>();
            services.AddSingleton<BackupManager>();
            services.AddSingleton<HashCopier>();
            services.AddSingleton<UpdateCheckService>();
            services.AddSingleton<ProgressUIManager>();
            services.AddTransient<ExplorerPreviewService>();
            services.AddTransient<TreeBuilderService>();
            services.AddTransient<TreeUIManager>();
            services.AddSingleton<WadSearchBoxService>();
            services.AddTransient<CSSParserService>();
            services.AddSingleton<JsBeautifierService>();
            services.AddSingleton<ContentFormatterService>();
            services.AddSingleton<DiffViewService>();
            services.AddSingleton<MonitorService>();

            // Versions Service
            services.AddSingleton<VersionService>();

            // Hashes Services
            services.AddSingleton<HashesManager>();
            services.AddSingleton<HashResolverService>();

            // Comparator Services
            services.AddSingleton<WadComparatorService>();
            services.AddSingleton<WadDifferenceService>();
            services.AddSingleton<WadPackagingService>();
            services.AddSingleton<WadNodeLoaderService>();
            services.AddSingleton<WadExtractionService>();

            // Model Viewer Services
            services.AddSingleton<ModelLoadingService>();

            // Main Application Logic Service
            services.AddTransient<ExtractionService>();

            // Windows, Views, and Dialogs
            services.AddTransient<MainWindow>();
            services.AddTransient<HomeWindow>();
            services.AddTransient<ExplorerWindow>();
            services.AddTransient<ComparatorWindow>();
            services.AddTransient<ModelWindow>();
            services.AddTransient<MonitorWindow>();
            services.AddTransient<HelpWindow>();
            services.AddTransient<JsonDiffWindow>();
            services.AddTransient<SettingsWindow>();
            services.AddTransient<ProgressDetailsWindow>();
            services.AddTransient<UpdateProgressWindow>();
            services.AddTransient<UpdateModeDialog>();
            services.AddTransient<InputDialog>();
            services.AddTransient<ConfirmationDialog>();
            services.AddSingleton<CustomMessageBoxService>();

            // Secondary Views
            services.AddTransient<LogView>();
            services.AddTransient<GeneralSettingsView>();
            services.AddTransient<AdvancedSettingsView>();
            services.AddTransient<HashPathsSettingsView>();
            services.AddTransient<AboutView>();
            services.AddTransient<BugReportsView>();
            services.AddTransient<ChangelogsView>();
            services.AddTransient<UpdatesView>();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!SingleInstance.EnsureSingleInstance())
            {
                Shutdown();
                return;
            }

            SingleInstance.SetCurrentProcessExplicitAppUserModelID(SingleInstance.AUMID);

            base.OnStartup(e);

            var logService = ServiceProvider.GetRequiredService<LogService>();
            var customMessageBoxService = ServiceProvider.GetRequiredService<CustomMessageBoxService>();
            
            SetupGlobalExceptionHandling(logService, customMessageBoxService);

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void SetupGlobalExceptionHandling(LogService logService, CustomMessageBoxService customMessageBoxService)
        {
            DispatcherUnhandledException += (sender, args) =>
            {
                var ex = args.Exception;
                logService.LogError(ex, "An unhandled UI exception occurred. See application_errors.log for details.");

                customMessageBoxService.ShowError("Error", "A critical error occurred in the UI. Please check the logs for details.", null);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                logService.LogError(ex, "An unhandled non-UI exception occurred. See application_errors.log for details.");

                customMessageBoxService.ShowError("Error", "A critical error occurred in a background process. Please check the logs for details.", null);
            };
        }
    }
}
