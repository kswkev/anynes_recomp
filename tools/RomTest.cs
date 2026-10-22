using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AnyNes.Core;
using Region = AnyNes.Core.Region;

// Headless regression runner: RomTest <rom dir|file> <out dir> [frames] [filter]
// Runs each ROM with a scripted Start-button press pattern, saves a screenshot and a contact sheet.
static class RomTest
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: RomTest <rom dir|file> <out dir> [frames] [name filter]");
            return 1;
        }
        string src = args[0], outDir = args[1];
        int frames = args.Length > 2 ? int.Parse(args[2]) : 1800;
        string filter = args.Length > 3 ? args[3] : null;
        Directory.CreateDirectory(outDir);

        var files = File.Exists(src) ? new[] { src } : Directory.GetFiles(src).OrderBy(f => f).ToArray();
        if (filter != null) files = files.Where(f => Path.GetFileName(f).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

        var shots = new List<KeyValuePair<string, Bitmap>>();
        var log = new StreamWriter(Path.Combine(outDir, "results.csv"));
        log.WriteLine("file,mapper,status,ms,fps,jammed");
        int ok = 0, fail = 0;
        foreach (var f in files)
        {
            string name = Path.GetFileNameWithoutExtension(f);
            string status = "ok";
            int mapper = -1;
            long ms = 0;
            bool jammed = false;
            Region region = Region.Ntsc;
            Bitmap shot = null;
            try
            {
                string inner;
                var rom = RomImage.Parse(RomLoader.Load(f, out inner));
                rom.Region = RomLoader.DetectRegion(rom, inner);
                if (Environment.GetEnvironmentVariable("ROMTEST_REGION") == "ntsc") rom.Region = Region.Ntsc;
                if (Environment.GetEnvironmentVariable("ROMTEST_REGION") == "pal") rom.Region = Region.Pal;
                mapper = rom.Mapper;
                region = rom.Region;
                var nes = new Nes(rom);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    int phase = i % 240;
                    nes.Input[0] = (i > 100 && phase < 6) ? Buttons.Start : (i > 100 && phase >= 120 && phase < 126 ? Buttons.A : Buttons.None);
                    nes.RunFrame();
                }
                ms = sw.ElapsedMilliseconds;
                jammed = nes.Cpu.Jammed;
                shot = ToBitmap(nes.Ppu.Frame);
                shot.Save(Path.Combine(outDir, Sanitize(name) + ".png"), ImageFormat.Png);
                ok++;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ": " + ex.Message.Replace(',', ';').Replace('\n', ' ');
                fail++;
            }
            double fps = ms > 0 ? frames * 1000.0 / ms : 0;
            Console.WriteLine("{0,-60} m{1,-4} {2} {3:F0}fps{4}{5}", name.Length > 60 ? name.Substring(0, 60) : name, mapper, status, fps,
                jammed ? " JAM" : "", region == Region.Pal ? " PAL" : "");
            log.WriteLine("\"{0}\",{1},\"{2}\",{3},{4:F0},{5}", name, mapper, status, ms, fps, jammed);
            log.Flush();
            if (shot != null) shots.Add(new KeyValuePair<string, Bitmap>(name, shot));
        }
        log.Close();

        // Contact sheets of 48 thumbnails each.
        const int cols = 8, rows = 6, tw = 256, th = 240 + 14;
        for (int sheet = 0; sheet * cols * rows < shots.Count; sheet++)
        {
            using (var bmp = new Bitmap(cols * tw, rows * th))
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 8))
            {
                g.Clear(Color.DimGray);
                for (int k = 0; k < cols * rows; k++)
                {
                    int idx = sheet * cols * rows + k;
                    if (idx >= shots.Count) break;
                    int x = (k % cols) * tw, y = (k / cols) * th;
                    g.DrawImage(shots[idx].Value, x, y + 14, 256, 240);
                    g.DrawString(shots[idx].Key, font, Brushes.White, x + 2, y);
                }
                bmp.Save(Path.Combine(outDir, "_sheet" + sheet.ToString("00") + ".png"), ImageFormat.Png);
            }
        }
        Console.WriteLine("done: {0} ok, {1} failed", ok, fail);
        return 0;
    }

    static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    static Bitmap ToBitmap(int[] frame)
    {
        var bmp = new Bitmap(256, 240, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, 256, 240), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(frame, 0, data.Scan0, frame.Length);
        bmp.UnlockBits(data);
        return bmp;
    }
}
