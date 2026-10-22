using System;

namespace AnyNes.Core
{
    /// <summary>Dot-based 2C02. Performs every background/sprite memory fetch at its real PPU cycle.</summary>
    [Serializable]
    public sealed class Ppu
    {
        public const int Width = 256, Height = 240;

        public readonly int[] Frame = new int[Width * Height];   // 0xAARRGGBB
        public int Scanline, Dot;
        public long Clock, FrameCount;
        public bool FrameReady;
        public int FetchKind;   // 0 = CPU/idle, 1 = background, 2 = sprite (used by MMC5)

        readonly Nes nes;
        int ctrl, mask, status;
        int oamAddr;
        readonly byte[] oam = new byte[256];
        readonly byte[] pal = new byte[32];
        int v, t, fineX;
        bool w, odd, suppressVbl;
        int readBuf, openBus;

        int ntLatch, atLatch, loLatch, hiLatch;
        int bgLo, bgHi, atLo, atHi;

        readonly int[] spX = new int[8], spAttr = new int[8], spLo = new int[8], spHi = new int[8];
        readonly int[] spIdx = new int[8], spRow = new int[8];
        int spCount;
        bool sp0OnLine;

        static readonly byte[] Reverse = BuildReverse();

        public Ppu(Nes nes) { this.nes = nes; }

        public bool NmiLine { get { return (ctrl & 0x80) != 0 && (status & 0x80) != 0; } }
        public bool RenderingEnabled { get { return (mask & 0x18) != 0; } }
        public bool TallSprites { get { return (ctrl & 0x20) != 0; } }

        public void Reset()
        {
            ctrl = mask = 0; w = false; readBuf = 0;
            Scanline = 0; Dot = 0; odd = false;
        }

        static byte[] BuildReverse()
        {
            var r = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int x = 0;
                for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) x |= 0x80 >> b;
                r[i] = (byte)x;
            }
            return r;
        }

        // ---------------------------------------------------------------- PPU bus

        int Read(int a)
        {
            a &= 0x3FFF;
            Mapper m = nes.Mapper;
            if (m.WantsA12) m.PpuAddress(a);
            if (a < 0x2000) return m.ReadChr(a);
            if (a < 0x3F00) return m.ReadNt(0x2000 | (a & 0x0FFF));
            return pal[PalIndex(a)];
        }

        void Write(int a, int val)
        {
            a &= 0x3FFF;
            Mapper m = nes.Mapper;
            if (m.WantsA12) m.PpuAddress(a);
            if (a < 0x2000) m.WriteChr(a, (byte)val);
            else if (a < 0x3F00) m.WriteNt(0x2000 | (a & 0x0FFF), (byte)val);
            else pal[PalIndex(a)] = (byte)(val & 0x3F);
        }

        static int PalIndex(int a)
        {
            a &= 0x1F;
            if ((a & 0x13) == 0x10) a &= 0x0F;
            return a;
        }

        // ---------------------------------------------------------------- CPU interface

        public int ReadRegister(int reg)
        {
            switch (reg & 7)
            {
                case 2:
                    {
                        int r = (status & 0xE0) | (openBus & 0x1F);
                        if (Scanline == 241 && Dot == 1) suppressVbl = true;
                        status &= 0x7F;
                        w = false;
                        openBus = r;
                        return r;
                    }
                case 4:
                    {
                        int r = oam[oamAddr];
                        if ((oamAddr & 3) == 2) r &= 0xE3;
                        openBus = r;
                        return r;
                    }
                case 7:
                    {
                        int r;
                        int a = v & 0x3FFF;
                        FetchKind = 0;
                        if (a < 0x3F00)
                        {
                            r = readBuf;
                            readBuf = Read(a);
                        }
                        else
                        {
                            r = (pal[PalIndex(a)] & 0x3F) | (openBus & 0xC0);
                            readBuf = Read(a - 0x1000);
                        }
                        v = (v + ((ctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF;
                        if (nes.Mapper.WantsA12) nes.Mapper.PpuAddress(v & 0x3FFF);
                        openBus = r;
                        return r;
                    }
                default:
                    return openBus;
            }
        }

        public void WriteRegister(int reg, int val)
        {
            openBus = val;
            switch (reg & 7)
            {
                case 0:
                    ctrl = val;
                    t = (t & 0x73FF) | ((val & 3) << 10);
                    break;
                case 1: mask = val; break;
                case 3: oamAddr = val; break;
                case 4: oam[oamAddr] = (byte)val; oamAddr = (oamAddr + 1) & 0xFF; break;
                case 5:
                    if (!w) { t = (t & 0x7FE0) | (val >> 3); fineX = val & 7; }
                    else t = (t & 0x0C1F) | ((val & 0xF8) << 2) | ((val & 7) << 12);
                    w = !w;
                    break;
                case 6:
                    if (!w) t = (t & 0x00FF) | ((val & 0x3F) << 8);
                    else
                    {
                        t = (t & 0x7F00) | val;
                        v = t;
                        if (nes.Mapper.WantsA12) nes.Mapper.PpuAddress(v & 0x3FFF);
                    }
                    w = !w;
                    break;
                case 7:
                    FetchKind = 0;
                    Write(v, val);
                    v = (v + ((ctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF;
                    if (nes.Mapper.WantsA12) nes.Mapper.PpuAddress(v & 0x3FFF);
                    break;
            }
        }

        public void WriteOamDirect(int val)
        {
            oam[oamAddr] = (byte)val;
            oamAddr = (oamAddr + 1) & 0xFF;
        }

        // ---------------------------------------------------------------- timing

        public void Step()
        {
            bool rendering = (mask & 0x18) != 0;
            int d = Dot;

            if (Scanline < 240 || Scanline == 261)
            {
                if (Scanline == 261 && d == 1) status &= 0x1F;   // clear vblank, sprite 0, overflow
                if (rendering) RenderCycle(d);
                else if (d == 257) spCount = 0;
                if (Scanline < 240 && d >= 1 && d <= 256) DrawPixel(d - 1, rendering);
                if (d == 1 && Scanline < 240 && rendering && nes.Mapper.WantsScanline) nes.Mapper.OnScanline(Scanline);
            }
            else if (Scanline == 241 && d == 1)
            {
                if (!suppressVbl) status |= 0x80;
                suppressVbl = false;
                FrameReady = true;
                if (nes.Mapper.WantsScanline) nes.Mapper.OnVblank();
            }

            Clock++;
            Dot++;
            if (Scanline == 261 && Dot == 340 && odd && rendering) Dot = 341;
            if (Dot > 340)
            {
                Dot = 0;
                Scanline++;
                if (Scanline > 261)
                {
                    Scanline = 0;
                    odd = !odd;
                    FrameCount++;
                }
            }
        }

        void RenderCycle(int d)
        {
            if ((d >= 2 && d <= 257) || (d >= 322 && d <= 337))
            {
                bgLo <<= 1; bgHi <<= 1; atLo <<= 1; atHi <<= 1;
            }
            if ((d >= 9 && d <= 257 && (d & 7) == 1) || d == 329 || d == 337)
            {
                bgLo = (bgLo & 0xFF00) | loLatch;
                bgHi = (bgHi & 0xFF00) | hiLatch;
                atLo = (atLo & 0xFF00) | ((atLatch & 1) != 0 ? 0xFF : 0);
                atHi = (atHi & 0xFF00) | ((atLatch & 2) != 0 ? 0xFF : 0);
            }

            if ((d >= 1 && d <= 256) || (d >= 321 && d <= 336))
            {
                FetchKind = 1;
                switch ((d - 1) & 7)
                {
                    case 0:
                        ntLatch = Read(0x2000 | (v & 0x0FFF));
                        break;
                    case 2:
                        {
                            int at = Read(0x23C0 | (v & 0x0C00) | ((v >> 4) & 0x38) | ((v >> 2) & 0x07));
                            if ((v & 0x40) != 0) at >>= 4;
                            if ((v & 0x02) != 0) at >>= 2;
                            atLatch = at & 3;
                            break;
                        }
                    case 4:
                        loLatch = Read(((ctrl & 0x10) << 8) + ntLatch * 16 + ((v >> 12) & 7));
                        break;
                    case 6:
                        hiLatch = Read(((ctrl & 0x10) << 8) + ntLatch * 16 + ((v >> 12) & 7) + 8);
                        break;
                    case 7:
                        IncX();
                        if (d == 256) IncY();
                        break;
                }
            }
            else if (d == 257)
            {
                v = (v & ~0x041F) | (t & 0x041F);
                if (Scanline < 240) EvaluateSprites(); else spCount = 0;
            }
            else if (d == 337 || d == 339)
            {
                FetchKind = 1;
                Read(0x2000 | (v & 0x0FFF));
            }

            if (Scanline == 261 && d >= 280 && d <= 304)
                v = (v & ~0x7BE0) | (t & 0x7BE0);

            if (d >= 257 && d <= 320) SpriteFetch(d);
        }

        void IncX()
        {
            if ((v & 0x001F) == 31) { v &= ~0x001F; v ^= 0x0400; }
            else v++;
        }

        void IncY()
        {
            if ((v & 0x7000) != 0x7000) { v += 0x1000; return; }
            v &= ~0x7000;
            int y = (v & 0x03E0) >> 5;
            if (y == 29) { y = 0; v ^= 0x0800; }
            else if (y == 31) y = 0;
            else y++;
            v = (v & ~0x03E0) | (y << 5);
        }

        void EvaluateSprites()
        {
            int h = (ctrl & 0x20) != 0 ? 16 : 8;
            int n = 0;
            sp0OnLine = false;
            for (int i = 0; i < 64; i++)
            {
                int row = Scanline - oam[i * 4];
                if (row < 0 || row >= h) continue;
                if (n == 8) { status |= 0x20; break; }
                if (i == 0) sp0OnLine = true;
                spIdx[n] = i;
                spRow[n] = row;
                n++;
            }
            spCount = n;
        }

        void SpriteFetch(int d)
        {
            int slot = (d - 257) >> 3;
            int phase = (d - 257) & 7;
            FetchKind = 2;
            if (phase == 0 || phase == 2)
            {
                Read(0x2000 | (v & 0x0FFF));   // garbage nametable fetches
                return;
            }
            if (phase != 4 && phase != 6) return;

            int tile, attr, row, x;
            if (slot < spCount)
            {
                int o = spIdx[slot] * 4;
                tile = oam[o + 1]; attr = oam[o + 2]; x = oam[o + 3];
                row = spRow[slot];
            }
            else
            {
                tile = 0xFF; attr = 0xFF; x = 0xFF; row = 0;
            }

            int addr;
            if ((ctrl & 0x20) == 0)
            {
                if ((attr & 0x80) != 0 && slot < spCount) row = 7 - row;
                addr = ((ctrl & 0x08) << 9) + tile * 16 + (row & 7);
            }
            else
            {
                if ((attr & 0x80) != 0 && slot < spCount) row = 15 - row;
                int bank = (tile & 1) << 12;
                int tt = tile & 0xFE;
                if (row >= 8) { tt++; row -= 8; }
                addr = bank + tt * 16 + row;
            }

            if (phase == 4)
            {
                int lo = Read(addr);
                if (slot < spCount)
                {
                    spLo[slot] = (attr & 0x40) != 0 ? Reverse[lo] : lo;
                    spAttr[slot] = attr;
                    spX[slot] = x;
                }
            }
            else
            {
                int hi = Read(addr + 8);
                if (slot < spCount) spHi[slot] = (attr & 0x40) != 0 ? Reverse[hi] : hi;
            }
        }

        void DrawPixel(int x, bool rendering)
        {
            int color;
            if (!rendering)
            {
                color = (v & 0x3F00) == 0x3F00 ? pal[PalIndex(v)] : pal[0];
            }
            else
            {
                int bgP = 0, bgPal = 0;
                if ((mask & 0x08) != 0 && (x >= 8 || (mask & 0x02) != 0))
                {
                    int sh = 15 - fineX;
                    bgP = ((bgLo >> sh) & 1) | (((bgHi >> sh) & 1) << 1);
                    bgPal = ((atLo >> sh) & 1) | (((atHi >> sh) & 1) << 1);
                }

                int spP = 0, spPal = 0;
                bool behind = false;
                if ((mask & 0x10) != 0 && (x >= 8 || (mask & 0x04) != 0))
                {
                    for (int i = 0; i < spCount; i++)
                    {
                        int off = x - spX[i];
                        if (off < 0 || off > 7) continue;
                        int sh = 7 - off;
                        int p = ((spLo[i] >> sh) & 1) | (((spHi[i] >> sh) & 1) << 1);
                        if (p == 0) continue;
                        if (i == 0 && sp0OnLine && bgP != 0 && x != 255) status |= 0x40;
                        spP = p;
                        spPal = spAttr[i] & 3;
                        behind = (spAttr[i] & 0x20) != 0;
                        break;
                    }
                }

                int idx;
                if (spP != 0 && (bgP == 0 || !behind)) idx = 0x10 | (spPal << 2) | spP;
                else if (bgP != 0) idx = (bgPal << 2) | bgP;
                else idx = 0;
                color = pal[PalIndex(idx)];
            }
            if ((mask & 0x01) != 0) color &= 0x30;
            Frame[Scanline * Width + x] = Palette.Rgb[(color & 0x3F) | ((mask & 0xE0) << 1)];
        }
    }
}
