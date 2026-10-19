using System.Collections.Generic;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Background subtraction for fit-from-image: downscale the photo, flood-fill
    /// the background from the four corners with a color tolerance, and produce a
    /// foreground mask plus its bounding box.
    /// </summary>
    public static class SilhouetteMask
    {
        public struct Result
        {
            public bool[] mask; // width*height, true = foreground
            public int width, height;
            public int minX, minY, maxX, maxY; // foreground bbox (inclusive)
            public bool hasForeground;
        }

        /// <summary>
        /// Extract the foreground mask from a photo. tolerance: 0..1 (higher =
        /// more aggressive background removal). The mask is computed at a reduced
        /// working size (max edge = workWidth).
        /// </summary>
        public static Result Extract(Texture2D source, int workWidth, float tolerance)
        {
            var result = new Result { minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1 };
            int srcW = source.width, srcH = source.height;
            if (srcW == 0 || srcH == 0) return result;

            int scale = Mathf.Max(1, Mathf.Max(srcW, srcH) / Mathf.Max(workWidth, 8));
            int w = srcW / scale, h = srcH / scale;
            if (w < 8 || h < 8) { w = srcW; h = srcH; scale = 1; }

            // downsample by averaging blocks
            var colors = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    long r = 0, g = 0, b = 0;
                    int n = 0;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int sy = y * scale + dy;
                        if (sy >= srcH) continue;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int sx = x * scale + dx;
                            if (sx >= srcW) continue;
                            Color32 c = source.GetPixel(sx, sy);
                            r += c.r; g += c.g; b += c.b;
                            n++;
                        }
                    }
                    if (n > 0) colors[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }

            // flood fill from all four corners, comparing against the top-left color
            Color32 reference = colors[0];
            float thresholdSq = (tolerance * 600f) * (tolerance * 600f);
            var bg = new bool[w * h];
            var queue = new Queue<int>();
            for (int i = 0; i < 4; i++)
            {
                int cx = (i == 1 || i == 3) ? w - 1 : 0;
                int cy = (i == 2 || i == 3) ? h - 1 : 0;
                queue.Enqueue(cy * w + cx);
            }
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                if (bg[idx]) continue;
                bg[idx] = true;
                int x = idx % w, y = idx / w;
                TryPush(queue, bg, colors, reference, thresholdSq, x - 1, y, w, h);
                TryPush(queue, bg, colors, reference, thresholdSq, x + 1, y, w, h);
                TryPush(queue, bg, colors, reference, thresholdSq, x, y - 1, w, h);
                TryPush(queue, bg, colors, reference, thresholdSq, x, y + 1, w, h);
            }

            result.mask = new bool[w * h];
            result.width = w;
            result.height = h;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool fg = !bg[y * w + x];
                    result.mask[y * w + x] = fg;
                    if (fg)
                    {
                        if (x < result.minX) result.minX = x;
                        if (x > result.maxX) result.maxX = x;
                        if (y < result.minY) result.minY = y;
                        if (y > result.maxY) result.maxY = y;
                    }
                }
            }
            result.hasForeground = result.maxX >= result.minX && result.maxY >= result.minY;
            return result;
        }

        private static void TryPush(Queue<int> queue, bool[] bg, Color32[] colors, Color32 reference, float thresholdSq,
            int x, int y, int w, int h)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int idx = y * w + x;
            if (bg[idx]) return;
            Color32 c = colors[idx];
            float dr = c.r - reference.r, dg = c.g - reference.g, db = c.b - reference.b;
            if (dr * dr + dg * dg + db * db < thresholdSq) queue.Enqueue(idx);
        }
    }
}
