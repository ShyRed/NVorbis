using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class Packet : DataPacket
    {
        // Instances are owned and reused by PacketProvider (see PacketProvider.CreatePacket): a packet returned by the provider
        // is only valid until the provider creates its next packet.

        // this is the list of pages & packets in packed 24:8 format
        // in theory, this is good for up to 1016 GiB of Ogg file
        // in practice, probably closer to 300 days @ 160k bps
        private readonly List<int> _dataParts = new List<int>(8);
        private readonly IPacketReader _packetReader;
        int _dataCount;
        Memory<byte> _data;
        bool _dataLoaded;
        int _dataIndex;
        int _dataOfs;

        internal Packet(IPacketReader packetReader)
        {
            _packetReader = packetReader;
        }

        /// <summary>
        /// Clears the packet so it can be filled for a new packet: the caller adds the data parts to <see cref="DataParts"/> afterwards.
        /// </summary>
        internal void Init(bool isResync)
        {
            ClearPacketState();
            _dataParts.Clear();
            _dataCount = 0;
            _data = Memory<byte>.Empty;
            _dataLoaded = false;
            _dataIndex = 0;
            _dataOfs = 0;
            IsResync = isResync;
        }

        internal List<int> DataParts => _dataParts;

        // The data of a part is fetched only when it is about to be read: the page reader reuses its page buffer, so data
        // fetched before other pages are read (e.g. while looking for the end of a continued packet) would be stale.
        private void EnsureData()
        {
            if (!_dataLoaded)
            {
                _dataLoaded = true;
                if (_dataIndex < _dataParts.Count)
                {
                    _data = _packetReader.GetPacketData(_dataParts[_dataIndex]);
                }
            }
        }

        protected override int TotalBits
        {
            get
            {
                EnsureData();
                return (_dataCount + _data.Length) * 8;
            }
        }

        protected override int ReadNextByte()
        {
            if (_dataIndex == _dataParts.Count) return -1;

            EnsureData();

            var b = _data.Span[_dataOfs];

            if (++_dataOfs == _data.Length)
            {
                _dataOfs = 0;
                _dataCount += _data.Length;
                if (++_dataIndex < _dataParts.Count)
                {
                    _data = _packetReader.GetPacketData(_dataParts[_dataIndex]);
                }
                else
                {
                    _data = Memory<byte>.Empty;
                }
            }

            return b;
        }

        public override void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;
            if (_dataParts.Count > 0)
            {
                // re-fetched on the next read
                _dataLoaded = false;
            }

            base.Reset();
        }

        public override void Done()
        {
            _packetReader?.InvalidatePacketCache(this);

            base.Done();
        }
    }
}
