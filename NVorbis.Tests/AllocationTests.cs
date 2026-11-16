using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class AllocationTests
    {
        static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestFiles", name));

        [Theory]
        [InlineData("2test.ogg")]
        [InlineData("3test.ogg")]
        [InlineData("issue6test.ogg")]
        public void WarmReadsAndSeeks_DoNotAllocate(string file)
        {
            using var r = new VorbisReader(new MemoryStream(Load(file), false), true);
            var buf = new float[1024 * r.Channels];
            var total = r.TotalSamples;

            void ReadAll()
            {
                while (r.ReadSamples(buf, 0, buf.Length) > 0) { }
            }

            // warm up: JIT tiering, page index, and every seek the measured part does
            for (var pass = 0; pass < 3; pass++)
            {
                r.SamplePosition = 0;
                ReadAll();
                r.SamplePosition = total / 2;
                r.ReadSamples(buf, 0, buf.Length);
            }

            r.SamplePosition = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            ReadAll();
            r.SamplePosition = 0;
            ReadAll();
            r.SamplePosition = total / 2;
            r.ReadSamples(buf, 0, buf.Length);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }
    }
}
