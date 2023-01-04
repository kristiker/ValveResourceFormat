// Credit to https://github.com/mafaca/Etc

using System.Runtime.CompilerServices;
using System;

namespace ValveResourceFormat.TextureDecoders
{
    internal class CommonETC
    {
        protected static readonly byte[] WriteOrderTable = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
        protected static readonly int[,] Etc1ModifierTable =
        {
            { 2, 8, -2, -8, },
            { 5, 17, -5, -17, },
            { 9, 29, -9, -29,},
            { 13, 42, -13, -42, },
            { 18, 60, -18, -60, },
            { 24, 80, -24, -80, },
            { 33, 106, -33, -106, },
            { 47, 183, -47, -183, }
        };
        protected static readonly byte[,] Etc1SubblockTable =
        {
            {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1},
            {0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1}
        };
        protected static readonly byte[] Etc2DistanceTable = { 3, 6, 11, 16, 23, 32, 41, 64 };


        protected readonly uint[] m_buf = new uint[16];
        protected byte[,] m_c = new byte[3, 3];

        protected void DecodeEtc2Block(Span<byte> data, int offset)
        {
            ushort j = (ushort)(data[offset + 6] << 8 | data[offset + 7]);
            ushort k = (ushort)(data[offset + 4] << 8 | data[offset + 5]);

            if ((data[offset + 3] & 2) != 0)
            {
                byte r = (byte)(data[offset + 0] & 0xf8);
                short dr = (short)((data[offset + 0] << 3 & 0x18) - (data[offset + 0] << 3 & 0x20));
                byte g = (byte)(data[offset + 1] & 0xf8);
                short dg = (short)((data[offset + 1] << 3 & 0x18) - (data[offset + 1] << 3 & 0x20));
                byte b = (byte)(data[offset + 2] & 0xf8);
                short db = (short)((data[offset + 2] << 3 & 0x18) - (data[offset + 2] << 3 & 0x20));
                if (r + dr < 0 || r + dr > 255)
                {
                    // T
                    unchecked
                    {
                        m_c[0, 0] = (byte)(data[offset + 0] << 3 & 0xc0 | data[offset + 0] << 4 & 0x30 | data[offset + 0] >> 1 & 0xc | data[offset + 0] & 3);
                        m_c[0, 1] = (byte)(data[offset + 1] & 0xf0 | data[offset + 1] >> 4);
                        m_c[0, 2] = (byte)(data[offset + 1] & 0x0f | data[offset + 1] << 4);
                        m_c[1, 0] = (byte)(data[offset + 2] & 0xf0 | data[offset + 2] >> 4);
                        m_c[1, 1] = (byte)(data[offset + 2] & 0x0f | data[offset + 2] << 4);
                        m_c[1, 2] = (byte)(data[offset + 3] & 0xf0 | data[offset + 3] >> 4);
                    }
                    byte d = Etc2DistanceTable[data[offset + 3] >> 1 & 6 | data[offset + 3] & 1];
                    uint[] color_set =
                    {
                        ApplicateColorRaw(m_c, 0),
                        ApplicateColor(m_c, 1, d),
                        ApplicateColorRaw(m_c, 1),
                        ApplicateColor(m_c, 1, -d)
                    };
                    for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                    {
                        m_buf[WriteOrderTable[i]] = color_set[k << 1 & 2 | j & 1];
                    }
                }
                else if (g + dg < 0 || g + dg > 255)
                {
                    // H
                    unchecked
                    {
                        m_c[0, 0] = (byte)(data[offset + 0] << 1 & 0xf0 | data[offset + 0] >> 3 & 0xf);
                        m_c[0, 1] = (byte)(data[offset + 0] << 5 & 0xe0 | data[offset + 1] & 0x10);
                        m_c[0, 1] |= (byte)(m_c[0, 1] >> 4);
                        m_c[0, 2] = (byte)(data[offset + 1] & 8 | data[offset + 1] << 1 & 6 | data[offset + 2] >> 7);
                        m_c[0, 2] |= (byte)(m_c[0, 2] << 4);
                        m_c[1, 0] = (byte)(data[offset + 2] << 1 & 0xf0 | data[offset + 2] >> 3 & 0xf);
                        m_c[1, 1] = (byte)(data[offset + 2] << 5 & 0xe0 | data[offset + 3] >> 3 & 0x10);
                        m_c[1, 1] |= (byte)(m_c[1, 1] >> 4);
                        m_c[1, 2] = (byte)(data[offset + 3] << 1 & 0xf0 | data[offset + 3] >> 3 & 0xf);
                    }
                    int di = data[offset + 3] & 4 | data[offset + 3] << 1 & 2;
                    if (m_c[0, 0] > m_c[1, 0] || (m_c[0, 0] == m_c[1, 0] && (m_c[0, 1] > m_c[1, 1] || (m_c[0, 1] == m_c[1, 1] && m_c[0, 2] >= m_c[1, 2]))))
                    {
                        ++di;
                    }
                    byte d = Etc2DistanceTable[di];
                    uint[] color_set =
                    {
                        ApplicateColor(m_c, 0, d),
                        ApplicateColor(m_c, 0, -d),
                        ApplicateColor(m_c, 1, d),
                        ApplicateColor(m_c, 1, -d)
                    };
                    for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                    {
                        m_buf[WriteOrderTable[i]] = color_set[k << 1 & 2 | j & 1];
                    }
                }
                else if (b + db < 0 || b + db > 255)
                {
                    // planar
                    unchecked
                    {
                        m_c[0, 0] = (byte)(data[offset + 0] << 1 & 0xfc | data[offset + 0] >> 5 & 3);
                        m_c[0, 1] = (byte)(data[offset + 0] << 7 & 0x80 | data[offset + 1] & 0x7e | data[offset + 0] & 1);
                        m_c[0, 2] = (byte)(data[offset + 1] << 7 & 0x80 | data[offset + 2] << 2 & 0x60 | data[offset + 2] << 3 & 0x18 | data[offset + 3] >> 5 & 4);
                        m_c[0, 2] |= (byte)(m_c[0, 2] >> 6);
                        m_c[1, 0] = (byte)(data[offset + 3] << 1 & 0xf8 | data[offset + 3] << 2 & 4 | data[offset + 3] >> 5 & 3);
                        m_c[1, 1] = (byte)(data[offset + 4] & 0xfe | data[offset + 4] >> 7);
                        m_c[1, 2] = (byte)(data[offset + 4] << 7 & 0x80 | data[offset + 5] >> 1 & 0x7c);
                        m_c[1, 2] |= (byte)(m_c[1, 2] >> 6);
                        m_c[2, 0] = (byte)(data[offset + 5] << 5 & 0xe0 | data[offset + 6] >> 3 & 0x1c | data[offset + 5] >> 1 & 3);
                        m_c[2, 1] = (byte)(data[offset + 6] << 3 & 0xf8 | data[offset + 7] >> 5 & 0x6 | data[offset + 6] >> 4 & 1);
                        m_c[2, 2] = (byte)(data[offset + 7] << 2 | data[offset + 7] >> 4 & 3);
                    }
                    for (int y = 0, i = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++, i++)
                        {
                            int ri = Clamp255((x * (m_c[1, 0] - m_c[0, 0]) + y * (m_c[2, 0] - m_c[0, 0]) + 4 * m_c[0, 0] + 2) >> 2);
                            int gi = Clamp255((x * (m_c[1, 1] - m_c[0, 1]) + y * (m_c[2, 1] - m_c[0, 1]) + 4 * m_c[0, 1] + 2) >> 2);
                            int bi = Clamp255((x * (m_c[1, 2] - m_c[0, 2]) + y * (m_c[2, 2] - m_c[0, 2]) + 4 * m_c[0, 2] + 2) >> 2);
                            m_buf[i] = Color(ri, gi, bi, 255);
                        }
                    }
                }
                else
                {
                    // differential
                    byte[] code = { (byte)(data[offset + 3] >> 5), (byte)(data[offset + 3] >> 2 & 7) };
                    int ti = data[offset + 3] & 1;
                    unchecked
                    {
                        m_c[0, 0] = (byte)(r | r >> 5);
                        m_c[0, 1] = (byte)(g | g >> 5);
                        m_c[0, 2] = (byte)(b | b >> 5);
                        m_c[1, 0] = (byte)(r + dr);
                        m_c[1, 1] = (byte)(g + dg);
                        m_c[1, 2] = (byte)(b + db);
                        m_c[1, 0] |= (byte)(m_c[1, 0] >> 5);
                        m_c[1, 1] |= (byte)(m_c[1, 1] >> 5);
                        m_c[1, 2] |= (byte)(m_c[1, 2] >> 5);
                    }
                    for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                    {
                        byte s = Etc1SubblockTable[ti, i];
                        int index = k << 1 & 2 | j & 1;
                        int m = Etc1ModifierTable[code[s], index];
                        m_buf[WriteOrderTable[i]] = ApplicateColor(m_c, s, m);
                    }
                }
            }
            else
            {
                // individual
                byte[] code = { (byte)(data[offset + 3] >> 5), (byte)(data[offset + 3] >> 2 & 7) };
                int ti = data[offset + 3] & 1;
                unchecked
                {
                    m_c[0, 0] = (byte)(data[offset + 0] & 0xf0 | data[offset + 0] >> 4);
                    m_c[1, 0] = (byte)(data[offset + 0] & 0x0f | data[offset + 0] << 4);
                    m_c[0, 1] = (byte)(data[offset + 1] & 0xf0 | data[offset + 1] >> 4);
                    m_c[1, 1] = (byte)(data[offset + 1] & 0x0f | data[offset + 1] << 4);
                    m_c[0, 2] = (byte)(data[offset + 2] & 0xf0 | data[offset + 2] >> 4);
                    m_c[1, 2] = (byte)(data[offset + 2] & 0x0f | data[offset + 2] << 4);
                }
                for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                {
                    byte s = Etc1SubblockTable[ti, i];
                    int index = k << 1 & 2 | j & 1;
                    int m = Etc1ModifierTable[code[s], index];
                    m_buf[WriteOrderTable[i]] = ApplicateColor(m_c, s, m);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static int Clamp255(int n)
        {
            return n < 0 ? 0 : n > 255 ? 255 : n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Color(int r, int g, int b, int a)
        {
            return unchecked((uint)(r << 16 | g << 8 | b | a << 24));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ApplicateColor(byte[,] c, int o, int m)
        {
            return Color(Clamp255(c[o, 0] + m), Clamp255(c[o, 1] + m), Clamp255(c[o, 2] + m), 255);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ApplicateColorRaw(byte[,] c, int o)
        {
            return Color(c[o, 0], c[o, 1], c[o, 2], 255);
        }
    }
}
