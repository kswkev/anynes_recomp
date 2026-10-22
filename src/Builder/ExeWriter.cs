using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AnyNes.Core;

namespace AnyNes.Builder
{
    /// <summary>
    /// Produces a standalone game exe: a copy of the embedded player stub with the ROM, an icon and
    /// version info written into its Win32 resources.
    /// </summary>
    static class ExeWriter
    {
        const int RT_ICON = 3, RT_RCDATA = 10, RT_GROUP_ICON = 14, RT_VERSION = 16;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UpdateResource(IntPtr h, IntPtr type, string name, ushort lang, byte[] data, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(IntPtr h, bool discard);

        public static byte[] PlayerStub()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnyNes.PlayerStub.exe"))
            {
                if (s == null) throw new InvalidOperationException("Player stub resource missing from the builder.");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static void Build(GamePackage pkg, Bitmap iconSource, string outPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(outPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            File.WriteAllBytes(tmp, PlayerStub());
            try
            {
                IntPtr h = BeginUpdateResource(tmp, false);
                if (h == IntPtr.Zero) throw new Win32Exception();
                bool ok = false;
                try
                {
                    byte[] data = pkg.Serialize();
                    Check(UpdateResource(h, (IntPtr)RT_RCDATA, GamePackage.ResourceName, 0, data, data.Length));

                    if (iconSource != null)
                    {
                        var images = IconImages(iconSource);
                        for (int i = 0; i < images.Count; i++)
                            Check(UpdateResource(h, (IntPtr)RT_ICON, (IntPtr)(i + 1), 0, images[i].Data, images[i].Data.Length));
                        byte[] group = GroupIcon(images);
                        Check(UpdateResource(h, (IntPtr)RT_GROUP_ICON, (IntPtr)1, 0, group, group.Length));
                    }

                    byte[] ver = VersionInfo(pkg.Title);
                    Check(UpdateResource(h, (IntPtr)RT_VERSION, (IntPtr)1, 0, ver, ver.Length));
                    ok = true;
                }
                finally
                {
                    if (!EndUpdateResource(h, !ok) && ok) throw new Win32Exception();
                }

                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        static void Check(bool ok) { if (!ok) throw new Win32Exception(); }

        // ---------------------------------------------------------------- icons

        sealed class IconImage
        {
            public int Size;
            public byte[] Data;
        }

        /// <summary>Square-crops the screenshot and renders 256 (PNG), 64, 48, 32 and 16 px icon images.</summary>
        static List<IconImage> IconImages(Bitmap src)
        {
            var list = new List<IconImage>();
            foreach (int size in new[] { 256, 64, 48, 32, 16 })
            {
                using (var bmp = RenderIcon(src, size))
                {
                    var img = new IconImage { Size = size };
                    if (size >= 256)
                    {
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            img.Data = ms.ToArray();
                        }
                    }
                    else img.Data = DibIcon(bmp);
                    list.Add(img);
                }
            }
            return list;
        }

        public static Bitmap RenderIcon(Bitmap src, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = size >= 128 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Centre square of the 256x240 frame, drawn into a rounded tile.
                int crop = Math.Min(src.Width, src.Height);
                var srcRect = new Rectangle((src.Width - crop) / 2, (src.Height - crop) / 2, crop, crop);
                float pad = size >= 32 ? size / 32f : 0;
                var dst = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                float radius = size / 8f;
                using (var path = RoundedRect(dst, radius))
                {
                    g.SetClip(path);
                    g.DrawImage(src, dst, srcRect, GraphicsUnit.Pixel);
                    g.ResetClip();
                    if (size >= 32)
                        using (var pen = new Pen(Color.FromArgb(160, 0, 0, 0), Math.Max(1f, size / 64f)))
                            g.DrawPath(pen, path);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Classic icon image: BITMAPINFOHEADER + bottom-up 32bpp BGRA + 1bpp AND mask.</summary>
        static byte[] DibIcon(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(n); w.Write(n * 2);
                w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(n * n * 4 + maskStride * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                for (int y = n - 1; y >= 0; y--)
                {
                    var row = new byte[maskStride];
                    for (int x = 0; x < n; x++)
                        if (bmp.GetPixel(x, y).A == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    w.Write(row);
                }
                return ms.ToArray();
            }
        }

        static byte[] GroupIcon(List<IconImage> images)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
                for (int i = 0; i < images.Count; i++)
                {
                    int s = images[i].Size;
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Data.Length);
                    w.Write((short)(i + 1));
                }
                return ms.ToArray();
            }
        }

        /// <summary>Writes a standard .ico file (used for previews / "save icon").</summary>
        public static void SaveIco(Bitmap src, string path)
        {
            var images = IconImages(src);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
                int offset = 6 + 16 * images.Count;
                foreach (var img in images)
                {
                    int s = img.Size;
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(img.Data.Length); w.Write(offset);
                    offset += img.Data.Length;
                }
                foreach (var img in images) w.Write(img.Data);
            }
        }

        // ---------------------------------------------------------------- version info

        static byte[] VersionInfo(string title)
        {
            byte[] fixedInfo;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(unchecked((int)0xFEEF04BD));
                w.Write(0x00010000);
                w.Write(0x00010000); w.Write(0);    // file version 1.0.0.0
                w.Write(0x00010000); w.Write(0);    // product version 1.0.0.0
                w.Write(0x3F); w.Write(0);
                w.Write(0x40004); w.Write(1); w.Write(0);
                w.Write(0); w.Write(0);
                fixedInfo = ms.ToArray();
            }

            var strings = new List<byte[]>();
            foreach (var kv in new[]
            {
                new KeyValuePair<string, string>("FileDescription", title),
                new KeyValuePair<string, string>("ProductName", title),
                new KeyValuePair<string, string>("InternalName", title),
                new KeyValuePair<string, string>("FileVersion", "1.0.0.0"),
                new KeyValuePair<string, string>("ProductVersion", "1.0.0.0"),
                new KeyValuePair<string, string>("Comments", "Built with AnyNES"),
            })
            {
                byte[] val = Encoding.Unicode.GetBytes(kv.Value + "\0");
                strings.Add(Block(kv.Key, val, val.Length / 2, 1));
            }
            byte[] table = Block("040904B0", new byte[0], 0, 1, strings.ToArray());
            byte[] sfi = Block("StringFileInfo", new byte[0], 0, 1, table);
            byte[] var = Block("Translation", BitConverter.GetBytes(0x04B00409), 4, 0);
            byte[] vfi = Block("VarFileInfo", new byte[0], 0, 1, var);
            return Block("VS_VERSION_INFO", fixedInfo, fixedInfo.Length, 0, sfi, vfi);
        }

        static byte[] Block(string key, byte[] value, int valueLength, int type, params byte[][] children)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0);
                w.Write((short)valueLength);
                w.Write((short)type);
                w.Write(Encoding.Unicode.GetBytes(key + "\0"));
                Pad(w);
                w.Write(value);
                foreach (var c in children)
                {
                    Pad(w);
                    w.Write(c);
                }
                w.Flush();
                byte[] r = ms.ToArray();
                r[0] = (byte)r.Length;
                r[1] = (byte)(r.Length >> 8);
                return r;
            }
        }

        static void Pad(BinaryWriter w)
        {
            while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
        }
    }
}
