using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AssetsManager.Services.Formatting
{
    /// <summary>Transcodes local video through Windows Media Foundation without a bundled codec runtime.</summary>
    internal static class WindowsVideoTranscoder
    {
        private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid AudioSampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid ContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
        private static readonly Guid TopologyMode = new("3e3df610-394a-40b2-9dea-3bab650bebf2");
        private static readonly Guid AdjustProfile = new("9c37c21b-060f-487c-a690-80d7f50d1c72");
        private static readonly Guid TopologyStatus = new("30c5018d-9a53-454b-ad9e-6d5f8fa7c43b");

        public static async Task ConvertFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            string stagingPath = Path.Combine(Path.GetDirectoryName(destinationPath), $".video-export-{Guid.NewGuid():N}.mp4");
            try
            {
                await TranscodeAsync(sourcePath, stagingPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(stagingPath) || new FileInfo(stagingPath).Length == 0)
                    throw new InvalidDataException("Windows did not produce an MP4 file.");
                File.Move(stagingPath, destinationPath);
            }
            finally
            {
                File.Delete(stagingPath);
            }
        }

        private static Task TranscodeAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
            => Task.Run(() => Transcode(sourcePath, destinationPath, cancellationToken), cancellationToken);

        private static void Transcode(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(CoInitializeEx(0, 0));
            bool started = false;
            try
            {
                Check(MFStartup(0x20070, 0));
                started = true;
                using var profile = CreateProfile(sourcePath);
                Check(MFCreateSourceResolver(out nint resolverPointer));
                using var resolver = new ComObject(resolverPointer);
                using var resolvedSource = resolver.ResolveSource(sourcePath);
                using var source = resolvedSource.QueryInterface(new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"));
                try
                {
                    Check(MFCreateTranscodeTopology(source.Pointer, destinationPath, profile.Pointer, out nint topologyPointer));
                    using var topology = new ComObject(topologyPointer);
                    Check(MFCreateMediaSession(0, out nint sessionPointer));
                    using var session = new ComObject(sessionPointer);
                    try
                    {
                        session.SetTopology(topology);
                        RunSession(session, cancellationToken);
                    }
                    finally
                    {
                        session.ShutdownSession();
                    }
                }
                finally
                {
                    source.ShutdownSource();
                }
            }
            finally
            {
                if (started) MFShutdown();
                CoUninitialize();
            }
        }

        private static ComObject CreateProfile(string sourcePath)
        {
            Check(MFCreateSourceReaderFromURL(sourcePath, 0, out nint readerPointer));
            using var reader = new ComObject(readerPointer);
            using var sourceVideo = reader.GetNativeMediaType(0xfffffffc); // MF_SOURCE_READER_FIRST_VIDEO_STREAM
            if (sourceVideo == null) throw new InvalidDataException("The WebM file does not contain a supported video stream.");

            using var video = CreateAttributes();
            video.SetGuid(Subtype, new Guid("34363248-0000-0010-8000-00aa00389b71")); // H.264
            ulong size = sourceVideo.GetUInt64(FrameSize);
            uint width = (uint)(size >> 32), height = (uint)size;
            if (width == 0 || height == 0) throw new InvalidDataException("The video dimensions are invalid.");
            // The Windows H.264 encoder requires even dimensions.
            width = checked(width + (width & 1));
            height = checked(height + (height & 1));
            video.SetUInt64(FrameSize, PackRatio(width, height));
            ulong rate = NormalizeFrameRate(sourceVideo.GetUInt64(FrameRate, PackRatio(30, 1)));
            video.SetUInt64(FrameRate, rate);
            video.SetUInt64(PixelAspectRatio, sourceVideo.GetUInt64(PixelAspectRatio, PackRatio(1, 1)));
            video.SetUInt32(InterlaceMode, 2); // MFVideoInterlace_Progressive
            double framesPerSecond = (double)(uint)(rate >> 32) / (uint)rate;
            video.SetUInt32(AverageBitrate, (uint)Math.Clamp(width * (double)height * framesPerSecond * 0.18, 1_000_000, 30_000_000));

            using var audio = CreateAttributes();
            using var sourceAudio = reader.GetNativeMediaType(0xfffffffd); // MF_SOURCE_READER_FIRST_AUDIO_STREAM
            if (sourceAudio != null)
            {
                audio.SetGuid(Subtype, new Guid("00001610-0000-0010-8000-00aa00389b71")); // AAC
                audio.SetUInt32(AudioChannels, Math.Clamp(sourceAudio.GetUInt32(AudioChannels, 2), 1u, 2u));
                audio.SetUInt32(AudioSampleRate, 48000);
                audio.SetUInt32(AudioBytesPerSecond, 24000);
            }

            using var container = CreateAttributes();
            container.SetGuid(ContainerType, new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a")); // MPEG-4
            container.SetUInt32(TopologyMode, 0); // Software codecs also work without a compatible GPU.
            container.SetUInt32(AdjustProfile, 1);
            Check(MFCreateTranscodeProfile(out nint profilePointer));
            var profile = new ComObject(profilePointer);
            try
            {
                if (sourceAudio != null) profile.SetAudioAttributes(audio);
                profile.SetVideoAttributes(video);
                profile.SetContainerAttributes(container);
                return profile;
            }
            catch
            {
                profile.Dispose();
                throw;
            }
        }

        private static void RunSession(ComObject session, CancellationToken cancellationToken)
        {
            bool running = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mediaEvent = session.GetEvent();
                if (mediaEvent == null)
                {
                    cancellationToken.WaitHandle.WaitOne(20);
                    continue;
                }

                Check(mediaEvent.GetEventStatus());
                switch (mediaEvent.GetEventType())
                {
                    case 111 when !running && mediaEvent.GetUInt32(TopologyStatus, 0) == 100: // MESessionTopologyStatus, MF_TOPOSTATUS_READY
                        session.Start();
                        running = true;
                        break;
                    case 107: // MESessionEnded
                        session.Close();
                        break;
                    case 106: // MESessionClosed: the MP4 sink has finalized its metadata.
                        return;
                }
            }
        }

        internal static ulong NormalizeFrameRate(ulong packedRate)
        {
            uint numerator = (uint)(packedRate >> 32), denominator = (uint)packedRate;
            if (numerator == 0 || denominator == 0) return PackRatio(30, 1);
            double rate = (double)numerator / denominator;
            // WebM timestamps can report 30 fps as 3000003000/100000000, which Windows H.264 rejects.
            foreach (uint commonRate in new uint[] { 24, 25, 30, 48, 50, 60, 120, 144, 240 })
            {
                if (Math.Abs(rate - commonRate) < 0.001) return PackRatio(commonRate, 1);
                if (Math.Abs(rate - commonRate * 1000.0 / 1001) < 0.001) return PackRatio(commonRate * 1000, 1001);
            }
            uint divisor = GreatestCommonDivisor(numerator, denominator);
            return PackRatio(numerator / divisor, denominator / divisor);
        }

        private static uint GreatestCommonDivisor(uint a, uint b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }

        private static ulong PackRatio(uint numerator, uint denominator) => ((ulong)numerator << 32) | denominator;
        private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
        private static ComObject CreateAttributes()
        {
            Check(MFCreateAttributes(out nint pointer, 8));
            return new ComObject(pointer);
        }

        // The slots below follow IMFAttributes, IMFSourceReader, IMFSourceResolver,
        // IMFTranscodeProfile, IMFMediaSession and IMFMediaEvent in the Windows SDK.
        // Calling the native ABI directly avoids shipping the Windows SDK's managed projections.
        private sealed unsafe class ComObject(nint pointer) : IDisposable
        {
            public nint Pointer { get; private set; } = pointer;
            private nint Slot(int index) => (*(nint**)Pointer)[index];
            public void Dispose()
            {
                if (Pointer != 0) Marshal.Release(Pointer);
                Pointer = 0;
            }
            public ComObject QueryInterface(Guid iid)
            {
                nint result;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(0))(Pointer, &iid, &result));
                return new ComObject(result);
            }
            public void SetUInt32(Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(21))(Pointer, &key, value));
            public void SetUInt64(Guid key, ulong value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(22))(Pointer, &key, value));
            public void SetGuid(Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(24))(Pointer, &key, &value));
            public uint GetUInt32(Guid key, uint fallback)
            {
                uint value;
                return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(7))(Pointer, &key, &value) >= 0 ? value : fallback;
            }
            public ulong GetUInt64(Guid key, ulong? fallback = null)
            {
                ulong value;
                int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(8))(Pointer, &key, &value);
                if (result < 0 && fallback.HasValue) return fallback.Value;
                Check(result);
                return value;
            }
            public ComObject ResolveSource(string path)
            {
                nint source;
                int type;
                fixed (char* url = path)
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, int*, nint*, int>)Slot(3))(Pointer, url, 1, 0, &type, &source));
                return new ComObject(source);
            }
            public ComObject GetNativeMediaType(uint stream)
            {
                nint mediaType;
                int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(5))(Pointer, stream, 0, &mediaType);
                if (result == unchecked((int)0xc00d36b3)) return null; // MF_E_INVALIDSTREAMNUMBER
                Check(result);
                return new ComObject(mediaType);
            }
            public void SetAudioAttributes(ComObject value) => SetAttributes(3, value);
            public void SetVideoAttributes(ComObject value) => SetAttributes(5, value);
            public void SetContainerAttributes(ComObject value) => SetAttributes(7, value);
            private void SetAttributes(int slot, ComObject value) => Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(slot))(Pointer, value.Pointer));
            public void SetTopology(ComObject topology) => Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(7))(Pointer, 0, topology.Pointer));
            public void Start()
            {
                // VT_EMPTY selects the start of the presentation; PROPVARIANT is 24 bytes on x64.
                byte* position = stackalloc byte[24];
                new Span<byte>(position, 24).Clear();
                Check(((delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)Slot(9))(Pointer, 0, position));
            }
            public void Close() => Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(12))(Pointer));
            public void ShutdownSession() => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(13))(Pointer);
            public void ShutdownSource() => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(12))(Pointer);
            public ComObject GetEvent()
            {
                nint mediaEvent;
                int result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(3))(Pointer, 1, &mediaEvent); // MF_EVENT_FLAG_NO_WAIT
                if (result == unchecked((int)0xc00d3e80)) return null; // MF_E_NO_EVENTS_AVAILABLE
                Check(result);
                return new ComObject(mediaEvent);
            }
            public int GetEventType() => GetEventValue(33);
            public int GetEventStatus() => GetEventValue(35);
            private int GetEventValue(int slot)
            {
                int value;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(slot))(Pointer, &value));
                return value;
            }
        }

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint initialSize);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
        [DllImport("mf.dll")] private static extern int MFCreateSourceResolver(out nint resolver);
        [DllImport("mf.dll")] private static extern int MFCreateTranscodeProfile(out nint profile);
        [DllImport("mf.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateTranscodeTopology(nint source, string outputPath, nint profile, out nint topology);
        [DllImport("mf.dll")] private static extern int MFCreateMediaSession(nint attributes, out nint session);
    }
}
