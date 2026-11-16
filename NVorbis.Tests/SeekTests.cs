using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace NVorbis.Tests
{
    public class SeekTests
    {
        // 2test.ogg: mono, first audio page ends at granule 145984; 3test.ogg: stereo, first audio page ends at 8192.
        // Both have a last page whose granule position is clipped to the stream's length.
        // onepage_stereo.ogg: 0.5 s of two sines (ffmpeg sine + libvorbis -q:a 2) in a single page, which is both the first
        // and the last audio page, so its granule position is clipped to the stream's length.
        public static TheoryData<string> Files => new TheoryData<string> { "2test.ogg", "3test.ogg", "onepage_stereo.ogg" };

        static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestFiles", name));

        static VorbisReader Open(byte[] bytes) => new VorbisReader(new MemoryStream(bytes, false), true);

        static float[] DecodeAll(byte[] bytes, out int channels)
        {
            using var r = Open(bytes);
            channels = r.Channels;
            var all = new float[r.TotalSamples * r.Channels];
            var n = 0;
            int k;
            while (n < all.Length && (k = r.ReadSamples(all, n, Math.Min(4096 * r.Channels, all.Length - n))) > 0) n += k;
            Assert.Equal(all.Length, n);
            return all;
        }

        // the granule position of the first Ogg page that has one above 0
        static long FirstAudioPageGranule(byte[] b)
        {
            var i = 0;
            while (i + 27 <= b.Length)
            {
                var gp = BitConverter.ToInt64(b, i + 6);
                var seg = b[i + 26];
                var len = 27 + seg;
                for (var s = 0; s < seg; s++) len += b[i + 27 + s];
                if (gp > 0) return gp;
                i += len;
            }
            throw new InvalidDataException("no audio page");
        }

        // Seeks to target and reads up to 256 frames; fails (instead of hanging the test run) if SeekTo / ReadSamples never return.
        static void AssertSeekMatches(VorbisReader r, float[] reference, int channels, long target)
        {
            var buf = new float[256 * channels];
            var task = Task.Run(() =>
            {
                r.SamplePosition = target;
                return r.ReadSamples(buf, 0, buf.Length);
            });
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), $"seek to {target} did not return");

            var expected = (int)Math.Min(256, reference.Length / channels - target);
            Assert.Equal(expected * channels, task.Result);
            for (var i = 0; i < task.Result; i++)
            {
                Assert.True(reference[target * channels + i] == buf[i], $"seek to {target}: sample {i} differs from a full decode");
            }
            Assert.Equal(target + expected, r.SamplePosition);
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void SeekInsideFirstAudioPage_ReturnsTheRequestedFrames(string file)
        {
            // 0.10.5: frames 1..128 never returned (Read looped forever), the rest of the first page came back 128 frames early
            var bytes = Load(file);
            var reference = DecodeAll(bytes, out var channels);
            var firstPageEnd = FirstAudioPageGranule(bytes);

            using var r = Open(bytes);
            _ = r.TotalSamples;
            var total = reference.Length / channels;
            foreach (var target in new long[] { 1, 2, 64, 127, 128, 129, 200, 256, 257, 1000, firstPageEnd - 129, firstPageEnd - 1, firstPageEnd, firstPageEnd + 1, 0 })
            {
                // (a single-page stream ends with its first page)
                if (target < total)
                {
                    AssertSeekMatches(r, reference, channels, target);
                }
            }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void SeekRightAfterOpening_DoesNotThrow(string file)
        {
            // 0.10.5: seeking before the first audio page had been read threw ArgumentOutOfRangeException (FindFirstDataPage)
            var bytes = Load(file);
            var reference = DecodeAll(bytes, out var channels);

            foreach (var target in new long[] { 0, 1, 1000 })
            {
                using var r = Open(bytes);
                AssertSeekMatches(r, reference, channels, target);
            }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void SeekIntoLastPage_ReturnsTheRequestedFrames(string file)
        {
            // 0.10.5: "GranulePos mismatch" because the last page's granule position is clipped to the stream's length
            var bytes = Load(file);
            var reference = DecodeAll(bytes, out var channels);
            var total = reference.Length / channels;

            using var r = Open(bytes);
            foreach (var target in new long[] { total - 100, total - 1, total / 2 })
            {
                AssertSeekMatches(r, reference, channels, target);
            }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void SeekAnywhere_MatchesFullDecode(string file)
        {
            var bytes = Load(file);
            var reference = DecodeAll(bytes, out var channels);
            var total = reference.Length / channels;

            using var r = Open(bytes);
            var rnd = new Random(1234);
            for (var i = 0; i < 40; i++)
            {
                AssertSeekMatches(r, reference, channels, rnd.Next(0, total));
            }
        }
    }
}
