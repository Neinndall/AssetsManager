using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task RunSampleTestAsync(LogService logService)
        {
            logService.Log("--- STARTING EMPIRICAL TEST: Action vs Func<Task> ---");

            // 1. Experiment with ACTION (The "Fire and Forget" behavior)
            logService.Log("\n[TEST 1] Testing with Action (Non-blocking):");
            Action testAction = async () =>
            {
                logService.Log("   -> UI (Action): Starting 500ms visual pause...");
                await Task.Delay(500);
                logService.Log("   -> UI (Action): Pause finished.");
            };

            logService.Log("   -> Motor: Discharging Action...");
            testAction.Invoke(); 
            logService.Log("   -> Motor: CONTINUING IMMEDIATELY (I didn't wait for UI!)");
            
            await Task.Delay(700); // Wait for the logs to settle

            // 2. Experiment with FUNC<TASK> (The "Blocking/Sync" behavior)
            logService.Log("\n[TEST 2] Testing with Func<Task> (Awaitable/Blocking):");
            Func<Task> testFunc = async () =>
            {
                logService.Log("   -> UI (Func): Starting 500ms visual pause...");
                await Task.Delay(500);
                logService.Log("   -> UI (Func): Pause finished.");
            };

            logService.Log("   -> Motor: Awaiting Func<Task>...");
            await testFunc.Invoke();
            logService.Log("   -> Motor: CONTINUING ONLY NOW (I waited for UI to finish).");

            logService.Log("\n--- TEST CONCLUSION ---");
            logService.Log("If we change OnVerifyingCompletedAsync to Action, we get [TEST 1] behavior:");
            logService.Log("The download would start before the UI finish showing the 100% Verifying state.");
        }
    }
}
