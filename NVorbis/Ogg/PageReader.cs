using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace NVorbis.Ogg
{
    class PageReader : PageReaderBase, IPageData
    {
        internal static Func<IPageData, int, IStreamPageReader> CreateStreamPageReader { get; set; } = (pr, ss) => new StreamPageReader(pr, ss);

        private readonly Dictionary<int, IStreamPageReader> _streamReaders = new Dictionary<int, IStreamPageReader>();
        private readonly Func<Contracts.IPacketProvider, bool> _newStreamCallback;
        private readonly object _readLock = new object();

        private long _nextPageOffset;
        private ushort _pageSize;

        // The current page lives in _pageBuf (reused for every page read), its packets are sliced into _packets[0 .. PacketCount).
        // _hasPackets says whether _packets is valid for the page at PageOffset.
        private readonly byte[] _pageBuf = new byte[MaxPageSize];
        private readonly Memory<byte>[] _packets = new Memory<byte>[255];
        private bool _hasPackets;

        public PageReader(Stream stream, bool closeOnDispose, Func<Contracts.IPacketProvider, bool> newStreamCallback)
            : base(stream, closeOnDispose)
        {
            _newStreamCallback = newStreamCallback;
        }

        private ushort ParsePageHeader(byte[] pageBuf, int? streamSerial, bool? isResync)
        {
            var segCnt = pageBuf[26];
            var dataLen = 0;
            var pktCnt = 0;
            var isContinued = false;

            var size = 0;
            for (int i = 0, idx = 27; i < segCnt; i++, idx++)
            {
                var seg = pageBuf[idx];
                size += seg;
                dataLen += seg;
                if (seg < 255)
                {
                    if (size > 0)
                    {
                        ++pktCnt;
                    }
                    size = 0;
                }
            }
            if (size > 0)
            {
                isContinued = pageBuf[segCnt + 26] == 255;
                ++pktCnt;
            }

            StreamSerial = streamSerial ?? BitConverter.ToInt32(pageBuf, 14);
            SequenceNumber = BitConverter.ToInt32(pageBuf, 18);
            PageFlags = (PageFlags)pageBuf[5];
            GranulePosition = BitConverter.ToInt64(pageBuf, 6);
            PacketCount = (short)pktCnt;
            IsResync = isResync;
            IsContinued = isContinued;
            PageOverhead = 27 + segCnt;
            return (ushort)(PageOverhead + dataLen);
        }

        private static void ReadPackets(Memory<byte>[] list, Span<byte> segments, Memory<byte> dataBuffer)
        {
            var listIdx = 0;
            var dataIdx = 0;
            var size = 0;

            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                size += seg;
                if (seg < 255)
                {
                    if (size > 0)
                    {
                        list[listIdx++] = dataBuffer.Slice(dataIdx, size);
                        dataIdx += size;
                    }
                    size = 0;
                }
            }
            if (size > 0)
            {
                list[listIdx] = dataBuffer.Slice(dataIdx, size);
            }
        }

        protected override bool ReusesPageBuffer => true;

        protected override byte[] GetPageBuffer(int pageLength)
        {
            // the current page's data is about to be overwritten
            _hasPackets = false;
            PageOffset = -1;
            return _pageBuf;
        }

        public override void Lock()
        {
            Monitor.Enter(_readLock);
        }

        protected override bool CheckLock()
        {
            return Monitor.IsEntered(_readLock);
        }

        public override bool Release()
        {
            if (Monitor.IsEntered(_readLock))
            {
                Monitor.Exit(_readLock);
                return true;
            }
            return false;
        }

        protected override void SaveNextPageSearch()
        {
            _nextPageOffset = StreamPosition;
        }

        protected override void PrepareStreamForNextPage()
        {
            SeekStream(_nextPageOffset);
        }

        protected override bool AddPage(int streamSerial, byte[] pageBuf, int pageLength, bool isResync)
        {
            PageOffset = StreamPosition - pageLength;
            ParsePageHeader(pageBuf, streamSerial, isResync);

            // if the page doesn't have any packets, we can't use it
            if (PacketCount == 0) return false;

            ReadPackets(_packets, new Span<byte>(pageBuf, 27, pageBuf[26]), new Memory<byte>(pageBuf, 27 + pageBuf[26], pageLength - 27 - pageBuf[26]));
            _hasPackets = true;

            if (_streamReaders.TryGetValue(streamSerial, out var spr))
            {
                spr.AddPage();

                // if we've read the last page, remove from our list so cleanup can happen.
                // this is safe because the instance still has access to us for reading.
                if ((PageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream)
                {
                    _streamReaders.Remove(StreamSerial);
                }
            }
            else
            {
                var streamReader = CreateStreamPageReader(this, StreamSerial);
                streamReader.AddPage();
                _streamReaders.Add(StreamSerial, streamReader);
                if (!_newStreamCallback(streamReader.PacketProvider))
                {
                    _streamReaders.Remove(StreamSerial);
                    return false;
                }
            }
            return true;
        }

        public override bool ReadPageAt(long offset)
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            // this should be safe; we've already checked the page by now

            if (offset == PageOffset)
            {
                // short circuit for when we've already loaded the page
                return true;
            }

            // the header goes into the page buffer, so the current page's packets are no longer valid
            var hdrBuf = _pageBuf;
            _hasPackets = false;

            SeekStream(offset);
            var cnt = EnsureRead(hdrBuf, 0, 27);

            PageOffset = offset;
            if (VerifyHeader(hdrBuf, 0, ref cnt))
            {
                // don't read the whole page yet; if our caller is seeking, they won't need packets anyway
                _pageSize = ParsePageHeader(hdrBuf, null, null);
                return true;
            }
            return false;
        }

        protected override void SetEndOfStreams()
        {
            foreach (var kvp in _streamReaders)
            {
                kvp.Value.SetEndOfStream();
            }
            _streamReaders.Clear();
        }


        #region IPacketData

        public long PageOffset { get; private set; }

        public int StreamSerial { get; private set; }

        public int SequenceNumber { get; private set; }

        public PageFlags PageFlags { get; private set; }

        public long GranulePosition { get; private set; }

        public short PacketCount { get; private set; }

        public bool? IsResync { get; private set; }

        public bool IsContinued { get; private set; }

        public int PageOverhead { get; private set; }

        public Memory<byte> GetPacket(int packetIndex)
        {
            if (!CheckLock()) throw new InvalidOperationException("Must be locked!");

            if (!_hasPackets)
            {
                var pageBuf = _pageBuf;
                SeekStream(PageOffset);
                EnsureRead(pageBuf, 0, _pageSize);
                ReadPackets(_packets, new Span<byte>(pageBuf, 27, pageBuf[26]), new Memory<byte>(pageBuf, 27 + pageBuf[26], _pageSize - 27 - pageBuf[26]));
                _hasPackets = true;
            }

            // the returned memory is only valid until the next page is read
            return packetIndex < PacketCount ? _packets[packetIndex] : Memory<byte>.Empty;
        }

        #endregion
    }
}