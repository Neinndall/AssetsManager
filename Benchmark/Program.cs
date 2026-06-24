using System;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BenchmarkApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var services = new ServiceCollection();

            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            services.AddSingleton<ILogger>(logger);
            services.AddSingleton<LogService>();

            var serviceProvider = services.BuildServiceProvider();
            var logService = serviceProvider.GetRequiredService<LogService>();

            Console.WriteLine("=== ASSETSMANAGER PERFORMANCE LAB ===");
            Console.WriteLine("Ready for benchmarks.");
            Console.WriteLine("=====================================");
        }
    }
}
