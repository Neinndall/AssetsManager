using AssetsManager.Services.Audio;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Audio
{
    public sealed class AudioBankLinkerWpkDiffTests
    {
        [Fact]
        public void AssignClickedBankFallbackRoutesWpkBytesToWpkNotAudioBnk()
        {
            byte[] oldWpkBytes = [0x72, 0x33, 0x64, 0x32];
            byte[] newWpkBytes = [0x72, 0x33, 0x64, 0x32, 0x01];
            byte[] oldEvents = null, newEvents = null;
            byte[] oldAudio = null, newAudio = null;
            byte[] oldWpk = null, newWpk = null;

            AudioBankLinkerService.AssignClickedBankFallback(
                "ahri_base_vo_audio.wpk",
                oldWpkBytes,
                newWpkBytes,
                ref oldEvents,
                ref newEvents,
                ref oldAudio,
                ref newAudio,
                ref oldWpk,
                ref newWpk);

            Assert.Same(oldWpkBytes, oldWpk);
            Assert.Same(newWpkBytes, newWpk);
            Assert.Null(oldAudio);
            Assert.Null(newAudio);
            Assert.Null(oldEvents);
            Assert.Null(newEvents);
        }

        [Fact]
        public void AssignClickedBankFallbackRoutesEventsAndAudioBnkCorrectly()
        {
            byte[] oldBytes = [1, 2];
            byte[] newBytes = [3, 4];

            byte[] oldEvents = null, newEvents = null, oldAudio = null, newAudio = null, oldWpk = null, newWpk = null;
            AudioBankLinkerService.AssignClickedBankFallback(
                "ahri_base_sfx_events.bnk",
                oldBytes,
                newBytes,
                ref oldEvents,
                ref newEvents,
                ref oldAudio,
                ref newAudio,
                ref oldWpk,
                ref newWpk);
            Assert.Same(oldBytes, oldEvents);
            Assert.Same(newBytes, newEvents);
            Assert.Null(oldAudio);
            Assert.Null(oldWpk);

            oldEvents = newEvents = oldAudio = newAudio = oldWpk = newWpk = null;
            AudioBankLinkerService.AssignClickedBankFallback(
                "ahri_base_sfx_audio.bnk",
                oldBytes,
                newBytes,
                ref oldEvents,
                ref newEvents,
                ref oldAudio,
                ref newAudio,
                ref oldWpk,
                ref newWpk);
            Assert.Same(oldBytes, oldAudio);
            Assert.Same(newBytes, newAudio);
            Assert.Null(oldEvents);
            Assert.Null(oldWpk);
        }
    }
}
