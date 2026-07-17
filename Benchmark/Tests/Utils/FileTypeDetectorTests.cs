using System;
using System.IO;
using System.Text;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.BenchmarkTests.Utils
{
    public sealed class FileTypeDetectorTests
    {
        [Fact]
        public void GuessExtensionRecognizesMultiTextureUiAtlas()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(2u);
                WriteString(writer, "UIAutoAtlas/ClientStates/Gameplay/UX/LoL/Cherry/PlayerAugments/atlas_0.tex");
                WriteString(writer, "UIAutoAtlas/ClientStates/Gameplay/UX/LoL/Cherry/PlayerAugments/atlas_1.tex");
                writer.Write(1u);
                WriteString(writer, "ASSETS/UX/Kiwi/AugmentIcon_Pending.png");
                for (int i = 0; i < 5; i++) writer.Write(0.5f);
            }

            byte[] data = stream.ToArray();
            Array.Resize(ref data, 256);

            Assert.Equal("atlas", FileTypeDetector.GuessExtension(data.AsSpan(0, 256)));

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            string extension = FileTypeDetector.GuessExtension(data.AsSpan(0, 256));
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("atlas", extension);
            Assert.Equal(0, allocated);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }
    }
}
