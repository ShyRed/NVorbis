# ShyRed.NVorbis

A fully managed Ogg Vorbis decoder for .NET: a fork of [NVorbis](https://github.com/NVorbis/NVorbis) 0.10.5 by Andrew Ward, under the same MIT licence. The assembly and the namespace are still `NVorbis`, so code written for NVorbis 0.10.5 works unchanged.

Source: https://github.com/ShyRed/NVorbis (branch `fix/no-garbage-per-packet`).

## What the fork changes

- **No garbage while decoding:** once a stream is warm, `ReadSamples` and `SeekTo` allocate nothing. NVorbis 0.10.5 allocates about 5 bytes a frame per channel. The output of a full decode is bit-identical to 0.10.5's.
- **Exact seeks inside the first audio page.** 0.10.5 landed 128 frames early there, and never returned for frames 1 to 128.
- Seeking before the first audio page has been read no longer throws.
- Seeking into a last page with a clipped granule position no longer throws, and trims the end correctly.
- Exact seeks inside a single-page stream.

## Use

```cs
using var vorbis = new NVorbis.VorbisReader("path/to/file.ogg");
var buffer = new float[vorbis.Channels * 4096];
int read;
while ((read = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
{
    // interleaved samples in buffer[0..read]
}
```

To stream with no allocations at all, read `TotalSamples` once after opening, on a loading thread: it indexes every page, so later reads and seeks never grow that index.

## Limits

- Non-seekable streams (forward-only reading) still allocate per packet.
- Packets from `IPacketProvider` are reused: a packet is valid only until the provider makes its next one.
- Two logical streams of one chained container must not be read from different threads at once.
