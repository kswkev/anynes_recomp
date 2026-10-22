using System;

namespace AnyNes.Core
{
    [Serializable]
    class Mmc3 : Mapper
    {
        protected readonly int[] regs = { 0, 2, 4, 5, 6, 7, 0, 1 };
        protected int bankSelect;
        int irqLatch, irqCounter;
        bool irqReload, irqEnabled;

        protected override void Init()
        {
            WantsA12 = true;
            UpdateBanks();
        }

        public override void Reset()
        {
            bankSelect = 0;
            irqEnabled = false;
            Irq = false;
            UpdateBanks();
        }

        /// <summary>Hooks for multicart outer banking; arguments may be negative (-1 = last bank).</summary>
        protected virtual int PrgBank(int bank) { return bank; }
        protected virtual int ChrBank(int bank) { return bank; }

        protected virtual void SetChrBank(int slot, int bank) { SetChr1(slot, ChrBank(bank)); }

        protected virtual void UpdateBanks()
        {
            int r6 = regs[6] & 0x3F, r7 = regs[7] & 0x3F;
            if ((bankSelect & 0x40) == 0) { SetPrg8(0, PrgBank(r6)); SetPrg8(2, PrgBank(-2)); }
            else { SetPrg8(0, PrgBank(-2)); SetPrg8(2, PrgBank(r6)); }
            SetPrg8(1, PrgBank(r7));
            SetPrg8(3, PrgBank(-1));

            int x = (bankSelect & 0x80) != 0 ? 4 : 0;
            SetChrBank(0 ^ x, regs[0] & 0xFE);
            SetChrBank(1 ^ x, regs[0] | 1);
            SetChrBank(2 ^ x, regs[1] & 0xFE);
            SetChrBank(3 ^ x, regs[1] | 1);
            SetChrBank(4 ^ x, regs[2]);
            SetChrBank(5 ^ x, regs[3]);
            SetChrBank(6 ^ x, regs[4]);
            SetChrBank(7 ^ x, regs[5]);
        }

        protected override void WriteRegister(int a, byte v)
        {
            switch (a & 0xE001)
            {
                case 0x8000: bankSelect = v; UpdateBanks(); break;
                case 0x8001: regs[bankSelect & 7] = v; UpdateBanks(); break;
                case 0xA000:
                    if (rom.Mirroring != Mirroring.FourScreen)
                        SetMirroring((v & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
                    break;
                case 0xA001: break;   // PRG RAM protect: ignored (also keeps MMC6 games working)
                case 0xC000: irqLatch = v; break;
                case 0xC001: irqCounter = 0; irqReload = true; break;
                case 0xE000: irqEnabled = false; Irq = false; break;
                case 0xE001: irqEnabled = true; break;
            }
        }

        protected override void ClockA12()
        {
            if (irqCounter == 0 || irqReload)
            {
                irqCounter = irqLatch;
                irqReload = false;
            }
            else irqCounter--;
            if (irqCounter == 0 && irqEnabled) Irq = true;
        }
    }

    /// <summary>TxSROM: CHR bank bit 7 selects the CIRAM page for each nametable.</summary>
    [Serializable]
    sealed class TxSRom : Mmc3
    {
        protected override void UpdateBanks()
        {
            base.UpdateBanks();
            if ((bankSelect & 0x80) == 0)
            {
                SetNtCiram(0, regs[0] >> 7); SetNtCiram(1, regs[0] >> 7);
                SetNtCiram(2, regs[1] >> 7); SetNtCiram(3, regs[1] >> 7);
            }
            else
            {
                for (int i = 0; i < 4; i++) SetNtCiram(i, regs[2 + i] >> 7);
            }
        }

        protected override void WriteRegister(int a, byte v)
        {
            if ((a & 0xE001) == 0xA000) return;
            base.WriteRegister(a, v);
        }
    }

    /// <summary>TQROM: CHR bank bit 6 selects the 8K CHR RAM instead of CHR ROM.</summary>
    [Serializable]
    sealed class TqRom : Mmc3
    {
        byte[] chrRam;

        protected override void SetChrBank(int slot, int bank)
        {
            if (chrRam == null) chrRam = new byte[0x2000];
            if ((bank & 0x40) != 0)
            {
                chrMem[slot] = chrRam;
                chrOff[slot] = (bank & 7) * 0x400;
                chrWritable[slot] = true;
            }
            else SetChr1(slot, bank & 0x3F);
        }
    }

    /// <summary>Super Mario Bros. + Tetris + Nintendo World Cup.</summary>
    [Serializable]
    sealed class Mapper37 : Mmc3
    {
        int outer;

        protected override int PrgBank(int bank)
        {
            if (outer <= 2) return bank & 7;
            if (outer == 3) return 0x08 | (bank & 7);
            if (outer <= 6) return 0x10 | (bank & 0x0F);
            return 0x18 | (bank & 7);
        }

        protected override int ChrBank(int bank) { return ((outer & 4) << 5) | (bank & 0x7F); }

        public override void WriteCpu(int a, byte v)
        {
            if (a >= 0x6000 && a < 0x8000) { outer = v & 7; UpdateBanks(); }
            else base.WriteCpu(a, v);
        }

        public override void Reset() { outer = 0; base.Reset(); }
    }

    /// <summary>Super Spike V'Ball + Nintendo World Cup.</summary>
    [Serializable]
    sealed class Mapper47 : Mmc3
    {
        int outer;

        protected override int PrgBank(int bank) { return (outer << 4) | (bank & 0x0F); }
        protected override int ChrBank(int bank) { return (outer << 7) | (bank & 0x7F); }

        public override void WriteCpu(int a, byte v)
        {
            if (a >= 0x6000 && a < 0x8000) { outer = v & 1; UpdateBanks(); }
            else base.WriteCpu(a, v);
        }

        public override void Reset() { outer = 0; base.Reset(); }
    }

    /// <summary>Tengen RAMBO-1.</summary>
    [Serializable]
    sealed class Rambo1 : Mapper
    {
        readonly int[] regs = new int[16];
        int bankSelect, irqLatch, irqCounter, prescaler;
        bool irqReload, irqEnabled, cycleMode;

        protected override void Init()
        {
            WantsA12 = true;
            NeedsCpuClock = true;
            Update();
        }

        void Update()
        {
            if ((bankSelect & 0x40) == 0)
            {
                SetPrg8(0, regs[6]); SetPrg8(1, regs[7]); SetPrg8(2, regs[15]);
            }
            else
            {
                SetPrg8(0, regs[15]); SetPrg8(1, regs[6]); SetPrg8(2, regs[7]);
            }
            SetPrg8(3, -1);

            int x = (bankSelect & 0x80) != 0 ? 4 : 0;
            if ((bankSelect & 0x20) != 0)
            {
                SetChr1(0 ^ x, regs[0]); SetChr1(1 ^ x, regs[8]);
                SetChr1(2 ^ x, regs[1]); SetChr1(3 ^ x, regs[9]);
            }
            else
            {
                SetChr1(0 ^ x, regs[0] & 0xFE); SetChr1(1 ^ x, regs[0] | 1);
                SetChr1(2 ^ x, regs[1] & 0xFE); SetChr1(3 ^ x, regs[1] | 1);
            }
            SetChr1(4 ^ x, regs[2]); SetChr1(5 ^ x, regs[3]);
            SetChr1(6 ^ x, regs[4]); SetChr1(7 ^ x, regs[5]);
        }

        protected override void WriteRegister(int a, byte v)
        {
            switch (a & 0xE001)
            {
                case 0x8000: bankSelect = v; Update(); break;
                case 0x8001: regs[bankSelect & 0x0F] = v; Update(); break;
                case 0xA000: SetMirroring((v & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical); break;
                case 0xC000: irqLatch = v; break;
                case 0xC001:
                    cycleMode = (v & 1) != 0;
                    prescaler = 0;
                    irqReload = true;
                    break;
                case 0xE000: irqEnabled = false; Irq = false; break;
                case 0xE001: irqEnabled = true; break;
            }
        }

        void ClockCounter()
        {
            if (irqReload)
            {
                irqCounter = irqLatch <= 1 ? irqLatch + 1 : irqLatch + 2;
                irqReload = false;
            }
            else if (irqCounter == 0) irqCounter = irqLatch + 1;
            irqCounter--;
            if (irqCounter == 0 && irqEnabled) Irq = true;
        }

        protected override void ClockA12() { if (!cycleMode) ClockCounter(); }

        public override void CpuClock()
        {
            if (cycleMode && ++prescaler >= 4) { prescaler = 0; ClockCounter(); }
        }
    }
}
