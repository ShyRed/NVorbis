# AllocBench

Measurement tool for the no-garbage work on this branch. It is not part of the
NVorbis library and is not packed.

It builds against either:

- the branch source (default), or
- the official NuGet `NVorbis 0.10.5` binary, with `-p:Baseline=true`. Give the
  baseline build its own `obj`/`bin` outside this folder, e.g.
  `-p:BaseIntermediateOutputPath=<dir>/obj/ -p:BaseOutputPath=<dir>/bin/`.

```
dotnet build -c Release tools/AllocBench
dotnet tools/AllocBench/bin/Release/net10.0/AllocBench.dll <command> ...
```

| command | what it does |
|---|---|
| `alloc <files...>` | Bytes allocated per decoded frame on a warm stream (`GC.GetAllocatedBytesForCurrentThread()` around each 1,024-frame `ReadSamples`), plus bytes allocated by a warm `SamplePosition` seek to 0, the middle and near the end. |
| `types <loops> <files...>` | Which types are allocated, from `GCAllocationTick` events (GC keyword, verbose) of `Microsoft-Windows-DotNETRuntime`. |
| `dump <outDir> <files...>` | Writes the decoded output (full decode with 1,024, 4,096 and 333-frame reads, plus a seek and loop pattern) as raw float32 and prints SHA-256 prefixes. Run it with both builds and compare the files byte for byte. |
| `speed <iterations> <files...>` | Decode time per frame (min and median over the iterations, after 3 warm-up runs). |
| `seekcheck <files...>` | Seeks to frames around the start and the end of the first audio page and checks the returned frames against a full decode. Reports exact, off by N frames, or a hang. |

Test audio can be made with ffmpeg, e.g.:

```
ffmpeg -f lavfi -i "anoisesrc=color=pink:sample_rate=48000:amplitude=0.5:duration=10:seed=42" -ac 2 -c:a libvorbis -q:a 5 pink_ch2_q5.ogg
```
