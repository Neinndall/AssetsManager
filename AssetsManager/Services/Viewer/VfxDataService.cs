using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;

using System.Threading.Tasks;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Viewer
{
    public class VfxDataService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolver;

        public VfxDataService(LogService logService, HashResolverService hashResolver = null)
        {
            _logService = logService;
            _hashResolver = hashResolver;
        }

        public Task<List<VfxSystemModel>> LoadVfxSystemsForModelAsync(string modelFilePath, string projectRootFolder = null)
        {
            return Task.Run(() => LoadVfxSystemsForModel(modelFilePath, projectRootFolder));
        }

        public List<VfxSystemModel> LoadVfxSystemsForModel(string modelFilePath, string projectRootFolder = null)
        {
            var systems = new List<VfxSystemModel>();
            if (string.IsNullOrWhiteSpace(modelFilePath)) return systems;

            var binFilesToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (modelFilePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && File.Exists(modelFilePath))
            {
                binFilesToScan.Add(Path.GetFullPath(modelFilePath));
            }

            // Build candidate directories from modelFilePath (both original and assets->data mapped)
            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string currentDir = Path.GetDirectoryName(modelFilePath);
            int levels = 0;
            while (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir) && levels < 6)
            {
                candidateDirs.Add(currentDir);
                if (currentDir.Contains("\\assets\\", StringComparison.OrdinalIgnoreCase))
                {
                    candidateDirs.Add(currentDir.Replace("\\assets\\", "\\data\\", StringComparison.OrdinalIgnoreCase));
                }
                if (currentDir.Contains("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    candidateDirs.Add(currentDir.Replace("/assets/", "/data/", StringComparison.OrdinalIgnoreCase));
                }

                string parent = Path.GetDirectoryName(currentDir);
                if (parent == currentDir || string.IsNullOrEmpty(parent)) break;
                currentDir = parent;
                levels++;
            }

            // Auto-detect WAD root folder from modelFilePath if not explicitly provided
            if (string.IsNullOrEmpty(projectRootFolder))
            {
                string dir = Path.GetDirectoryName(modelFilePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    if (Directory.Exists(Path.Combine(dir, "data")) || Directory.Exists(Path.Combine(dir, "assets")))
                    {
                        projectRootFolder = dir;
                        break;
                    }
                    string parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
            }

            if (!string.IsNullOrEmpty(projectRootFolder) && Directory.Exists(projectRootFolder))
            {
                candidateDirs.Add(projectRootFolder);
                string dataFolder = Path.Combine(projectRootFolder, "data");
                if (Directory.Exists(dataFolder)) candidateDirs.Add(dataFolder);
            }

            // Scan candidate directories for .bin files relevant to the active skin
            string targetSkinTag = ExtractSkinTag(modelFilePath);

            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var bins = Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories);
                    foreach (var bin in bins)
                    {
                        if (IsBinRelevantToSkin(bin, targetSkinTag))
                        {
                            binFilesToScan.Add(Path.GetFullPath(bin));
                        }
                    }
                }
                catch { }
            }

            _logService?.LogDebug($"[VFX] Targeted scan found {binFilesToScan.Count} BIN candidate(s) for skin '{targetSkinTag}' ('{Path.GetFileName(modelFilePath)}').");

            foreach (string binFile in binFilesToScan)
            {
                var parsed = LoadVfxSystemsFromBin(binFile);
                systems.AddRange(parsed);
            }

            return systems;
        }

        private static string ExtractSkinTag(string modelFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath)) return "skin0";

            var match = System.Text.RegularExpressions.Regex.Match(modelFilePath, @"skin(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value.ToLower();
            }

            if (modelFilePath.Contains("base", StringComparison.OrdinalIgnoreCase))
            {
                return "skin0";
            }

            return "skin0";
        }

        private static bool IsBinRelevantToSkin(string binPath, string targetSkinTag)
        {
            string fileName = Path.GetFileNameWithoutExtension(binPath).ToLower();

            // Extract all skin tags present in the filename (e.g. skin0, skin1, skin20)
            var matches = System.Text.RegularExpressions.Regex.Matches(fileName, @"skin(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                // If the BIN filename specifies skin tags, it must include targetSkinTag (e.g. skin0)
                return matches.Cast<System.Text.RegularExpressions.Match>()
                              .Any(m => string.Equals(m.Value, targetSkinTag, StringComparison.OrdinalIgnoreCase));
            }

            // Base champion bin (e.g. aurora.bin or root.bin)
            return true;
        }

        public List<VfxSystemModel> LoadVfxSystemsFromBin(string binFilePath)
        {
            var systems = new List<VfxSystemModel>();

            if (string.IsNullOrEmpty(binFilePath) || !File.Exists(binFilePath))
            {
                _logService.LogDebug($"[VFX] BIN file for VFX loading not found: {binFilePath}");
                return systems;
            }

            try
            {
                using var stream = File.OpenRead(binFilePath);
                var binTree = new BinTree(stream);

                _logService.LogDebug($"[VFX] Processing BIN file '{Path.GetFileName(binFilePath)}' with {binTree.Objects.Count} object(s)...");

                foreach (var kvp in binTree.Objects)
                {
                    var obj = kvp.Value;
                    string resolvedClassName = _hashResolver?.ResolveBinHashGeneral(obj.ClassHash);

                    // ClassHash 0x45CD899F or 0x79BD121D or resolved name represent VfxSystemDefinitionData in BIN files
                    bool isVfxSystem = resolvedClassName == "VfxSystemDefinitionData" ||
                                       obj.ClassHash == 0x45CD899F ||
                                       obj.ClassHash == 0x79BD121D || 
                                       obj.Properties.ContainsKey(0x868EB76A) || // Emitters container
                                       obj.Properties.ContainsKey(0xDF6B357F) || 
                                       obj.Properties.ContainsKey(0x9EB3DC85);

                    if (!isVfxSystem) continue;

                    var systemModel = ParseVfxSystemObject(obj, kvp.Key);
                    if (systemModel != null)
                    {
                        // Ensure at least 1 emitter is active for visualization
                        if (systemModel.Emitters.Count == 0)
                        {
                            systemModel.Emitters.Add(new VfxEmitterModel
                            {
                                Name = "MainEmitter",
                                Lifetime = 1.5f,
                                EmissionRate = 12.0f,
                                StartColor = new Vector4(1.0f, 0.8f, 0.3f, 1.0f),
                                EndColor = new Vector4(0.9f, 0.4f, 0.1f, 0.0f),
                                BlendMode = 1 // Additive
                            });
                        }
                        systems.Add(systemModel);
                        _logService.LogDebug($"[VFX] Extracted VFX System '{systemModel.Name}' with {systemModel.Emitters.Count} emitter(s).");
                    }
                }

                _logService.LogSuccess($"[VFX] Total VFX Systems extracted from '{Path.GetFileName(binFilePath)}': {systems.Count}");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"[VFX] Failed to parse VFX systems from BIN: {binFilePath}");
            }

            return systems;
        }

        private VfxSystemModel ParseVfxSystemObject(BinTreeObject obj, uint pathHash)
        {
            var system = new VfxSystemModel
            {
                Name = $"Vfx_{pathHash:X8}"
            };

            foreach (var prop in obj.Properties)
            {
                if (prop.Value is BinTreeString strProp)
                {
                    if (prop.Key == 0xECF1C6BC || prop.Key == 0xE7638138 || prop.Key == 0x7D3C5230 || string.IsNullOrEmpty(system.ParticlePath))
                    {
                        system.ParticlePath = strProp.Value;
                        if (!string.IsNullOrWhiteSpace(strProp.Value))
                        {
                            system.Name = Path.GetFileNameWithoutExtension(strProp.Value);
                        }
                    }
                }
                else if (prop.Value is BinTreeContainer container)
                {
                    foreach (var elem in container.Elements)
                    {
                        if (elem is BinTreeStruct emitterStruct)
                        {
                            var emitter = ParseEmitterStruct(emitterStruct);
                            if (emitter != null)
                            {
                                system.Emitters.Add(emitter);
                            }
                        }
                    }
                }
            }

            return system;
        }

        private VfxEmitterModel ParseEmitterStruct(BinTreeStruct emitterStruct)
        {
            var emitter = new VfxEmitterModel
            {
                Name = "Emitter"
            };

            foreach (var prop in emitterStruct.Properties)
            {
                switch (prop.Value)
                {
                    case BinTreeString strVal:
                        AssignEmitterStringProperty(emitter, prop.Key, strVal.Value);
                        break;

                    case BinTreeStruct nestedStruct:
                        AssignEmitterNestedStructProperty(emitter, prop.Key, nestedStruct);
                        break;

                    case BinTreeOptional optVal:
                        if (optVal.Value is BinTreeF32 f32Opt)
                        {
                            emitter.Duration = f32Opt.Value;
                        }
                        break;

                    case BinTreeF32 f32Val:
                        AssignEmitterFloatProperty(emitter, prop.Key, f32Val.Value);
                        break;

                    case BinTreeU16 u16Val:
                        if (prop.Key == 0x2A2E2F82 || prop.Key == 0x94677E59) // numFrames
                        {
                            emitter.NumFrames = u16Val.Value;
                        }
                        break;

                    case BinTreeU8 u8Val:
                        if (prop.Key == 0x748B4783 || prop.Key == 0x16F4FBA9) // blendMode
                        {
                            emitter.BlendMode = u8Val.Value;
                        }
                        break;
                }
            }

            return emitter;
        }

        private void AssignEmitterStringProperty(VfxEmitterModel emitter, uint key, string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;

            // Common property hashes & key substrings
            string lower = val.ToLowerInvariant();
            if (lower.EndsWith(".dds") || lower.EndsWith(".png") || lower.EndsWith(".tex") || lower.Contains("textures/"))
            {
                emitter.TexturePath = val;
            }
            else if (lower.EndsWith(".scb") || lower.EndsWith(".sco") || lower.EndsWith(".skn") || lower.Contains("meshes/"))
            {
                emitter.MeshPath = val;
            }
            else if (val.StartsWith("BUFFBONE", StringComparison.OrdinalIgnoreCase) ||
                     val.StartsWith("C_", StringComparison.OrdinalIgnoreCase) ||
                     val.StartsWith("L_", StringComparison.OrdinalIgnoreCase) ||
                     val.StartsWith("R_", StringComparison.OrdinalIgnoreCase) ||
                     val.Equals("root", StringComparison.OrdinalIgnoreCase))
            {
                emitter.AttachToBone = val;
            }
        }

        private void AssignEmitterFloatProperty(VfxEmitterModel emitter, uint key, float val)
        {
            if (val <= 0) return;
            // Delay or Duration or Rate heuristics
            if (emitter.Lifetime <= 1.0f && val > 0.05f && val < 30.0f)
            {
                emitter.Lifetime = val;
            }
        }

        private void AssignEmitterNestedStructProperty(VfxEmitterModel emitter, uint key, BinTreeStruct nestedStruct)
        {
            // ValueFloat or ValueVector3 or FlexType
            foreach (var prop in nestedStruct.Properties)
            {
                if (prop.Value is BinTreeF32 f32)
                {
                    if (f32.Value > 0 && emitter.Lifetime == 1.0f)
                    {
                        emitter.Lifetime = f32.Value;
                    }
                }
                else if (prop.Value is BinTreeStruct vecStruct)
                {
                    // Vector3/Vector4 extraction
                    var vec = ExtractVector3(vecStruct);
                    if (vec != Vector3.Zero && emitter.InitialVelocity == Vector3.Zero)
                    {
                        emitter.InitialVelocity = vec;
                    }
                }
            }
        }

        private Vector3 ExtractVector3(BinTreeStruct structProp)
        {
            float x = 0, y = 0, z = 0;
            foreach (var p in structProp.Properties)
            {
                if (p.Value is BinTreeF32 fVal)
                {
                    if (x == 0) x = fVal.Value;
                    else if (y == 0) y = fVal.Value;
                    else if (z == 0) z = fVal.Value;
                }
            }
            return new Vector3(x, y, z);
        }
    }
}
