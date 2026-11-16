namespace NVorbis.Contracts
{
    interface IMdct
    {
        /// <summary>
        /// Prepares the tables and scratch buffers for the given transform size, so the first <see cref="Reverse"/> call for it does not allocate.
        /// </summary>
        void Setup(int sampleCount);

        void Reverse(float[] samples, int sampleCount);
    }
}
