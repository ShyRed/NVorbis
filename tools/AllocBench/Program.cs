// Measurement tool for NVorbis: allocations per decoded frame, allocation types,
// decoded-output dumps (for bit-identity checks), decode speed and seek accuracy.
// Not part of the NVorbis library.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;

static class Program
{
    const int ChunkFrames = 1024;

    static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: alloc|types|dump|speed|seekcheck ...");
            return 2;
        }
        Console.WriteLine($"# NVorbis {typeof(VorbisReader).Assembly.GetName().Version} from {typeof(VorbisReader).Assembly.Location}");
        switch (args[0])
        {
            case "alloc": return Alloc(args.Skip(1).ToArray());
            case "types": return Types(int.Parse(args[1]), args.Skip(2).ToArray());
            case "dump": return Dump(args[1], args.Skip(2).ToArray());
            case "speed": return Speed(int.Parse(args[1]), args.Skip(2).ToArray());
            case "seekcheck": return SeekCheck(args.Skip(1).ToArray());
            default: Console.Error.WriteLine("unknown command"); return 2;
        }
    }

    static VorbisReader Open(byte[] bytes) => new VorbisReader(new MemoryStream(bytes, false), true);

    static long ReadToEnd(VorbisReader r, float[] buf)
    {
        long frames = 0;
        int n;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0) frames += n / r.Channels;
        return frames;
    }

    // ---------------------------------------------------------------- alloc
    // Bytes allocated per decoded frame on a warm stream, measured with
    // GC.GetAllocatedBytesForCurrentThread() around each ReadSamples call.
    static int Alloc(string[] files)
    {
        Console.WriteLine("file\tch\tframes\treadBytes\tbytes/frame\tbytes/8reads\tallocReads/reads\tseek0\tseekMid\tseekEnd");
        foreach (var f in files)
        {
            try { AllocOne(f); }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(f)}\tEXCEPTION {e.GetType().Name}: {e.Message}"); }
        }
        return 0;
    }

    static void AllocOne(string f)
    {
        {
            var bytes = File.ReadAllBytes(f);
            using var r = Open(bytes);
            var ch = r.Channels;
            var buf = new float[ChunkFrames * ch];
            var total = r.TotalSamples;

            // warm-up: two full passes (JIT tiering, MDCT setup for both block sizes, page index)
            for (var pass = 0; pass < 2; pass++)
            {
                r.SamplePosition = 0;
                ReadToEnd(r, buf);
            }
            r.SamplePosition = 0;

            GC.Collect(0);
            long readBytes = 0, frames = 0, reads = 0, allocReads = 0;
            while (true)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var n = r.ReadSamples(buf, 0, buf.Length);
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (n == 0) break;
                readBytes += delta;
                frames += n / ch;
                reads++;
                if (delta != 0) allocReads++;
            }

            string SeekCost(long pos)
            {
                // warm this exact seek once, then measure the second one
                try { r.SamplePosition = pos; } catch (Exception e) { return "ERR:" + e.GetType().Name; }
                r.ReadSamples(buf, 0, buf.Length);
                GC.Collect(0);
                var b = GC.GetAllocatedBytesForCurrentThread();
                r.SamplePosition = pos;
                var a = GC.GetAllocatedBytesForCurrentThread() - b;
                r.ReadSamples(buf, 0, buf.Length);
                return a.ToString();
            }
            var s0 = SeekCost(0);
            var sMid = SeekCost(total / 2);
            var sEnd = SeekCost(total - 2000);

            Console.WriteLine($"{Path.GetFileName(f)}\t{ch}\t{frames}\t{readBytes}\t{(double)readBytes / frames:F3}\t{(double)readBytes / reads * 8:F0}\t{allocReads}/{reads}\t{s0}\t{sMid}\t{sEnd}");
        }
    }

    // ---------------------------------------------------------------- types
    sealed class AllocListener : EventListener
    {
        public readonly Dictionary<string, (long bytes, int ticks)> ByType = new();
        public volatile bool Recording;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!Recording || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
            var name = e.PayloadNames.IndexOf("TypeName") is var ti && ti >= 0 ? e.Payload[ti] as string : "?";
            long amount = 0;
            var ai = e.PayloadNames.IndexOf("AllocationAmount64");
            if (ai >= 0) amount = Convert.ToInt64(e.Payload[ai]);
            else amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount")]);
            lock (ByType)
            {
                ByType.TryGetValue(name ?? "?", out var cur);
                ByType[name ?? "?"] = (cur.bytes + amount, cur.ticks + 1);
            }
        }
    }

    // Which types are allocated while reading (sampled by GCAllocationTick, ~100 KB per tick).
    static int Types(int loops, string[] files)
    {
        using var listener = new AllocListener();
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            using var r = Open(bytes);
            var buf = new float[ChunkFrames * r.Channels];
            _ = r.TotalSamples;
            ReadToEnd(r, buf);
            r.SamplePosition = 0;

            Thread.Sleep(500); // let events from opening the file drain before recording
            lock (listener.ByType) listener.ByType.Clear();
            GC.Collect(0);
            var before = GC.GetAllocatedBytesForCurrentThread();
            listener.Recording = true;
            long frames = 0;
            for (var i = 0; i < loops; i++)
            {
                frames += ReadToEnd(r, buf);
                r.SamplePosition = 0;
            }
            listener.Recording = false;
            var total = GC.GetAllocatedBytesForCurrentThread() - before;
            Thread.Sleep(500); // let the event dispatcher drain
            Console.WriteLine($"## {Path.GetFileName(f)}: {loops} loops, {frames} frames, {total} bytes ({(double)total / frames:F3} B/frame)");
            lock (listener.ByType)
            {
                var sum = listener.ByType.Values.Sum(v => v.bytes);
                foreach (var kv in listener.ByType.OrderByDescending(kv => kv.Value.bytes))
                    Console.WriteLine($"  {kv.Key,-40} ticks={kv.Value.ticks,6}  ~{kv.Value.bytes,12} B  {100.0 * kv.Value.bytes / Math.Max(1, sum),5:F1}%");
                if (listener.ByType.Count == 0) Console.WriteLine("  (no GCAllocationTick events)");
            }
        }
        return 0;
    }

    // ---------------------------------------------------------------- dump
    // Writes the decoded output of each file (full decode + a seek pattern) as raw
    // float32 and prints SHA-256 hashes, for bit-identity comparison between builds.
    static int Dump(string outDir, string[] files)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in files)
        {
            try { DumpOne(outDir, f); }
            catch (Exception e)
            {
                // record the failure itself, so both builds can be compared on it too
                var name = Path.GetFileNameWithoutExtension(f);
                File.WriteAllText(Path.Combine(outDir, name + ".error.txt"), e.GetType().FullName + ": " + e.Message);
                Console.WriteLine($"EXCEPTION  {Path.GetFileName(f)}: {e.GetType().Name}: {e.Message}");
            }
        }
        return 0;
    }

    static void DumpOne(string outDir, string f)
    {
        {
            var bytes = File.ReadAllBytes(f);
            var name = Path.GetFileNameWithoutExtension(f);

            // full decode with a few different read sizes (exercises the packet copy-out logic)
            foreach (var chunk in new[] { 1024, 4096, 333 })
            {
                using var r = Open(bytes);
                var buf = new float[chunk * r.Channels];
                using var fs = File.Create(Path.Combine(outDir, $"{name}.full{chunk}.f32"));
                int n;
                while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
                    fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n)));
            }

            // seek pattern: only positions past the first audio page (0.10.5 hangs / is off inside it)
            using (var r = Open(bytes))
            {
                var total = r.TotalSamples;
                var buf = new float[4096 * r.Channels];
                using var fs = File.Create(Path.Combine(outDir, $"{name}.seek.f32"));
                var positions = new List<long> { 0 };
                if (total > 48000 * 2)
                    positions.AddRange(new[] { total * 3 / 10, total / 2, total * 77 / 100, total - 5000, total / 3 + 1, 0 });
                foreach (var p in positions)
                {
                    r.SamplePosition = p;
                    var n = r.ReadSamples(buf, 0, buf.Length);
                    fs.Write(BitConverter.GetBytes(p));
                    fs.Write(BitConverter.GetBytes(r.SamplePosition));
                    fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n)));
                }
                // loop pattern like the game: read to the end, seek to 0, read again
                ReadToEnd(r, buf);
                r.SamplePosition = 0;
                var m = r.ReadSamples(buf, 0, buf.Length);
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, m)));
            }

            foreach (var file in Directory.GetFiles(outDir, name + ".*.f32").OrderBy(x => x))
            {
                using var s = File.OpenRead(file);
                Console.WriteLine($"{Convert.ToHexString(SHA256.HashData(s))[..16]}  {new FileInfo(file).Length,10}  {Path.GetFileName(file)}");
            }
        }
    }

    // ---------------------------------------------------------------- speed
    static int Speed(int iterations, string[] files)
    {
        Console.WriteLine("file\tframes\tmin ns/frame\tmedian ns/frame\tx realtime (median)");
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            var times = new List<double>();
            long frames = 0;
            for (var i = 0; i < iterations + 3; i++)
            {
                using var r = Open(bytes);
                var buf = new float[ChunkFrames * r.Channels];
                var sw = Stopwatch.StartNew();
                frames = ReadToEnd(r, buf);
                sw.Stop();
                if (i >= 3) times.Add(sw.Elapsed.TotalNanoseconds / frames); // first 3 are warm-up
            }
            times.Sort();
            var median = times[times.Count / 2];
            Console.WriteLine($"{Path.GetFileName(f)}\t{frames}\t{times[0]:F2}\t{median:F2}\t{1e9 / 48000 / median:F0}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- seekcheck
    // For each target frame, seek and check the first frames returned against a full decode.
    static int SeekCheck(string[] files)
    {
        var hung = false;
        var indexFirst = files.Length > 0 && files[0] == "--index-first";
        if (indexFirst) files = files.Skip(1).ToArray();
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            float[] reference;
            int ch;
            long total;
            using (var r = Open(bytes))
            {
                ch = r.Channels;
                total = r.TotalSamples;
                reference = new float[total * ch + 4096];
                var n = 0; int k;
                while ((k = r.ReadSamples(reference, n, Math.Min(4096, reference.Length - n))) > 0) n += k;
            }

            // first audio page end, found by scanning Ogg page headers
            var firstPageGp = FirstAudioPageGranule(bytes);
            Console.WriteLine($"## {Path.GetFileName(f)}: total={total}, first audio page ends at granule {firstPageGp}");

            var targets = new SortedSet<long> { 0, 1, 2, 64, 127, 128, 129, 200, 1000 };
            foreach (var d in new long[] { -1000, -129, -128, -2, -1, 0, 1, 2, 128, 1000 }) targets.Add(firstPageGp + d);
            targets.Add(total / 2); targets.Add(total - 100);
            targets.RemoveWhere(t => t < 0 || t >= total);

            foreach (var t in targets)
            {
                using var r = Open(bytes);
                var buf = new float[256 * ch];
                if (indexFirst) _ = r.TotalSamples; // index all pages first (avoids the fresh-open FindFirstDataPage bug)
                var task = Task.Run(() => { r.SamplePosition = t; return r.ReadSamples(buf, 0, buf.Length); });
                try
                {
                    if (!task.Wait(3000))
                    {
                        Console.WriteLine($"  seek {t,8}: HANG (no return within 3 s)");
                        hung = true;
                        continue;
                    }
                }
                catch (AggregateException e)
                {
                    Console.WriteLine($"  seek {t,8}: EXCEPTION {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    continue;
                }
                var n = task.Result / ch;
                // find the offset at which the returned frames match the reference
                // Offset 0 first, then the nearest offsets (silence or a periodic signal can match at many offsets, and only
                // the nearest is meaningful). Frames returned past the stream's end are reported, not compared.
                string verdict = "NO MATCH";
                for (var k = 0; k <= 4096; k++)
                {
                    var off = (k & 1) == 0 ? k / 2 : -(k + 1) / 2;
                    var start = t + off;
                    if (start < 0 || start >= total) continue;
                    var m = (int)Math.Min(n, total - start);
                    var ok = true;
                    for (var i = 0; i < m * ch && ok; i++)
                        ok = MathF.Abs(buf[i] - reference[start * ch + i]) <= 1e-6f;
                    if (ok)
                    {
                        verdict = off == 0 ? "exact" : $"OFF BY {off} frames";
                        if (n > m) verdict += $", PLUS {n - m} FRAMES PAST THE END";
                        break;
                    }
                }
                Console.WriteLine($"  seek {t,8}: {verdict} (reported pos after read {r.SamplePosition}, expected {t + n})");
            }
        }
        if (hung) Environment.Exit(3); // hung worker threads would keep the process alive
        return 0;
    }

    static long FirstAudioPageGranule(byte[] b)
    {
        var i = 0;
        while (i + 27 <= b.Length)
        {
            if (b[i] != 'O' || b[i + 1] != 'g' || b[i + 2] != 'g' || b[i + 3] != 'S') { i++; continue; }
            var gp = BitConverter.ToInt64(b, i + 6);
            var seg = b[i + 26];
            var len = 27 + seg;
            for (var s = 0; s < seg; s++) len += b[i + 27 + s];
            if (gp > 0) return gp;
            i += len;
        }
        return -1;
    }
}
