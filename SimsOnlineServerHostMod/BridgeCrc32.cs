using System;

namespace SimsOnlineServerHost
{
    internal static class BridgeCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data)
        {
            return Compute(data, 0, data.Length);
        }

        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ data[offset + i]) & 0xFF];
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((entry & 1) != 0)
                    {
                        entry = (entry >> 1) ^ 0xEDB88320;
                    }
                    else
                    {
                        entry >>= 1;
                    }
                }

                table[i] = entry;
            }

            return table;
        }
    }
}
