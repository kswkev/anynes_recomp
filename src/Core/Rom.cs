using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AnyNes.Core
{
    public enum Mirroring { Horizontal, Vertical, SingleLow, SingleHigh, FourScreen }

    /// <summary>Console timing: NTSC (2A03/2C02, 60 Hz) or PAL (2A07/2C07, 50 Hz).</summary>
    public enum Region { Ntsc, Pal }

    [Serializable]
    public sealed class RomImage
    {
        public byte[] Prg;
        public byte[] Chr;          // empty when the board uses CHR RAM
        public int Mapper;
        public int Submapper;
        public Mirroring Mirroring;
        public bool Battery;
        public byte[] Trainer;
        public int PrgRamSize;      // volatile + battery-backed, in bytes
        public int ChrRamSize;
        public uint Crc32;          // CRC of PRG+CHR (header independent)
        public bool IsNes2;
        /// <summary>True when the header itself declares PAL timing.</summary>
        public bool HeaderSaysPal;
        /// <summary>Timing the console is built with; set before constructing <see cref="Nes"/>.</summary>
        public Region Region;

        public static RomImage Parse(byte[] data)
        {
            if (data == null || data.Length < 16 || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A)
                throw new InvalidDataException("Not an iNES (.nes) ROM image.");

            var r = new RomImage();
            byte f6 = data[6], f7 = data[7];
            r.IsNes2 = (f7 & 0x0C) == 0x08;
            int prgUnits = data[4], chrUnits = data[5];
            r.Mapper = (f6 >> 4) | (f7 & 0xF0);
            if (r.IsNes2)
            {
                r.Mapper |= (data[8] & 0x0F) << 8;
                r.Submapper = data[8] >> 4;
                prgUnits |= (data[9] & 0x0F) << 8;
                chrUnits |= (data[9] & 0xF0) << 4;
            }
            else if ((f7 & 0x0C) != 0 && data[12] + data[13] + data[14] + data[15] != 0)
            {
                // "DiskDude!" style garbage in bytes 7-15: ignore the upper mapper nibble.
                r.Mapper = f6 >> 4;
            }

            r.Battery = (f6 & 0x02) != 0;
            if ((f6 & 0x08) != 0) r.Mirroring = Mirroring.FourScreen;
            else r.Mirroring = (f6 & 0x01) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;

            int pos = 16;
            if ((f6 & 0x04) != 0)
            {
                r.Trainer = new byte[512];
                Array.Copy(data, pos, r.Trainer, 0, Math.Min(512, data.Length - pos));
                pos += 512;
            }

            int prgSize = prgUnits * 0x4000;
            int chrSize = chrUnits * 0x2000;
            if (prgSize == 0 || pos + prgSize > data.Length)
            {
                // Truncated/oversized header: take what is there.
                prgSize = Math.Max(0x4000, Math.Min(prgSize, data.Length - pos) & ~0x1FFF);
            }
            r.Prg = new byte[prgSize];
            Array.Copy(data, pos, r.Prg, 0, Math.Min(prgSize, data.Length - pos));
            pos += prgSize;

            int chrAvail = Math.Max(0, Math.Min(chrSize, data.Length - pos));
            r.Chr = new byte[chrAvail & ~0x3FF];
            Array.Copy(data, pos, r.Chr, 0, r.Chr.Length);

            if (r.IsNes2)
            {
                int vol = (data[10] & 0x0F) == 0 ? 0 : 64 << (data[10] & 0x0F);
                int nv = (data[10] >> 4) == 0 ? 0 : 64 << (data[10] >> 4);
                r.PrgRamSize = vol + nv;
                int cv = (data[11] & 0x0F) == 0 ? 0 : 64 << (data[11] & 0x0F);
                int cn = (data[11] >> 4) == 0 ? 0 : 64 << (data[11] >> 4);
                r.ChrRamSize = cv + cn;
            }
            else
            {
                r.PrgRamSize = (data[8] == 0 ? 1 : data[8]) * 0x2000;
            }

            // NES 2.0: byte 12 bits 0-1 (1 = PAL). iNES 1.0: byte 9 bit 0, trusted only in clean headers.
            if (r.IsNes2) r.HeaderSaysPal = (data[12] & 3) == 1;
            else r.HeaderSaysPal = (data[9] & 1) != 0 && data[11] == 0 && data[12] == 0 && data[13] == 0 && data[14] == 0 && data[15] == 0;
            r.Region = r.HeaderSaysPal ? Region.Pal : Region.Ntsc;
            if (r.Chr.Length == 0 && r.ChrRamSize == 0) r.ChrRamSize = 0x2000;

            uint crc = Crc.Compute(r.Prg, 0, r.Prg.Length, 0);
            r.Crc32 = Crc.Compute(r.Chr, 0, r.Chr.Length, crc);
            return r;
        }
    }

    public static class Crc
    {
        static uint[] table;

        public static uint Compute(byte[] data, int offset, int count, uint seed)
        {
            if (table == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                table = t;
            }
            uint crc = ~seed;
            for (int i = offset; i < offset + count; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }

    public static class RomLoader
    {
        /// <summary>Loads a .nes file, or the first .nes inside a .zip. Returns the raw iNES bytes.</summary>
        public static byte[] Load(string path, out string romName)
        {
            romName = Path.GetFileNameWithoutExtension(path);
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K')
            {
                using (var ms = new MemoryStream(bytes))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry best = null;
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith(".nes", StringComparison.OrdinalIgnoreCase)) { best = e; break; }
                    }
                    if (best == null)
                    {
                        // Fall back to any entry with an iNES header.
                        foreach (var e in zip.Entries)
                        {
                            if (e.Length < 16) continue;
                            using (var s = e.Open())
                            {
                                var h = new byte[4];
                                if (s.Read(h, 0, 4) == 4 && h[0] == 'N' && h[1] == 'E' && h[2] == 'S' && h[3] == 0x1A) { best = e; break; }
                            }
                        }
                    }
                    if (best == null) throw new InvalidDataException("The zip archive does not contain a .nes ROM.");
                    romName = Path.GetFileNameWithoutExtension(best.Name);
                    using (var s = best.Open())
                    using (var outMs = new MemoryStream())
                    {
                        s.CopyTo(outMs);
                        return outMs.ToArray();
                    }
                }
            }
            return bytes;
        }

        static readonly string[] PalTags =
        {
            "europe", "australia", "pal", "e", "germany", "france", "spain", "italy", "netherlands",
            "sweden", "scandinavia", "uk", "united kingdom", "a", "g", "f", "s", "i"
        };
        static readonly string[] NtscTags = { "usa", "u", "japan", "j", "world", "canada", "brazil", "korea", "asia", "jue", "ju", "ue" };

        /// <summary>
        /// Best region for a ROM: a PAL header wins; otherwise No-Intro/GoodNES region tags in the file
        /// name. Multi-region dumps such as "(USA, Europe)" or "(World)" are NTSC releases.
        /// </summary>
        public static Region DetectRegion(RomImage rom, string romName)
        {
            if (rom.HeaderSaysPal) return Region.Pal;
            bool pal = false, ntsc = false;
            string lower = (romName ?? "").ToLowerInvariant();
            int i = 0;
            while ((i = lower.IndexOfAny(new[] { '(', '[' }, i)) >= 0)
            {
                int end = lower.IndexOfAny(new[] { ')', ']' }, i);
                if (end < 0) break;
                foreach (var raw in lower.Substring(i + 1, end - i - 1).Split(','))
                {
                    string tag = raw.Trim();
                    if (Array.IndexOf(NtscTags, tag) >= 0) ntsc = true;
                    else if (Array.IndexOf(PalTags, tag) >= 0) pal = true;
                }
                i = end;
            }
            return pal && !ntsc ? Region.Pal : Region.Ntsc;
        }

        /// <summary>Turns "Super Mario Bros. (World) (Rev A)" into "Super Mario Bros.".</summary>
        public static string CleanTitle(string name)
        {
            var sb = new StringBuilder();
            int depth = 0;
            foreach (char c in name)
            {
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(c);
            }
            string s = sb.ToString().Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            // "Legend of Zelda, The" -> "The Legend of Zelda"
            if (s.EndsWith(", The")) s = "The " + s.Substring(0, s.Length - 5);
            else
            {
                int i = s.IndexOf(", The ");
                if (i > 0) s = "The " + s.Substring(0, i) + s.Substring(i + 5);
            }
            return s.Length == 0 ? name : s;
        }
    }
}
