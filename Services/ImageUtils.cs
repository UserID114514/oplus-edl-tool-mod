using System;
using System.IO;

namespace OplusEdlTool.Services
{
    public static class ImageUtils
    {
        // Android sparse image magic, stored little-endian at offset 0 (0xED26FF3A -> bytes 3A FF 26 ED).
        private const uint SparseMagic = 0xED26FF3A;

        /// <summary>
        /// Returns true if the file is an Android sparse image (as produced by img2simg / lpmake --sparse),
        /// false if it is a raw image (or unreadable/too short).
        /// </summary>
        public static bool IsSparseImage(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> buf = stackalloc byte[4];
                if (fs.Read(buf) < 4) return false;
                return BitConverter.ToUInt32(buf) == SparseMagic;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the expanded (non-sparse) size in bytes of an Android sparse image, derived from
        /// its header (blk_sz * total_blks). Returns null if the file is not sparse or unreadable.
        /// </summary>
        public static long? GetSparseExpandedSize(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> buf = stackalloc byte[28];
                if (fs.Read(buf) < 28) return null;
                if (BitConverter.ToUInt32(buf) != SparseMagic) return null;
                uint blkSz = BitConverter.ToUInt32(buf.Slice(12, 4));
                uint totalBlks = BitConverter.ToUInt32(buf.Slice(16, 4));
                return (long)blkSz * totalBlks;
            }
            catch
            {
                return null;
            }
        }
    }
}
