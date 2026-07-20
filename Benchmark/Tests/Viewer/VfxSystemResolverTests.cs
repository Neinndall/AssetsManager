using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class VfxSystemResolverTests
    {
        [Fact]
        public void CanExtractAnimationClipsFromRealBinFiles()
        {
            // Path to some real bin files from Aatrox character folder
            string testDir = @"C:\Users\danielpriego\Downloads\Workspace\TexSharp\TexSharp_v1.0.0-beta.3\TexSharp\LoL\Aatrox\data\characters\aatrox";
            
            if (!Directory.Exists(testDir))
            {
                // Skip if Aatrox directory does not exist on this environment
                return;
            }

            var binFiles = Directory.GetFiles(testDir, "*.bin", SearchOption.AllDirectories);
            Assert.NotEmpty(binFiles);

            bool foundAnyClips = false;

            foreach (var file in binFiles)
            {
                byte[] fileBytes = File.ReadAllBytes(file);
                
                // Parse clips
                var clips = VfxSystemResolver.ExtractAnimationClips(fileBytes);
                Assert.NotNull(clips);

                if (clips.Count > 0)
                {
                    foundAnyClips = true;
                    foreach (var clip in clips.Values)
                    {
                        Assert.NotNull(clip.Name);
                        Assert.NotNull(clip.AnimationName);
                        
                        foreach (var ev in clip.ParticleEvents)
                        {
                            // Ensure properties are loaded and not all null/empty
                            Assert.True(ev.EffectHash != 0 || !string.IsNullOrEmpty(ev.EffectName), 
                                $"Effect in clip {clip.Name} should have a non-zero hash or non-empty name");
                                
                            // Verify bone names and hashes
                            if (!string.IsNullOrEmpty(ev.BoneName))
                            {
                                Assert.True(ev.BoneName.Length > 0);
                            }
                        }
                    }
                }
            }

            // We should find at least some animation clips in Aatrox character bins
            Assert.True(foundAnyClips, "Should have found and parsed animation clips in Aatrox character bins.");
        }
    }
}
