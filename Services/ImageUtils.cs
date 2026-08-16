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
    }
}
