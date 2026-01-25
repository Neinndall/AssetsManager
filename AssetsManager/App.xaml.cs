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
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Help;
using AssetsManager.Views.Settings;
using AssetsManager.Views.Controls;
using AssetsManager.Views.Dialogs.Controls;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Models;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Backup;

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
      services.AddSingleton<LogService>();
      services.AddSingleton<NotificationService>();
      services.AddSingleton<TaskCancellationManager>(); // Added for managing task cancellations
      services.AddSingleton<DiffViewService>();
      services.AddSingleton<CustomMessageBoxService>();
      services.AddSingleton<ProgressUIManager>();
      services.AddSingleton<UpdateCheckService>();

      // Configure and register HttpClient for LCU API (ignoring self-signed cert for loopback)
      services.AddSingleton<HttpClient>(sp =>
      {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
          {
            // Trust the self-signed certificate for the local LCU API
            if (message.RequestUri.IsLoopback)
            {
              return true;
            }
                // For all other requests, use the default system validation
            return errors == System.Net.Security.SslPolicyErrors.None;
          };
        return new HttpClient(handler);
      });

      // Utils Services
      services.AddSingleton(provider => AppSettings.LoadSettings());
      services.AddSingleton<DirectoriesCreator>();
      services.AddSingleton<BackupManager>();

      // Updater Services
      services.AddSingleton<UpdateManager>();
      services.AddSingleton<UpdateExtractor>();
      
      // Formatting Services
      services.AddTransient<CSSParserService>();
      services.AddSingleton<JsBeautifierService>();
      services.AddSingleton<JsonFormatterService>();
      services.AddSingleton<ContentFormatterService>();
      services.AddSingleton<AudioConversionService>();
 
      // Explorer Services
      services.AddTransient<ExplorerPreviewService>();
      services.AddSingleton<WadSavingService>();
      services.AddSingleton<WadExtractionService>();
      services.AddSingleton<WadNodeLoaderService>();
      services.AddSingleton<WadSearchBoxService>();
      services.AddTransient<TreeBuilderService>();
      services.AddTransient<TreeUIManager>();
      services.AddSingleton<FavoritesManager>();
    
      // Downloads Services
      services.AddSingleton<AssetDownloader>();
      services.AddSingleton<ExtractionService>();
      services.AddSingleton<Status>();
      services.AddSingleton<Requests>();
       
      // Monitor Services
      services.AddSingleton<MonitorService>();
      services.AddSingleton<PbeStatusService>();
      services.AddSingleton<RiotApiService>();
      services.AddSingleton<VersionService>();
      services.AddSingleton<JsonDataService>();
      services.AddSingleton<ComparisonHistoryService>();

      // Hashes Services
      services.AddSingleton<HashResolverService>();

      // Comparator Services
      services.AddSingleton<WadComparatorService>();
      services.AddSingleton<WadDifferenceService>();
      services.AddSingleton<WadPackagingService>();
      services.AddSingleton<ReportGenerationService>();

      // Models Services
      services.AddSingleton<SknModelLoadingService>();
      services.AddSingleton<ScoModelLoadingService>();
      services.AddSingleton<MapGeometryLoadingService>();

      // Audio Services
      services.AddSingleton<AudioBankService>();
      services.AddSingleton<AudioBankLinkerService>();

      // Windows, Views, and Dialogs
      services.AddTransient<MainWindow>();
      services.AddTransient<HomeWindow>(); // New Home View
      services.AddTransient<ExplorerWindow>();
      services.AddTransient<ComparatorWindow>();
      services.AddTransient<ModelWindow>();
      services.AddTransient<MonitorWindow>();
      services.AddTransient<HelpWindow>();
      services.AddTransient<JsonDiffWindow>();
      services.AddTransient<NotificationHubWindow>();
      services.AddTransient<SettingsWindow>();
      services.AddTransient<ProgressDetailsWindow>();
      services.AddTransient<UpdateProgressWindow>();
      services.AddTransient<UpdateModeDialog>();
      services.AddTransient<InputDialog>();
      services.AddTransient<ConfirmationDialog>();

      // Secondary Views
      services.AddTransient<LogView>();
      services.AddTransient<GeneralSettingsView>();
      services.AddTransient<AdvancedSettingsView>();
      services.AddTransient<DefaultPathsSettingsView>();
      services.AddTransient<AboutView>();
      services.AddTransient<DocumentationView>();
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

      // Pin the application identity for Windows to avoid duplicates in notification settings and registry.
      SingleInstance.SetCurrentProcessExplicitAppUserModelID(SingleInstance.AppId);

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

        customMessageBoxService.ShowError("Error", "A critical error occurred in the UI. Please check the logs for details.", Application.Current.MainWindow);
        args.Handled = true;
      };

      AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
      {
        var ex = args.ExceptionObject as Exception;
        logService.LogError(ex, "An unhandled non-UI exception occurred. See application_errors.log for details.");

        customMessageBoxService.ShowError("Error", "A critical error occurred in a background process. Please check the logs for details.", Application.Current.MainWindow);
      };
    }
  }
}
