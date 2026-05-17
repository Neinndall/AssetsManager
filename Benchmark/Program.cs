using System;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BenchmarkApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var services = new ServiceCollection();
            
            // Setup minimal logging to console
            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();
                
            services.AddSingleton<ILogger>(logger);
            services.AddSingleton<LogService>();
            services.AddSingleton<DirectoriesCreator>();
            services.AddSingleton<HashResolverService>();
            
            var serviceProvider = services.BuildServiceProvider();
            var logService = serviceProvider.GetRequiredService<LogService>();
            var hashService = serviceProvider.GetRequiredService<HashResolverService>();

            Console.WriteLine("=== ASSETSMANAGER PERFORMANCE LAB ===");
            
            // Lab is ready for new technical tests.

            Console.WriteLine("=====================================");
        }
    }
}