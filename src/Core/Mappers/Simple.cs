using System;

namespace AnyNes.Core
{
    [Serializable] sealed class Nrom : Mapper { }

    [Serializable]
    sealed class UxRom : Mapper
    {
        protected override void Init() { SetPrg16(0, 0); SetPrg16(1, -1); }
        protected override void WriteRegister(int a, byte v) { SetPrg16(0, v); }
    }

    [Serializable]
    sealed class CnRom : Mapper
    {
        // CNROM boards have bus conflicts: the written value is ANDed with the ROM byte.
        protected override void WriteRegister(int a, byte v) { SetChr8(v & ReadCpu(a)); }
    }

    [Serializable]
    sealed class AxRom : Mapper
    {
        protected override void Init() { SetPrg32(0); SetMirroring(Mirroring.SingleLow); }
        protected override void WriteRegister(int a, byte v)
        {
            SetPrg32(v & 0x0F);
            SetMirroring((v & 0x10) != 0 ? Mirroring.SingleHigh : Mirroring.SingleLow);
        }
    }

    [Serializable]
    sealed class Mmc2 : Mapper
    {
        int fd0, fe0, fd1, fe1;
        int latch0 = 0xFE, latch1 = 0xFE;

        protected override void Init()
        {
            SetPrg8(0, 0); SetPrg8(1, -3); SetPrg8(2, -2); SetPrg8(3, -1);
            UpdateChr();
        }

        protected override void WriteRegister(int a, byte v)
        {
            switch (a & 0xF000)
            {
                case 0xA000: SetPrg8(0, v & 0x0F); break;
                case 0xB000: fd0 = v & 0x1F; break;
                case 0xC000: fe0 = v & 0x1F; break;
                case 0xD000: fd1 = v & 0x1F; break;
                case 0xE000: fe1 = v & 0x1F; break;
                case 0xF000: SetMirroring((v & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical); break;
            }
            UpdateChr();
        }

        void UpdateChr()
        {
            SetChr4(0, latch0 == 0xFD ? fd0 : fe0);
            SetChr4(1, latch1 == 0xFD ? fd1 : fe1);
        }

        public override byte ReadChr(int a)
        {
            byte r = base.ReadChr(a);
            if (a == 0x0FD8) { latch0 = 0xFD; UpdateChr(); }
            else if (a == 0x0FE8) { latch0 = 0xFE; UpdateChr(); }
            else if (a >= 0x1FD8 && a <= 0x1FDF) { latch1 = 0xFD; UpdateChr(); }
            else if (a >= 0x1FE8 && a <= 0x1FEF) { latch1 = 0xFE; UpdateChr(); }
            return r;
        }
    }

    [Serializable]
    sealed class ColorDreams : Mapper
    {
        // Mapper 144 (AGCI, e.g. Death Race) is Color Dreams with bus conflicts where ROM always wins bit 0.
        bool agci;

        protected override void Init() { agci = rom.Mapper == 144 || rom.Crc32 == 0x5CAA3E61; }

        protected override void WriteRegister(int a, byte v)
        {
            if (agci)
            {
                int r = ReadCpu(a);
                v = (byte)((v & r) | (r & 1));
            }
            SetPrg32(v & 3);
            SetChr8(v >> 4);
        }
    }

    [Serializable]
    sealed class CpRom : Mapper
    {
        protected override int ChrRamSize(RomImage r) { return 0x4000; }
        protected override void Init() { SetChr4(0, 0); SetChr4(1, 0); }
        protected override void WriteRegister(int a, byte v) { SetChr4(1, v & 3); }
    }

    [Serializable]
    sealed class Mapper34 : Mapper
    {
        // CHR ROM larger than 8K means NINA-001 (registers at $7FFD-$7FFF), otherwise BNROM.
        bool nina;

        protected override void Init() { nina = chr.Length > 0x2000; }

        public override void WriteCpu(int a, byte v)
        {
            if (nina && a >= 0x7FFD && a <= 0x7FFF)
            {
                if (a == 0x7FFD) SetPrg32(v & 1);
                else if (a == 0x7FFE) SetChr4(0, v & 0x0F);
                else SetChr4(1, v & 0x0F);
            }
            base.WriteCpu(a, v);
        }

        protected override void WriteRegister(int a, byte v) { if (!nina) SetPrg32(v); }
    }

    [Serializable]
    sealed class Mapper41 : Mapper
    {
        int outer, inner;

        public override void WriteCpu(int a, byte v)
        {
            if (a >= 0x6000 && a < 0x6800)
            {
                outer = a & 0x3F;
                SetPrg32(outer & 7);
                SetMirroring((outer & 0x20) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
                Update();
            }
            else if (a >= 0x8000)
            {
                if ((outer & 4) != 0) { inner = v & 3; Update(); }
            }
        }

        void Update() { SetChr8(((outer >> 1) & 0x0C) | inner); }
        public override void Reset() { outer = 0; inner = 0; SetPrg32(0); Update(); }
    }

    [Serializable]
    sealed class GxRom : Mapper
    {
        protected override void WriteRegister(int a, byte v) { SetPrg32((v >> 4) & 3); SetChr8(v & 3); }
    }

    [Serializable]
    sealed class Camerica : Mapper
    {
        protected override void Init() { SetPrg16(0, 0); SetPrg16(1, -1); }

        protected override void WriteRegister(int a, byte v)
        {
            if (a >= 0xC000) SetPrg16(0, v);
            else if (a < 0xA000) SetMirroring((v & 0x10) != 0 ? Mirroring.SingleHigh : Mirroring.SingleLow);
        }
    }

    [Serializable]
    sealed class Nina03 : Mapper
    {
        public override void WriteCpu(int a, byte v)
        {
            if ((a & 0xE100) == 0x4100) { SetPrg32((v >> 3) & 1); SetChr8(v & 7); }
            else base.WriteCpu(a, v);
        }
    }

    [Serializable]
    sealed class Mapper113 : Mapper
    {
        public override void WriteCpu(int a, byte v)
        {
            if ((a & 0xE100) == 0x4100)
            {
                SetPrg32((v >> 3) & 7);
                SetChr8((v & 7) | ((v >> 3) & 8));
                SetMirroring((v & 0x80) != 0 ? Mirroring.Vertical : Mirroring.Horizontal);
            }
            else base.WriteCpu(a, v);
        }
    }

    [Serializable]
    sealed class Mapper228 : Mapper
    {
        protected override void WriteRegister(int a, byte v)
        {
            int chip = (a >> 11) & 3;
            if (chip == 3) chip = 2;
            int page = ((a >> 6) & 0x1F) | (chip << 5);
            if ((a & 0x20) != 0) { SetPrg16(0, page); SetPrg16(1, page); }
            else SetPrg32(page >> 1);
            SetMirroring((a & 0x2000) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
            SetChr8(((a & 0x0F) << 2) | (v & 3));
        }

        public override void Reset() { SetPrg32(0); SetChr8(0); }
    }

    [Serializable]
    sealed class Mapper232 : Mapper
    {
        int outer, inner;

        protected override void Init() { Update(); }

        protected override void WriteRegister(int a, byte v)
        {
            if (a < 0xC000) outer = (v >> 3) & 3;
            else inner = v & 3;
            Update();
        }

        void Update()
        {
            SetPrg16(0, outer * 4 + inner);
            SetPrg16(1, outer * 4 + 3);
        }
    }

    [Serializable]
    sealed class Mapper234 : Mapper
    {
        int reg0, reg1;

        protected override void Init() { Update(); }
        public override void Reset() { reg0 = reg1 = 0; Update(); }

        public override byte ReadCpu(int a)
        {
            byte r = base.ReadCpu(a);
            if (a >= 0xFF80) Latch(a, r);
            return r;
        }

        protected override void WriteRegister(int a, byte v)
        {
            if (a >= 0xFF80) Latch(a, v);
        }

        void Latch(int a, int v)
        {
            if (a <= 0xFF9F) { if ((reg0 & 0x3F) == 0) { reg0 = v; Update(); } }
            else if (a >= 0xFFE8 && a <= 0xFFF8) { reg1 = v; Update(); }
        }

        void Update()
        {
            if ((reg0 & 0x40) != 0)
            {
                SetPrg32((reg0 & 0x0E) | (reg1 & 1));
                SetChr8(((reg0 & 0x0E) << 2) | ((reg1 >> 4) & 7));
            }
            else
            {
                SetPrg32(reg0 & 0x0F);
                SetChr8(((reg0 & 0x0F) << 2) | ((reg1 >> 4) & 3));
            }
            SetMirroring((reg0 & 0x80) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
        }
    }

    [Serializable]
    sealed class Sunsoft4 : Mapper
    {
        readonly int[] ntReg = new int[2];
        int mirror;
        bool chrNt;

        protected override void Init() { SetPrg16(0, 0); SetPrg16(1, -1); }

        protected override void WriteRegister(int a, byte v)
        {
            switch (a & 0xF000)
            {
                case 0x8000: SetChr2(0, v); break;
                case 0x9000: SetChr2(1, v); break;
                case 0xA000: SetChr2(2, v); break;
                case 0xB000: SetChr2(3, v); break;
                case 0xC000: ntReg[0] = v | 0x80; UpdateNt(); break;
                case 0xD000: ntReg[1] = v | 0x80; UpdateNt(); break;
                case 0xE000: mirror = v & 3; chrNt = (v & 0x10) != 0; UpdateNt(); break;
                case 0xF000: SetPrg16(0, v & 0x0F); prgRamEnabled = (v & 0x10) != 0; break;
            }
        }

        void UpdateNt()
        {
            int[] pages;
            switch (mirror)
            {
                case 0: pages = new[] { 0, 1, 0, 1 }; break;
                case 1: pages = new[] { 0, 0, 1, 1 }; break;
                case 2: pages = new[] { 0, 0, 0, 0 }; break;
                default: pages = new[] { 1, 1, 1, 1 }; break;
            }
            for (int i = 0; i < 4; i++)
            {
                if (chrNt)
                {
                    ntMem[i] = chr;
                    ntOff[i] = (ntReg[pages[i]] % ChrBanks1) * 0x400;
                    ntWritable[i] = false;
                }
                else SetNtCiram(i, pages[i]);
            }
        }
    }
}
