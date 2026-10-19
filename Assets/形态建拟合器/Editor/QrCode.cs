using System;
using System.Collections.Generic;
using System.Text;

namespace VrcModelCard
{
    /// <summary>
    /// Minimal QR Code encoder (ISO/IEC 18004): byte mode, error correction level L,
    /// versions 1-10 auto-selected (UTF-8 input). Used to render a scannable QR
    /// code as the card cover. The card data itself is still embedded in the PNG
    /// chunk/pixels as usual - the QR is only the visible cover, and can carry a
    /// short summary or a custom URL/text.
    /// </summary>
    public static class QrCode
    {
        // ---- QR tables (EC level L) ----
        private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
        private static readonly int[] EcPerBlock = { 0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 36 };
        private static readonly int[] BlockCount = { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2 };

        private static readonly int[][] AlignmentPositions =
        {
            new int[0],
            new int[0],
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 },
        };

        // ---- GF(256) arithmetic (primitive polynomial 0x11D) ----
        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }

        private static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        // ------------------------------------------------------------ public API
        /// <summary>Build a short summary text for the QR cover from a card.</summary>
        public static string AutoSummary(ModelCardData card)
        {
            string s = "VRC Model Card\n" + card.avatarName;
            if (!string.IsNullOrEmpty(card.author)) s += "\n" + card.author;
            if (!string.IsNullOrEmpty(card.description)) s += "\n" + card.description;
            return s;
        }

        /// <summary>Encode text as a QR code and render it to a PNG image.</summary>
        public static byte[] RenderPng(string text, int moduleScale = 12, int quietZone = 4)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");

            int version = 0;
            for (int v = 1; v <= 10; v++)
            {
                int dataCodewords = TotalCodewords[v] - EcPerBlock[v] * BlockCount[v];
                int capacity = v <= 9 ? dataCodewords - 2 : dataCodewords - 3;
                if (bytes.Length <= capacity) { version = v; break; }
            }
            if (version == 0)
            {
                version = 10;
                bytes = TruncateUtf8(bytes, TotalCodewords[10] - EcPerBlock[10] * BlockCount[10] - 3);
            }

            int size = 17 + 4 * version;
            var modules = new bool[size, size];
            var reserved = new bool[size, size];

            // 1. function patterns
            DrawFinder(modules, reserved, 0, 0);
            DrawFinder(modules, reserved, size - 7, 0);
            DrawFinder(modules, reserved, 0, size - 7);
            DrawTiming(modules, reserved, size);
            DrawAlignment(modules, reserved, version, size);
            reserved[size - 8, 8] = true;
            modules[size - 8, 8] = true; // dark module
            ReserveFormatAreas(reserved, size);
            if (version >= 7) ReserveVersionAreas(reserved, size);

            // 2. data bitstream (byte mode)
            int dataCodewordsTotal = TotalCodewords[version] - EcPerBlock[version] * BlockCount[version];
            var bits = new List<bool>();
            AppendBits(bits, 4, 4); // mode: byte
            AppendBits(bits, bytes.Length, version <= 9 ? 8 : 16);
            foreach (byte b in bytes) AppendBits(bits, b, 8);
            int capacityBits = dataCodewordsTotal * 8;
            int terminator = Math.Min(4, capacityBits - bits.Count);
            for (int i = 0; i < terminator; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            int[] pad = { 0xEC, 0x11 };
            int pi = 0;
            while (bits.Count < capacityBits) AppendBits(bits, pad[pi++ % 2], 8);

            // 3. Reed-Solomon + interleave
            byte[] dataBytes = BitsToBytes(bits);
            int ecLen = EcPerBlock[version];
            int blocks = BlockCount[version];
            int dataPerBlock = dataCodewordsTotal / blocks;
            var blockData = new byte[blocks][];
            var blockEc = new byte[blocks][];
            for (int i = 0; i < blocks; i++)
            {
                blockData[i] = new byte[dataPerBlock];
                Array.Copy(dataBytes, i * dataPerBlock, blockData[i], 0, dataPerBlock);
                blockEc[i] = ComputeRs(blockData[i], ecLen);
            }
            var final = new byte[dataCodewordsTotal + ecLen * blocks];
            int f = 0;
            for (int i = 0; i < dataPerBlock; i++)
                for (int b = 0; b < blocks; b++) final[f++] = blockData[b][i];
            for (int i = 0; i < ecLen; i++)
                for (int b = 0; b < blocks; b++) final[f++] = blockEc[b][i];

            // 4. place data
            PlaceData(modules, reserved, final, size);

            // 5. choose the best mask
            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                var copy = (bool[,])modules.Clone();
                ApplyMask(copy, mask, reserved, size);
                DrawFormatInfo(copy, mask, size);
                if (version >= 7) DrawVersionInfo(copy, version, size);
                int p = Penalty(copy, size);
                if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
            }

            // 6. finalize with the best mask
            ApplyMask(modules, bestMask, reserved, size);
            DrawFormatInfo(modules, bestMask, size);
            if (version >= 7) DrawVersionInfo(modules, version, size);

            return RenderToPng(modules, size, moduleScale, quietZone);
        }

        // ----------------------------------------------------------- QR internals
        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        private static byte[] BitsToBytes(List<bool> bits)
        {
            var result = new byte[bits.Count / 8];
            for (int i = 0; i < result.Length; i++)
            {
                byte b = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (bits[i * 8 + j]) b |= (byte)(1 << (7 - j));
                }
                result[i] = b;
            }
            return result;
        }

        private static byte[] TruncateUtf8(byte[] bytes, int maxBytes)
        {
            if (bytes.Length <= maxBytes) return bytes;
            int end = maxBytes;
            while (end > 0 && (bytes[end] & 0xC0) == 0x80) end--; // walk back over continuation bytes
            byte b = bytes[end];
            int len;
            if (b < 0x80) len = 1;
            else if ((b & 0xE0) == 0xC0) len = 2;
            else if ((b & 0xF0) == 0xE0) len = 3;
            else len = 4;
            if (end + len > maxBytes) end--; // drop the char that would overflow
            if (end <= 0) return new byte[0];
            var result = new byte[end];
            Array.Copy(bytes, result, end);
            return result;
        }

        // --------------------------------------------------------- RS error correction
        private static List<int> PolyMul(List<int> a, List<int> b)
        {
            var res = new List<int>(new int[a.Count + b.Count - 1]);
            for (int i = 0; i < a.Count; i++)
            {
                for (int j = 0; j < b.Count; j++)
                {
                    res[i + j] ^= GfMul(a[i], b[j]);
                }
            }
            return res;
        }

        private static byte[] ComputeRs(byte[] data, int ecLen)
        {
            var gen = new List<int> { 1 };
            for (int i = 0; i < ecLen; i++) gen = PolyMul(gen, new List<int> { 1, GfExp[i] });

            var rem = new int[ecLen];
            foreach (byte b in data)
            {
                int factor = b ^ rem[0];
                Array.Copy(rem, 1, rem, 0, ecLen - 1);
                rem[ecLen - 1] = 0;
                if (factor != 0)
                {
                    for (int i = 0; i < ecLen; i++)
                    {
                        rem[i] ^= GfMul(gen[i + 1], factor);
                    }
                }
            }
            var result = new byte[ecLen];
            for (int i = 0; i < ecLen; i++) result[i] = (byte)rem[i];
            return result;
        }

        // ------------------------------------------------------- format / version
        private static int ComputeFormatBits(int ecLevelBits, int mask)
        {
            int data = (ecLevelBits << 3) | mask; // 5 bits
            int rem = data << 10;
            const int gen = 0x537;
            for (int i = 14; i >= 10; i--)
            {
                if (((rem >> i) & 1) != 0) rem ^= gen << (i - 10);
            }
            return (((data << 10) | rem) ^ 0x5412) & 0x7FFF;
        }

        private static int ComputeVersionBits(int version)
        {
            int rem = version << 12;
            const int gen = 0x1F25;
            for (int i = 17; i >= 12; i--)
            {
                if (((rem >> i) & 1) != 0) rem ^= gen << (i - 12);
            }
            return (version << 12) | (rem & 0xFFF);
        }

        private static void SetBit(bool[,] m, int row, int col, int bits, int bitIndex)
        {
            m[row, col] = ((bits >> bitIndex) & 1) != 0;
        }

        private static void DrawFormatInfo(bool[,] m, int mask, int size)
        {
            int bits = ComputeFormatBits(1, mask); // EC level L
            SetBit(m, 8, 0, bits, 14); SetBit(m, 8, 1, bits, 13); SetBit(m, 8, 2, bits, 12);
            SetBit(m, 8, 3, bits, 11); SetBit(m, 8, 4, bits, 10); SetBit(m, 8, 5, bits, 9);
            SetBit(m, 8, 7, bits, 8); SetBit(m, 8, 8, bits, 7); SetBit(m, 7, 8, bits, 6);
            SetBit(m, 5, 8, bits, 5); SetBit(m, 4, 8, bits, 4); SetBit(m, 3, 8, bits, 3);
            SetBit(m, 2, 8, bits, 2); SetBit(m, 1, 8, bits, 1); SetBit(m, 0, 8, bits, 0);

            SetBit(m, size - 1, 8, bits, 14); SetBit(m, size - 2, 8, bits, 13);
            SetBit(m, size - 3, 8, bits, 12); SetBit(m, size - 4, 8, bits, 11);
            SetBit(m, size - 5, 8, bits, 10); SetBit(m, size - 6, 8, bits, 9);
            SetBit(m, size - 7, 8, bits, 8); SetBit(m, 8, size - 8, bits, 7);
            SetBit(m, 8, size - 7, bits, 6); SetBit(m, 8, size - 6, bits, 5);
            SetBit(m, 8, size - 5, bits, 4); SetBit(m, 8, size - 4, bits, 3);
            SetBit(m, 8, size - 3, bits, 2); SetBit(m, 8, size - 2, bits, 1);
            SetBit(m, 8, size - 1, bits, 0);
        }

        private static void DrawVersionInfo(bool[,] m, int version, int size)
        {
            int bits = ComputeVersionBits(version);
            for (int i = 0; i < 18; i++)
            {
                bool dark = ((bits >> (17 - i)) & 1) != 0;
                int r1 = i % 6, c1 = i / 6;
                m[r1, size - 11 + c1] = dark;            // block 1 (bottom-left area)
                m[size - 11 + c1, r1] = dark;            // block 2 (top-right area)
            }
        }

        private static void ReserveFormatAreas(bool[,] r, int size)
        {
            for (int i = 0; i <= 5; i++) { r[8, i] = true; r[i, 8] = true; }
            r[8, 7] = true; r[8, 8] = true; r[7, 8] = true;
            for (int i = 0; i <= 7; i++)
            {
                r[size - 1 - i, 8] = true;
                r[8, size - 1 - i] = true;
            }
        }

        private static void ReserveVersionAreas(bool[,] r, int size)
        {
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    r[i, size - 11 + j] = true;
                    r[size - 11 + j, i] = true;
                }
            }
        }

        // ------------------------------------------------------- function patterns
        private static void DrawFinder(bool[,] m, bool[,] r, int row, int col)
        {
            for (int i = -1; i <= 7; i++)
            {
                for (int j = -1; j <= 7; j++)
                {
                    int rr = row + i, cc = col + j;
                    if (rr < 0 || rr >= m.GetLength(0) || cc < 0 || cc >= m.GetLength(1)) continue;
                    bool dark = i >= 0 && i <= 6 && j >= 0 && j <= 6
                        && (i == 0 || i == 6 || j == 0 || j == 6 || (i >= 2 && i <= 4 && j >= 2 && j <= 4));
                    m[rr, cc] = dark;
                    r[rr, cc] = true;
                }
            }
        }

        private static void DrawTiming(bool[,] m, bool[,] r, int size)
        {
            for (int i = 8; i < size - 8; i++)
            {
                m[6, i] = i % 2 == 0; r[6, i] = true;
                m[i, 6] = i % 2 == 0; r[i, 6] = true;
            }
        }

        private static void DrawAlignment(bool[,] m, bool[,] r, int version, int size)
        {
            int[] pos = AlignmentPositions[version];
            for (int a = 0; a < pos.Length; a++)
            {
                for (int b = 0; b < pos.Length; b++)
                {
                    int cy = pos[a], cx = pos[b];
                    if (r[cy, cx]) continue; // overlaps a finder/timing -> skip
                    for (int i = -2; i <= 2; i++)
                    {
                        for (int j = -2; j <= 2; j++)
                        {
                            bool dark = Math.Max(Math.Abs(i), Math.Abs(j)) != 1;
                            m[cy + i, cx + j] = dark;
                            r[cy + i, cx + j] = true;
                        }
                    }
                }
            }
        }

        // ----------------------------------------------------------- data placement
        private static void PlaceData(bool[,] m, bool[,] r, byte[] data, int size)
        {
            int i = 0;
            bool upward = true;
            int right = size - 1;
            while (right > 0)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    int row = upward ? size - 1 - vert : vert;
                    for (int c = 0; c < 2; c++)
                    {
                        int col = right - c;
                        if (col < 0) continue;
                        if (!r[row, col])
                        {
                            bool bit = i < data.Length * 8 && ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            m[row, col] = bit;
                            i++;
                        }
                    }
                }
                upward = !upward;
                right -= 2;
            }
        }

        // ------------------------------------------------------------- masking
        private static bool Masked(int mask, int row, int col)
        {
            switch (mask)
            {
                case 0: return (row + col) % 2 == 0;
                case 1: return row % 2 == 0;
                case 2: return col % 3 == 0;
                case 3: return (row + col) % 3 == 0;
                case 4: return (row / 2 + col / 3) % 2 == 0;
                case 5: return (row * col) % 2 + (row * col) % 3 == 0;
                case 6: return ((row * col) % 2 + (row * col) % 3) % 2 == 0;
                default: return ((row + col) % 2 + (row * col) % 3) % 2 == 0;
            }
        }

        private static void ApplyMask(bool[,] m, int mask, bool[,] reserved, int size)
        {
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (!reserved[r, c] && Masked(mask, r, c)) m[r, c] = !m[r, c];
                }
            }
        }

        private static int Penalty(bool[,] m, int size)
        {
            int score = 0;
            // N1: runs of >=5 in rows
            for (int r = 0; r < size; r++)
            {
                int run = 1;
                bool prev = m[r, 0];
                for (int c = 1; c < size; c++)
                {
                    bool cur = m[r, c];
                    if (cur == prev) run++;
                    else { if (run >= 5) score += 3 + (run - 5); run = 1; prev = cur; }
                }
                if (run >= 5) score += 3 + (run - 5);
            }
            // N1: runs of >=5 in columns
            for (int c = 0; c < size; c++)
            {
                int run = 1;
                bool prev = m[0, c];
                for (int r = 1; r < size; r++)
                {
                    bool cur = m[r, c];
                    if (cur == prev) run++;
                    else { if (run >= 5) score += 3 + (run - 5); run = 1; prev = cur; }
                }
                if (run >= 5) score += 3 + (run - 5);
            }
            // N2: 2x2 blocks
            for (int r = 0; r < size - 1; r++)
            {
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (m[r, c + 1] == v && m[r + 1, c] == v && m[r + 1, c + 1] == v) score += 3;
                }
            }
            // N3: finder-like patterns (1:0:1:1:1:0:1 with 4 light on either side)
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size - 10; c++)
                {
                    if (IsFinderPattern(m, r, c, true, size)) score += 40;
                }
            }
            for (int c = 0; c < size; c++)
            {
                for (int r = 0; r < size - 10; r++)
                {
                    if (IsFinderPattern(m, r, c, false, size)) score += 40;
                }
            }
            // N4: dark module proportion
            int dark = 0;
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (m[r, c]) dark++;
                }
            }
            int percent = (int)((dark * 100L) / (size * size));
            score += (Math.Abs(percent - 50) / 5) * 10;
            return score;
        }

        private static bool IsFinderPattern(bool[,] m, int startRow, int startCol, bool horizontal, int size)
        {
            bool[] pat = { true, false, true, true, true, false, true };
            for (int i = 0; i < 7; i++)
            {
                bool v = horizontal ? m[startRow, startCol + i] : m[startRow + i, startCol];
                if (v != pat[i]) return false;
            }
            bool okBefore = true, okAfter = true;
            for (int i = 1; i <= 4; i++)
            {
                int b = horizontal ? startCol - i : startRow - i;
                int a = horizontal ? startCol + 6 + i : startRow + 6 + i;
                if (b < 0) okBefore = false;
                else if (horizontal ? m[startRow, b] : m[b, startCol]) okBefore = false;
                if (a >= size) okAfter = false;
                else if (horizontal ? m[startRow, a] : m[a, startCol]) okAfter = false;
            }
            return okBefore || okAfter;
        }

        // ------------------------------------------------------------- rendering
        private static byte[] RenderToPng(bool[,] modules, int size, int moduleScale, int quietZone)
        {
            int dim = (size + quietZone * 2) * moduleScale;
            var rgba = new byte[dim * dim * 4];
            for (int y = 0; y < dim; y++)
            {
                int my = (y / moduleScale) - quietZone;
                for (int x = 0; x < dim; x++)
                {
                    int mx = (x / moduleScale) - quietZone;
                    bool dark = my >= 0 && my < size && mx >= 0 && mx < size && modules[my, mx];
                    int p = (y * dim + x) * 4;
                    rgba[p] = dark ? (byte)0 : (byte)255;
                    rgba[p + 1] = dark ? (byte)0 : (byte)255;
                    rgba[p + 2] = dark ? (byte)0 : (byte)255;
                    rgba[p + 3] = 255;
                }
            }
            return PngCodec.CreatePngFromRgba(rgba, dim, dim);
        }
    }
}
