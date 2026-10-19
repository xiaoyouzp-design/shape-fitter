using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Pure C# PNG reader/writer used to embed a ModelCard payload into a PNG file.
    ///
    /// Two independent channels are used so the card survives as many image
    /// pipelines as possible:
    ///   1. A custom ancillary PNG chunk ("mcrd") inserted before IEND. PNG is a
    ///      lossless format, so any tool that re-encodes losslessly keeps the chunk.
    ///   2. An optional LSB (least-significant-bit) pixel backup. The payload bits
    ///      are hidden in the low bits of RGBA channels. This survives tools that
    ///      strip metadata/chunks but keep the pixels (e.g. naive re-encoders).
    ///
    /// Nothing survives lossy compression (JPEG / strong quantization) — see README.
    /// </summary>
    public static class PngCodec
    {
        public const string CardChunkType = "mcrd"; // ancillary + safe-to-copy per PNG spec

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private const byte ColorTypeRgba = 6;

        // ------------------------------------------------------------------ CRC32
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }

        // -------------------------------------------------------------- chunk IO
        private struct Chunk
        {
            public string type;
            public byte[] data;
        }

        public static bool IsPng(byte[] png)
        {
            return HasSignature(png);
        }

        private static bool HasSignature(byte[] png)
        {
            if (png == null || png.Length < PngSignature.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (png[i] != PngSignature[i]) return false;
            }
            return true;
        }

        private static int ReadBE32(byte[] b, int i)
        {
            return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
        }

        private static void WriteBE32(byte[] b, int i, uint v)
        {
            b[i] = (byte)(v >> 24);
            b[i + 1] = (byte)(v >> 16);
            b[i + 2] = (byte)(v >> 8);
            b[i + 3] = (byte)v;
        }

        private static void WriteBE32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static List<Chunk> ReadChunks(byte[] png)
        {
            var chunks = new List<Chunk>();
            if (!HasSignature(png)) throw new InvalidDataException("Not a PNG file.");
            int pos = PngSignature.Length;
            while (pos + 8 <= png.Length)
            {
                int len = ReadBE32(png, pos);
                if (len < 0 || pos + 12 + len > png.Length)
                    throw new InvalidDataException("Corrupt PNG chunk.");
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                var data = new byte[len];
                Array.Copy(png, pos + 8, data, 0, len);
                chunks.Add(new Chunk { type = type, data = data });
                pos += 12 + len;
                if (type == "IEND") break;
            }
            return chunks;
        }

        private static byte[] BuildPng(List<Chunk> chunks)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(PngSignature, 0, PngSignature.Length);
                foreach (var c in chunks)
                {
                    WriteBE32(ms, (uint)c.data.Length);
                    byte[] typeBytes = Encoding.ASCII.GetBytes(c.type);
                    ms.Write(typeBytes, 0, 4);
                    ms.Write(c.data, 0, c.data.Length);
                    var crcBuf = new byte[4 + c.data.Length];
                    Array.Copy(typeBytes, crcBuf, 4);
                    Array.Copy(c.data, 0, crcBuf, 4, c.data.Length);
                    WriteBE32(ms, Crc32(crcBuf, 0, crcBuf.Length));
                }
                return ms.ToArray();
            }
        }

        /// <summary>Insert (or replace) the ModelCard chunk before IEND.</summary>
        public static byte[] EmbedChunk(byte[] png, byte[] cardData)
        {
            List<Chunk> chunks = ReadChunks(png);
            chunks.RemoveAll(c => c.type == CardChunkType);
            var card = new Chunk { type = CardChunkType, data = cardData };
            int iend = chunks.FindIndex(c => c.type == "IEND");
            if (iend < 0) iend = chunks.Count;
            chunks.Insert(iend, card);
            return BuildPng(chunks);
        }

        /// <summary>Read the ModelCard chunk, or null if the image has none.</summary>
        public static byte[] ExtractChunk(byte[] png)
        {
            if (!HasSignature(png)) return null;
            List<Chunk> chunks;
            try { chunks = ReadChunks(png); }
            catch { return null; }
            foreach (var c in chunks)
            {
                if (c.type == CardChunkType) return c.data;
            }
            return null;
        }

        /// <summary>Remove the ModelCard chunk, returning a clean PNG.</summary>
        public static byte[] StripChunk(byte[] png)
        {
            List<Chunk> chunks = ReadChunks(png);
            chunks.RemoveAll(c => c.type == CardChunkType);
            return BuildPng(chunks);
        }

        // ------------------------------------------------------- deflate helpers
        public static byte[] Compress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        public static byte[] Decompress(byte[] data)
        {
            return Decompress(data, 0, data.Length);
        }

        public static byte[] Decompress(byte[] data, int offset, int count)
        {
            using (var input = new MemoryStream(data, offset, count))
            using (var ds = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                ds.CopyTo(output);
                return output.ToArray();
            }
        }

        // ---------------------------------------------- raw RGBA -> PNG writer
        /// <summary>Encode raw RGBA bytes (width*height*4) into a PNG file.</summary>
        public static byte[] CreatePngFromRgba(byte[] rgba, int width, int height)
        {
            var chunks = new List<Chunk>();
            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, (uint)width);
            WriteBE32(ihdr, 4, (uint)height);
            ihdr[8] = 8;             // bit depth
            ihdr[9] = ColorTypeRgba; // color type: RGBA
            chunks.Add(new Chunk { type = "IHDR", data = ihdr });

            int rowBytes = 1 + width * 4;
            var raw = new byte[height * rowBytes];
            int p = 0;
            for (int y = 0; y < height; y++)
            {
                raw[p++] = 0; // filter: none
                Array.Copy(rgba, y * width * 4, raw, p, width * 4);
                p += width * 4;
            }

            byte[] deflated = Compress(raw);
            var idat = new byte[2 + deflated.Length + 4];
            idat[0] = 0x78;
            idat[1] = 0x9C; // zlib header, default compression
            Array.Copy(deflated, 0, idat, 2, deflated.Length);
            WriteBE32(idat, 2 + deflated.Length, Adler32(raw));
            chunks.Add(new Chunk { type = "IDAT", data = idat });
            chunks.Add(new Chunk { type = "IEND", data = new byte[0] });
            return BuildPng(chunks);
        }

        /// <summary>Generate a simple gradient PNG that can be used as card art.</summary>
        public static byte[] CreatePlaceholderPng(int width, int height)
        {
            var rgba = new byte[width * height * 4];
            int p = 0;
            for (int y = 0; y < height; y++)
            {
                float t = height <= 1 ? 0f : (float)y / (height - 1);
                byte cr = (byte)(30 + (int)(120 * t));
                byte cg = (byte)(60 + (int)(90 * (1f - t)));
                byte cb = (byte)(150 + (int)(60 * t));
                for (int x = 0; x < width; x++)
                {
                    rgba[p++] = cr;
                    rgba[p++] = cg;
                    rgba[p++] = cb;
                    rgba[p++] = 255;
                }
            }
            return CreatePngFromRgba(rgba, width, height);
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        // -------------------------------------------- pixel (LSB) steganography
        /// <summary>
        /// Embed payload bytes into the least-significant bits of RGBA pixels
        /// (1 bit per channel, 4 bits per pixel, MSB-first per byte).
        /// Returns false (with an error message) when the payload does not fit.
        /// </summary>
        public static bool TryEmbedPixels(Color32[] pixels, byte[] payload, out string error)
        {
            error = null;
            long totalBits = (long)pixels.Length * 4L;
            long neededBits = (long)payload.Length * 8L + 8L * 8L + 32L; // magic + length + payload
            if (neededBits > totalBits)
            {
                error = string.Format(
                    "Payload is {0} bytes but this image can only hold about {1} bytes in its pixels. Use a larger image.",
                    payload.Length, totalBits / 8 - 12);
                return false;
            }

            int bitIndex = 0;
            WriteBits(pixels, ref bitIndex, Encoding.ASCII.GetBytes(ModelCardData.Magic));
            var lenBytes = new byte[4];
            WriteBE32(lenBytes, 0, (uint)payload.Length);
            WriteBits(pixels, ref bitIndex, lenBytes);
            WriteBits(pixels, ref bitIndex, payload);
            return true;
        }

        /// <summary>Extract a payload previously embedded with TryEmbedPixels, or null.</summary>
        public static byte[] ExtractPixels(Color32[] pixels)
        {
            int bitIndex = 0;
            byte[] magic = ReadBits(pixels, ref bitIndex, 8);
            if (magic == null) return null;
            if (!StartsWith(magic, Encoding.ASCII.GetBytes(ModelCardData.Magic))) return null;
            byte[] lenBytes = ReadBits(pixels, ref bitIndex, 4);
            if (lenBytes == null) return null;
            int len = ReadBE32(lenBytes, 0);
            long capacity = (long)pixels.Length * 4L / 8L;
            if (len <= 0 || len > capacity) return null;
            return ReadBits(pixels, ref bitIndex, len);
        }

        private static void WriteBits(Color32[] pixels, ref int bitIndex, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    SetBit(pixels, bitIndex++, ((b >> bit) & 1) != 0);
                }
            }
        }

        private static byte[] ReadBits(Color32[] pixels, ref int bitIndex, int byteCount)
        {
            long needed = (long)byteCount * 8L;
            if (bitIndex + needed > (long)pixels.Length * 4L) return null;
            var result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                byte b = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    if (GetBit(pixels, bitIndex++)) b |= (byte)(1 << bit);
                }
                result[i] = b;
            }
            return result;
        }

        private static void SetBit(Color32[] pixels, int bitIndex, bool value)
        {
            int channel = bitIndex;
            int px = channel >> 2;  // /4
            int ch = channel & 3;   // %4 (0=r, 1=g, 2=b, 3=a)
            Color32 c = pixels[px];
            byte v = c.r;
            if (ch == 1) v = c.g;
            else if (ch == 2) v = c.b;
            else if (ch == 3) v = c.a;
            v = value ? (byte)(v | 1) : (byte)(v & 0xFE);
            if (ch == 0) c.r = v;
            else if (ch == 1) c.g = v;
            else if (ch == 2) c.b = v;
            else c.a = v;
            pixels[px] = c;
        }

        private static bool GetBit(Color32[] pixels, int bitIndex)
        {
            int channel = bitIndex;
            int px = channel >> 2;
            int ch = channel & 3;
            Color32 c = pixels[px];
            byte v = c.r;
            if (ch == 1) v = c.g;
            else if (ch == 2) v = c.b;
            else if (ch == 3) v = c.a;
            return (v & 1) != 0;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i]) return false;
            }
            return true;
        }
    }
}
