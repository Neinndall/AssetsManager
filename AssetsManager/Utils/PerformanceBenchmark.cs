using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using LeagueToolkit.Core.Wad;
using ZstdSharp;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static void RunBinaryHashBenchmark(LogService logService)
        {
            logService.Log("--- STARTING BINARY HASH CACHE BENCHMARK ---");

            string txtFile = Path.Combine(Path.GetTempPath(), "hashes.benchmark.txt");
            string binFile = Path.ChangeExtension(txtFile, ".bin");
            int entryCount = 100000;

            // 1. Crear archivo de texto falso
            using (var swFile = new StreamWriter(txtFile))
            {
                for (int i = 0; i < entryCount; i++)
                {
                    swFile.WriteLine($"{i:X16} data/assets/characters/champion_number_{i}/skins/skin_{i}/textures/texture_file_path_long_name.dds");
                }
            }

            try
            {
                // TEST 1: Generación y Carga Inicial
                var cache = new BinaryHashCache(txtFile, logService);
                Stopwatch sw = Stopwatch.StartNew();
                cache.Load();
                sw.Stop();
                logService.Log($"[CACHÉ BINARIA] Generación y Carga inicial: {sw.ElapsedMilliseconds} ms");

                // TEST 2: Carga desde Binario (Simulando arranque rápido)
                cache.Dispose();
                cache = new BinaryHashCache(txtFile, logService);
                sw.Restart();
                
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long memStart = GC.GetTotalAllocatedBytes(true);
                
                cache.Load();
                sw.Stop();
                long allocated = GC.GetTotalAllocatedBytes(true) - memStart;
                
                logService.Log($"[CACHÉ BINARIA] Carga desde .bin (Arranque): {sw.ElapsedMilliseconds} ms");
                logService.Log($"[CACHÉ BINARIA] RAM consumida (Índices): {allocated / 1024.0 / 1024.0:F2} MB");

                // TEST 3: Resolución (Velocidad de búsqueda)
                sw.Restart();
                for (int i = 0; i < 1000; i++)
                {
                    var res = cache.Resolve((ulong)i);
                    if (res == null) throw new Exception("Resolution failed");
                }
                sw.Stop();
                logService.Log($"[CACHÉ BINARIA] 1000 Resoluciones: {sw.Elapsed.TotalMilliseconds:F4} ms");
            }
            finally
            {
                if (File.Exists(txtFile)) File.Delete(txtFile);
                if (File.Exists(binFile)) File.Delete(binFile);
            }

            logService.Log("--- BINARY HASH BENCHMARK COMPLETED ---");
        }

        public static void RunDecompressionBenchmark(LogService logService)
        {
            logService.Log("--- STARTING DECOMPRESSION COMPARISON BENCHMARK ---");

            int iterations = 2000; // Aumentamos para mayor precisión
            byte[] rawData = new byte[256 * 1024]; 
            new Random(42).NextBytes(rawData);
            
            byte[] compressedData;
            using (var compressor = new Compressor())
            {
                compressedData = compressor.Wrap(rawData).ToArray();
            }

            logService.Log($"Config: {iterations} iteraciones | Chunk: {rawData.Length / 1024}KB");

            // --- TEST 1: BASELINE ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var proc = Process.GetCurrentProcess();
            var cpuStart = proc.TotalProcessorTime;
            Stopwatch sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                using (var compressedStream = new MemoryStream(compressedData))
                using (var decompressedStream = new MemoryStream())
                using (var zstdStream = new ZstdSharp.DecompressionStream(compressedStream))
                {
                    zstdStream.CopyTo(decompressedStream);
                    byte[] result = decompressedStream.ToArray();
                }
            }

            sw.Stop();
            var cpuTimeBaseline = proc.TotalProcessorTime - cpuStart;
            logService.Log($"[BASELINE] Reloj: {sw.ElapsedMilliseconds}ms | Tiempo CPU (Core-Time): {cpuTimeBaseline.TotalMilliseconds:F0}ms");

            // --- TEST 2: OPTIMIZADO ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            cpuStart = proc.TotalProcessorTime;
            sw.Restart();

            for (int i = 0; i < iterations; i++)
            {
                byte[] result = WadChunkUtils.DecompressChunk(compressedData, WadChunkCompression.Zstd);
            }

            sw.Stop();
            var cpuTimeOpt = proc.TotalProcessorTime - cpuStart;
            logService.Log($"[OPTIMIZADO] Reloj: {sw.ElapsedMilliseconds}ms | Tiempo CPU (Core-Time): {cpuTimeOpt.TotalMilliseconds:F0}ms");

            // --- RESULTADOS CPU ---
            double cpuSaving = (1.0 - (cpuTimeOpt.TotalMilliseconds / cpuTimeBaseline.TotalMilliseconds)) * 100;
            logService.Log($">> REDUCCIÓN CARGA CPU: {cpuSaving:F1}% menos esfuerzo del procesador.");

            logService.Log("--- DECOMPRESSION BENCHMARK COMPLETED ---");
        }

        public static void RunRealWorldWadBenchmark(LogService logService)
        {
            logService.Log("--- STARTING REAL-WORLD GALLERY SIMULATION ---");

            int assetCount = 100; // 100 iconos/texturas
            int assetSize = 128 * 1024; // 128KB de media
            var random = new Random(42);

            // Preparar datos comprimidos variados
            var assets = new List<byte[]>();
            using (var compressor = new Compressor())
            {
                for (int i = 0; i < assetCount; i++)
                {
                    byte[] data = new byte[assetSize + random.Next(-50000, 50000)];
                    random.NextBytes(data);
                    assets.Add(compressor.Wrap(data).ToArray());
                }
            }

            logService.Log($"Escenario: Carga paralela de {assetCount} assets aleatorios.");

            // --- TEST 1: BASELINE (Simulando WadFile.LoadChunkDecompressed) ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memStart = GC.GetTotalAllocatedBytes(true);
            Stopwatch sw = Stopwatch.StartNew();

            // Simulamos lo que hace la librería original (muchas asignaciones)
            var tasksBaseline = assets.Select(data => Task.Run(() =>
            {
                // 1. Simular MemoryStream interno de la lib
                using var ms = new MemoryStream(data);
                // 2. Simular descompresión por stream
                using var ds = new ZstdSharp.DecompressionStream(ms);
                using var output = new MemoryStream();
                ds.CopyTo(output);
                // 3. Simular el ToArray final que espera la UI
                return output.ToArray();
            })).ToArray();

            Task.WaitAll(tasksBaseline);
            sw.Stop();
            long allocatedBaseline = GC.GetTotalAllocatedBytes(true) - memStart;
            logService.Log($"[BASELINE GALLERY] Tiempo: {sw.ElapsedMilliseconds}ms | RAM: {allocatedBaseline / 1024.0 / 1024.0:F2} MB");

            // --- TEST 2: OPTIMIZADO (Nuestro sistema actual) ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            memStart = GC.GetTotalAllocatedBytes(true);
            sw.Restart();

            var tasksOpt = assets.Select(data => Task.Run(() =>
            {
                // Usamos nuestro WadChunkUtils que usa ArrayPool (indirectamente vía Unwrap) y reuso de descompresor
                return WadChunkUtils.DecompressChunk(data, WadChunkCompression.Zstd);
            })).ToArray();

            Task.WaitAll(tasksOpt);
            sw.Stop();
            long allocatedOpt = GC.GetTotalAllocatedBytes(true) - memStart;
            logService.Log($"[OPTIMIZED GALLERY] Tiempo: {sw.ElapsedMilliseconds}ms | RAM: {allocatedOpt / 1024.0 / 1024.0:F2} MB");

            // --- RESULTADOS FINALES ---
            double ramReduction = (double)(allocatedBaseline - allocatedOpt) / allocatedBaseline * 100;
            logService.Log($">> REDUCCIÓN DE CARGA GC: {ramReduction:F1}% menos presión sobre el recolector de basura.");
            logService.Log($">> VELOCIDAD: {(double)sw.ElapsedMilliseconds / assetCount:F4} ms de media por asset.");

            logService.Log("--- REAL-WORLD SIMULATION COMPLETED ---");
        }

        public static void RunFullZeroCopyBenchmark(LogService logService)
        {
            logService.Log("--- STARTING FULL ZERO-COPY BENCHMARK (SAFE VERSION) ---");

            string tempFile = Path.Combine(Path.GetTempPath(), "benchmark_data.bin");
            int chunkCount = 200;
            int chunkSize = 256 * 1024; // 256KB por chunk
            byte[] dummyData = new byte[chunkSize];
            new Random(42).NextBytes(dummyData);

            using (var fs = File.Create(tempFile))
            {
                for (int i = 0; i < chunkCount; i++) fs.Write(dummyData);
            }

            try
            {
                // TEST 1: BASELINE (Lógica anterior)
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long memStart = GC.GetTotalAllocatedBytes(true);
                Stopwatch sw = Stopwatch.StartNew();

                for (int i = 0; i < chunkCount; i++)
                {
                    byte[] data = File.ReadAllBytes(tempFile);
                    var result = WadChunkUtils.DecompressChunk(data.Skip(i * chunkSize).Take(chunkSize).ToArray(), WadChunkCompression.None);
                }
                sw.Stop();
                long allocatedBaseline = GC.GetTotalAllocatedBytes(true) - memStart;
                logService.Log($"[BASELINE DISCO] Tiempo: {sw.ElapsedMilliseconds}ms, RAM: {allocatedBaseline / 1024.0 / 1024.0:F2} MB");

                // TEST 2: OPTIMIZADO (Safe MMF + ArrayPool)
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                memStart = GC.GetTotalAllocatedBytes(true);
                sw.Restart();

                using (var mmf = MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open))
                {
                    for (int i = 0; i < chunkCount; i++)
                    {
                        using var stream = mmf.CreateViewStream(i * chunkSize, chunkSize, MemoryMappedFileAccess.Read);
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                        try
                        {
                            int read = stream.Read(buffer, 0, chunkSize);
                            var result = WadChunkUtils.DecompressChunk(buffer.AsSpan(0, read), WadChunkCompression.None);
                        }
                        finally { ArrayPool<byte>.Shared.Return(buffer); }
                    }
                }
                sw.Stop();
                long allocatedOpt = GC.GetTotalAllocatedBytes(true) - memStart;
                logService.Log($"[SAFE ZERO-COPY] Tiempo: {sw.ElapsedMilliseconds}ms, RAM: {allocatedOpt / 1024.0 / 1024.0:F2} MB");
                
                double savedRam = (allocatedBaseline - allocatedOpt) / 1024.0 / 1024.0;
                logService.Log($">> Ahorro de RAM: {savedRam:F2} MB (Sin usar código unsafe).");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }

            logService.Log("--- FULL BENCHMARK COMPLETED ---");
        }
    }
}
