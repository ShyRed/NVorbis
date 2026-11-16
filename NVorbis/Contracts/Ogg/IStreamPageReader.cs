using System;

namespace NVorbis.Contracts.Ogg
{
    interface IStreamPageReader
    {
        IPacketProvider PacketProvider { get; }

        void AddPage();

        /// <summary>
        /// Gets the data of a packet in the specified page, or <see cref="Memory{T}.Empty"/> if the page has no such packet.
        /// The returned memory is only valid until the next page is read.
        /// </summary>
        Memory<byte> GetPagePacket(int pageIndex, int packetIndex);

        int FindPage(long granulePos);

        bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead);

        void SetEndOfStream();

        int PageCount { get; }

        bool HasAllPages { get; }

        long? MaxGranulePosition { get; }

        int FirstDataPageIndex { get; }
    }
}
