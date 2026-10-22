using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AnyNes.Core;

namespace AnyNes.Builder
{
    /// <summary>Runs a game headless and grabs frames to pick an icon from (usually the title screen).</summary>
    static class Snapshots
    {
        public sealed class Result
        {
            public readonly List<Bitmap> Frames = new List<Bitmap>();
            public int Best;
        }

        public static Result Capture(RomImage rom, int seconds, Func<bool> cancelled)
        {
            var res = new Result();
            var nes = new Nes(rom);
            int bestScore = -1;
            int[] earlier = null;
            for (int frame = 1; frame <= seconds * 60; frame++)
            {
                if (cancelled != null && cancelled()) break;
                nes.RunFrame();
                // Remember the picture half a second before each capture to judge whether it is static.
                if (frame % 60 == 30) earlier = (int[])nes.Ppu.Frame.Clone();
                if (frame % 60 != 0) continue;
                var bmp = ToBitmap(nes.Ppu.Frame);
                int score = Score(nes.Ppu.Frame);
                // Title screens sit still; intros and attract-mode demos scroll.
                if (score > 0 && earlier != null && Difference(earlier, nes.Ppu.Frame) < 0.03) score += 120;
                // Prefer earlier frames on ties: title screens show up before attract-mode demos.
                if (score > bestScore * 1.15) { bestScore = score; res.Best = res.Frames.Count; }
                res.Frames.Add(bmp);
            }
            return res;
        }

        static double Difference(int[] a, int[] b)
        {
            int diff = 0, n = 0;
            for (int i = 0; i < a.Length; i += 7, n++) if (a[i] != b[i]) diff++;
            return diff / (double)n;
        }

        /// <summary>Rough "interestingness": distinct colours and how much isn't the backdrop colour.</summary>
        static int Score(int[] frame)
        {
            var colors = new HashSet<int>();
            var counts = new Dictionary<int, int>();
            for (int y = 16; y < 224; y += 2)
                for (int x = 0; x < 256; x += 2)
                {
                    int c = frame[y * 256 + x];
                    colors.Add(c);
                    int n;
                    counts.TryGetValue(c, out n);
                    counts[c] = n + 1;
                }
            int max = 0;
            foreach (var kv in counts) max = Math.Max(max, kv.Value);
            int total = 104 * 128;
            int coverage = (total - max) * 100 / total;
            return colors.Count <= 1 ? 0 : coverage * 4 + Math.Min(colors.Count, 16) * 10;
        }

        public static Bitmap ToBitmap(int[] frame)
        {
            var bmp = new Bitmap(256, 240, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, 256, 240), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(frame, 0, data.Scan0, frame.Length);
            bmp.UnlockBits(data);
            return bmp;
        }
    }
}
