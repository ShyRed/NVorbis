using System;

namespace NVorbis.Contracts.Ogg
{
    interface IPageData : IPageReader
    {
        long PageOffset { get; }
        int StreamSerial { get; }
        int SequenceNumber { get; }
        PageFlags PageFlags { get; }
        long GranulePosition { get; }
        short PacketCount { get; }
        bool? IsResync { get; }
        bool IsContinued { get; }
        int PageOverhead { get; }

        /// <summary>
        /// Gets the data of a packet in the current page, or <see cref="Memory{T}.Empty"/> if the page has no such packet.
        /// The returned memory is only valid until the next page is read.
        /// </summary>
        Memory<byte> GetPacket(int packetIndex);
    }
}
