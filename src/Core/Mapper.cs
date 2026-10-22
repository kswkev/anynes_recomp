using System;

namespace AnyNes.Core
{
    /// <summary>
    /// Cartridge base: banked PRG ($8000-$FFFF in 8K windows), PRG RAM at $6000, CHR in 1K windows and
    /// nametables in 1K windows, each of which may point at any backing array.
    /// </summary>
    [Serializable]
    public abstract class Mapper
    {
        protected Nes nes;
        protected RomImage rom;
        protected byte[] prg, chr, prgRam;
        protected readonly byte[] ciram = new byte[0x1000];
        protected bool chrIsRam;

        protected readonly int[] prgOff = new int[4];
        protected readonly byte[][] chrMem = new byte[8][];
        protected readonly int[] chrOff = new int[8];
        protected readonly bool[] chrWritable = new bool[8];
        protected readonly byte[][] ntMem = new byte[4][];
        protected readonly int[] ntOff = new int[4];
        protected readonly bool[] ntWritable = new bool[4];
        protected int prgRamOff;
        protected bool prgRamEnabled = true;

        public bool Irq;
        public bool NeedsCpuClock, WantsA12, WantsScanline, SnoopPpuRegs, HasAudio;

        bool a12High;
        long a12LowSince = -100;

        public void Attach(Nes nes, RomImage rom)
        {
            this.nes = nes;
            this.rom = rom;
            prg = rom.Prg;
            if (rom.Chr.Length > 0) chr = rom.Chr;
            else
            {
                chr = new byte[Math.Max(ChrRamSize(rom), 0x2000)];
                chrIsRam = true;
            }
            int ramSize = PrgRamSize(rom);
            if (ramSize > 0) prgRam = new byte[ramSize];
            if (rom.Trainer != null && prgRam != null) Array.Copy(rom.Trainer, 0, prgRam, 0x1000, 512);
            SetPrg32(0);
            SetChr8(0);
            SetMirroring(rom.Mirroring);
            Init();
        }

        protected virtual int PrgRamSize(RomImage r) { return Math.Max(0x2000, r.PrgRamSize); }
        protected virtual int ChrRamSize(RomImage r) { return r.ChrRamSize; }
        protected virtual void Init() { }
        public virtual void Reset() { }

        /// <summary>Battery-backed memory to persist, or null.</summary>
        public virtual byte[] BatteryRam { get { return rom.Battery ? prgRam : null; } }

        // ---------------------------------------------------------------- banking helpers

        protected int PrgBanks8 { get { return Math.Max(1, prg.Length / 0x2000); } }
        protected int ChrBanks1 { get { return Math.Max(1, chr.Length / 0x400); } }

        static int Wrap(int bank, int count)
        {
            bank %= count;
            return bank < 0 ? bank + count : bank;
        }

        protected void SetPrg8(int slot, int bank) { prgOff[slot] = Wrap(bank, PrgBanks8) * 0x2000; }
        protected void SetPrg16(int slot, int bank) { SetPrg8(slot * 2, bank * 2); SetPrg8(slot * 2 + 1, bank * 2 + 1); }
        protected void SetPrg32(int bank) { for (int i = 0; i < 4; i++) SetPrg8(i, bank * 4 + i); }

        protected void SetChr1(int slot, int bank)
        {
            chrMem[slot] = chr;
            chrOff[slot] = Wrap(bank, ChrBanks1) * 0x400;
            chrWritable[slot] = chrIsRam;
        }
        protected void SetChr2(int slot, int bank) { SetChr1(slot * 2, bank * 2); SetChr1(slot * 2 + 1, bank * 2 + 1); }
        protected void SetChr4(int slot, int bank) { for (int i = 0; i < 4; i++) SetChr1(slot * 4 + i, bank * 4 + i); }
        protected void SetChr8(int bank) { for (int i = 0; i < 8; i++) SetChr1(i, bank * 8 + i); }

        protected void SetNtCiram(int slot, int page)
        {
            ntMem[slot] = ciram;
            ntOff[slot] = (page & 3) * 0x400;
            ntWritable[slot] = true;
        }

        protected void SetNtPages(int a, int b, int c, int d)
        {
            SetNtCiram(0, a); SetNtCiram(1, b); SetNtCiram(2, c); SetNtCiram(3, d);
        }

        protected void SetMirroring(Mirroring m)
        {
            switch (m)
            {
                case Mirroring.Horizontal: SetNtPages(0, 0, 1, 1); break;
                case Mirroring.Vertical: SetNtPages(0, 1, 0, 1); break;
                case Mirroring.SingleLow: SetNtPages(0, 0, 0, 0); break;
                case Mirroring.SingleHigh: SetNtPages(1, 1, 1, 1); break;
                default: SetNtPages(0, 1, 2, 3); break;
            }
        }

        // ---------------------------------------------------------------- CPU side

        public virtual byte ReadCpu(int a)
        {
            if (a >= 0x8000) return prg[prgOff[(a >> 13) & 3] + (a & 0x1FFF)];
            if (a >= 0x6000 && prgRam != null && prgRamEnabled) return prgRam[prgRamOff + (a & 0x1FFF)];
            return (byte)nes.OpenBus;
        }

        public virtual void WriteCpu(int a, byte v)
        {
            if (a >= 0x8000) WriteRegister(a, v);
            else if (a >= 0x6000) WriteRam(a, v);
        }

        protected void WriteRam(int a, byte v)
        {
            if (prgRam != null && prgRamEnabled) prgRam[prgRamOff + (a & 0x1FFF)] = v;
        }

        protected virtual void WriteRegister(int a, byte v) { }

        public virtual void CpuClock() { }

        // ---------------------------------------------------------------- PPU side

        public virtual byte ReadChr(int a)
        {
            int s = a >> 10;
            return chrMem[s][chrOff[s] + (a & 0x3FF)];
        }

        public virtual void WriteChr(int a, byte v)
        {
            int s = a >> 10;
            if (chrWritable[s]) chrMem[s][chrOff[s] + (a & 0x3FF)] = v;
        }

        public virtual byte ReadNt(int a)
        {
            int s = (a >> 10) & 3;
            return ntMem[s][ntOff[s] + (a & 0x3FF)];
        }

        public virtual void WriteNt(int a, byte v)
        {
            int s = (a >> 10) & 3;
            if (ntWritable[s]) ntMem[s][ntOff[s] + (a & 0x3FF)] = v;
        }

        /// <summary>Sees every PPU bus address (when WantsA12) and detects filtered A12 rising edges.</summary>
        public void PpuAddress(int addr)
        {
            if ((addr & 0x1000) != 0)
            {
                if (!a12High)
                {
                    a12High = true;
                    if (nes.Ppu.Clock - a12LowSince >= 8) ClockA12();
                }
            }
            else if (a12High)
            {
                a12High = false;
                a12LowSince = nes.Ppu.Clock;
            }
        }

        protected virtual void ClockA12() { }
        public virtual void OnScanline(int scanline) { }
        public virtual void OnVblank() { }
        public virtual void OnPpuRegWrite(int reg, int v) { }
        public virtual float AudioOutput() { return 0; }
    }

    public static class MapperFactory
    {
        public static Mapper Create(RomImage rom)
        {
            switch (rom.Mapper)
            {
                case 0: return new Nrom();
                case 1: return new Mmc1();
                case 2: return new UxRom();
                case 3: return new CnRom();
                case 4: return new Mmc3();
                case 5: return new Mmc5();
                case 7: return new AxRom();
                case 9: return new Mmc2();
                case 11: case 144: return new ColorDreams();
                case 13: return new CpRom();
                case 19: return new Namco163();
                case 34: return new Mapper34();
                case 37: return new Mapper37();
                case 41: return new Mapper41();
                case 47: return new Mapper47();
                case 64: return new Rambo1();
                case 66: return new GxRom();
                case 68: return new Sunsoft4();
                case 69: return new Fme7();
                case 71: return new Camerica();
                case 79: return new Nina03();
                case 105: return new Mapper105();
                case 113: return new Mapper113();
                case 118: return new TxSRom();
                case 119: return new TqRom();
                case 228: return new Mapper228();
                case 232: return new Mapper232();
                case 234: return new Mapper234();
                default: throw new NotSupportedException("Mapper " + rom.Mapper + " is not supported.");
            }
        }

        public static bool IsSupported(int mapper)
        {
            switch (mapper)
            {
                case 0: case 1: case 2: case 3: case 4: case 5: case 7: case 9: case 11: case 13: case 19: case 144:
                case 34: case 37: case 41: case 47: case 64: case 66: case 68: case 69: case 71: case 79:
                case 105: case 113: case 118: case 119: case 228: case 232: case 234:
                    return true;
                default:
                    return false;
            }
        }

        public static string Name(int mapper)
        {
            switch (mapper)
            {
                case 0: return "NROM";
                case 1: return "MMC1";
                case 2: return "UxROM";
                case 3: return "CNROM";
                case 4: return "MMC3";
                case 5: return "MMC5";
                case 7: return "AxROM";
                case 9: return "MMC2";
                case 11: return "Color Dreams";
                case 13: return "CPROM";
                case 19: return "Namco 163";
                case 34: return "BNROM / NINA-001";
                case 37: return "MMC3 multicart (PAL-ZZ)";
                case 41: return "Caltron 6-in-1";
                case 47: return "MMC3 multicart (QJ)";
                case 64: return "Tengen RAMBO-1";
                case 66: return "GxROM";
                case 68: return "Sunsoft-4";
                case 69: return "Sunsoft FME-7";
                case 71: return "Camerica";
                case 79: return "AVE NINA-03/06";
                case 105: return "NES-EVENT";
                case 113: return "NINA/Sachen multicart";
                case 118: return "TxSROM";
                case 119: return "TQROM";
                case 144: return "AGCI (Color Dreams variant)";
                case 228: return "Action 52";
                case 232: return "Camerica Quattro";
                case 234: return "Maxi 15";
                default: return "Mapper " + mapper;
            }
        }
    }
}
