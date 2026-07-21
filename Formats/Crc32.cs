using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// CRC-32 used by Level-5 T2B (.cfg.bin) files. Two variants share the reflected
    /// polynomial 0xEDB88320: the standard reflected CRC-32 (inverted output) and the
    /// "JAM" variant (same table, non-inverted output). Which one a file uses is
    /// auto-detected by hashing the first known name and comparing to the stored value.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        /// <summary>Standard reflected CRC-32 (final XOR with 0xFFFFFFFF).</summary>
        public static uint Standard(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>JAM CRC-32 (no final inversion).</summary>
        public static uint Jam(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Compute(string text, HashType type, Encoding encoding)
        {
            byte[] data = encoding.GetBytes(text);
            return type == HashType.Crc32Jam ? Jam(data) : Standard(data);
        }
    }

    public enum HashType
    {
        Crc32Standard,
        Crc32Jam
    }
}
