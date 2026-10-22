using System;

namespace AnyNes.Core
{
    [Serializable]
    class Mmc1 : Mapper
    {
        protected int shift = 0x10, control = 0x0C, chr0, chr1, prgReg;
        long lastWrite = -10;

        protected override int PrgRamSize(RomImage r)
        {
            // SOROM/SXROM (Koei titles etc.) carry 16K/32K of PRG RAM, banked through the CHR registers.
            if (r.IsNes2 && r.PrgRamSize > 0) return r.PrgRamSize;
            switch (r.Crc32)
            {
                case 0x2225C20F: // Genghis Khan (USA)            - SOROM
                case 0x4642DDA6: // Nobunaga's Ambition (USA)     - SOROM
                case 0xC6182024: // Romance of the Three Kingdoms - SOROM
                    return 0x4000;
                default:
                    return 0x2000;
            }
        }

        protected override void Init() { Update(); }

        public override void Reset()
        {
            shift = 0x10;
            control |= 0x0C;
            Update();
        }

        protected override void WriteRegister(int a, byte v)
        {
            // Writes on consecutive cycles (read-modify-write instructions) are ignored by the chip.
            long cyc = nes.Cpu.Cycles;
            bool consecutive = cyc == lastWrite + 1;
            lastWrite = cyc;
            if (consecutive) return;

            if ((v & 0x80) != 0)
            {
                shift = 0x10;
                control |= 0x0C;
                Update();
                return;
            }
            bool full = (shift & 1) != 0;
            shift = (shift >> 1) | ((v & 1) << 4);
            if (!full) return;
            switch ((a >> 13) & 3)
            {
                case 0: control = shift; break;
                case 1: chr0 = shift; break;
                case 2: chr1 = shift; break;
                case 3: prgReg = shift; break;
            }
            shift = 0x10;
            Update();
        }

        protected virtual void Update()
        {
            switch (control & 3)
            {
                case 0: SetMirroring(Mirroring.SingleLow); break;
                case 1: SetMirroring(Mirroring.SingleHigh); break;
                case 2: SetMirroring(Mirroring.Vertical); break;
                case 3: SetMirroring(Mirroring.Horizontal); break;
            }

            if ((control & 0x10) != 0) { SetChr4(0, chr0); SetChr4(1, chr1); }
            else SetChr8(chr0 >> 1);

            // SUROM/SXROM: CHR register bit 4 selects the 256K PRG half.
            int outer = prg.Length > 0x40000 ? (chr0 & 0x10) : 0;
            UpdatePrg(outer, prgReg & 0x0F);

            if (prgRam.Length >= 0x8000) prgRamOff = ((chr0 >> 2) & 3) * 0x2000;
            else if (prgRam.Length >= 0x4000) prgRamOff = ((chr0 >> 3) & 1) * 0x2000;
        }

        protected void UpdatePrg(int outer, int bank)
        {
            switch ((control >> 2) & 3)
            {
                case 0:
                case 1:
                    SetPrg16(0, outer | (bank & 0x0E));
                    SetPrg16(1, outer | (bank & 0x0E) | 1);
                    break;
                case 2:
                    SetPrg16(0, outer);
                    SetPrg16(1, outer | bank);
                    break;
                default:
                    SetPrg16(0, outer | bank);
                    SetPrg16(1, outer | 0x0F);
                    break;
            }
        }
    }

    /// <summary>Nintendo World Championships 1990: MMC1 with a two-chip PRG layout and a countdown timer.</summary>
    [Serializable]
    sealed class Mapper105 : Mmc1
    {
        int initState;
        long counter;
        const long TimerTarget = 0x20000000L + (4L << 25);   // DIP switches set to ~6.25 minutes

        protected override void Init()
        {
            NeedsCpuClock = true;
            Update();
        }

        protected override void Update()
        {
            switch (control & 3)
            {
                case 0: SetMirroring(Mirroring.SingleLow); break;
                case 1: SetMirroring(Mirroring.SingleHigh); break;
                case 2: SetMirroring(Mirroring.Vertical); break;
                case 3: SetMirroring(Mirroring.Horizontal); break;
            }
            SetChr8(0);

            if (initState == 0 && (chr0 & 0x10) != 0) initState = 1;
            else if (initState == 1 && (chr0 & 0x10) == 0) initState = 2;

            if (initState < 2) { SetPrg32(0); return; }
            if ((chr0 & 0x08) == 0) SetPrg32((chr0 >> 1) & 3);
            else UpdatePrg(8, prgReg & 7);
        }

        public override void CpuClock()
        {
            if ((chr0 & 0x10) != 0) { counter = 0; Irq = false; return; }
            if (++counter == TimerTarget) Irq = true;
        }
    }
}
