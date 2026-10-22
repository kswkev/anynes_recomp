using System;

namespace AnyNes.Core
{
    /// <summary>
    /// Nintendo MMC5 (ExROM). Covers PRG/CHR banking, ExRAM, extended attributes, fill mode, IRQ,
    /// multiplier and expansion audio (two pulse channels plus raw PCM).
    /// </summary>
    [Serializable]
    sealed class Mmc5 : Mapper
    {
        int prgMode = 3, chrMode, ramProtect1, ramProtect2, exMode, ntMapping, fillTile, fillAttr;
        readonly int[] prgRegs = { 0, 0xFF, 0xFF, 0xFF, 0xFF };   // $5113-$5117
        readonly int[] chrA = new int[8], chrB = new int[4];
        int chrUpper;
        bool lastWasA = true;
        readonly byte[] exram = new byte[0x400];
        int irqTarget, scanlineCounter;
        bool irqEnabled, irqPending, inFrame;
        int mulA = 0xFF, mulB = 0xFF;
        bool tallSprites;
        int lastExByte;

        // Expansion audio. Envelopes and length counters run off a fixed 240 Hz sequencer.
        readonly Apu.Pulse pulse1 = new Apu.Pulse(false, false), pulse2 = new Apu.Pulse(true, false);
        int pcm, audioFrame;
        bool audioOdd;
        // $5010: bit 0 = PCM read mode (samples come from CPU reads of $8000-$BFFF), bit 7 = PCM IRQ
        // enable. In read mode a $00 byte raises the PCM IRQ instead of updating the output.
        bool pcmReadMode, pcmIrqEnabled, pcmIrqPending;

        readonly byte[][] cpuMem = new byte[5][];
        readonly int[] cpuOff = new int[5];
        readonly bool[] cpuIsRam = new bool[5];

        protected override int PrgRamSize(RomImage r) { return 0x10000; }

        protected override void Init()
        {
            WantsScanline = true;
            SnoopPpuRegs = true;
            NeedsCpuClock = true;
            HasAudio = true;
            UpdatePrg();
        }

        void WriteAudio(int a, byte v)
        {
            switch (a)
            {
                case 0x5000: case 0x5002: case 0x5003: pulse1.Write(a & 3, v); break;
                case 0x5004: case 0x5006: case 0x5007: pulse2.Write(a & 3, v); break;
                case 0x5010:
                    pcmReadMode = (v & 1) != 0;
                    pcmIrqEnabled = (v & 0x80) != 0;
                    UpdateIrq();
                    break;
                case 0x5011: if (!pcmReadMode && v != 0) pcm = v; break;    // write mode; 0 is ignored
                case 0x5015:
                    pulse1.Enabled = (v & 1) != 0; if (!pulse1.Enabled) pulse1.Length = 0;
                    pulse2.Enabled = (v & 2) != 0; if (!pulse2.Enabled) pulse2.Length = 0;
                    break;
            }
        }

        public override void CpuClock()
        {
            if (++audioFrame >= 7457)
            {
                audioFrame = 0;
                pulse1.Env.Clock(); pulse2.Env.Clock();
                pulse1.ClockHalf(); pulse2.ClockHalf();
            }
            audioOdd = !audioOdd;
            if (audioOdd) { pulse1.ClockTimer(); pulse2.ClockTimer(); }
        }

        public override float AudioOutput()
        {
            return Apu.PulseMix[pulse1.Output + pulse2.Output] + pcm * (0.4f / 255f);
        }

        public override void Reset()
        {
            prgMode = 3;
            prgRegs[4] = 0xFF;
            UpdatePrg();
        }

        public override byte[] BatteryRam { get { return rom.Battery ? prgRam : null; } }

        void MapSlot(int slot, int reg, bool forceRom)
        {
            if (forceRom || (reg & 0x80) != 0)
            {
                cpuMem[slot] = prg;
                cpuOff[slot] = ((reg & 0x7F) % PrgBanks8) * 0x2000;
                cpuIsRam[slot] = false;
            }
            else
            {
                cpuMem[slot] = prgRam;
                cpuOff[slot] = ((reg & 7) % (prgRam.Length / 0x2000)) * 0x2000;
                cpuIsRam[slot] = true;
            }
        }

        void UpdatePrg()
        {
            MapSlot(0, prgRegs[0] & 0x7F, false);
            switch (prgMode)
            {
                case 0:
                    {
                        int b = prgRegs[4] & 0x7C;
                        for (int i = 0; i < 4; i++) MapSlot(1 + i, b | i, true);
                        break;
                    }
                case 1:
                    {
                        int r = prgRegs[2] & 0xFE;
                        MapSlot(1, r, false); MapSlot(2, r | 1, false);
                        int r2 = prgRegs[4] & 0x7E;
                        MapSlot(3, r2, true); MapSlot(4, r2 | 1, true);
                        break;
                    }
                case 2:
                    {
                        int r = prgRegs[2] & 0xFE;
                        MapSlot(1, r, false); MapSlot(2, r | 1, false);
                        MapSlot(3, prgRegs[3], false);
                        MapSlot(4, prgRegs[4], true);
                        break;
                    }
                default:
                    MapSlot(1, prgRegs[1], false);
                    MapSlot(2, prgRegs[2], false);
                    MapSlot(3, prgRegs[3], false);
                    MapSlot(4, prgRegs[4], true);
                    break;
            }
        }

        void UpdateIrq() { Irq = (irqPending && irqEnabled) || (pcmIrqPending && pcmIrqEnabled); }

        public override byte ReadCpu(int a)
        {
            if (a >= 0x6000)
            {
                int s = (a - 0x6000) >> 13;
                byte v = cpuMem[s][cpuOff[s] + (a & 0x1FFF)];
                if (pcmReadMode && a >= 0x8000 && a < 0xC000)
                {
                    if (v != 0) pcm = v;
                    else { pcmIrqPending = true; UpdateIrq(); }
                }
                return v;
            }
            if (a >= 0x5C00)
            {
                if (exMode >= 2) return exram[a - 0x5C00];
                return (byte)nes.OpenBus;
            }
            switch (a)
            {
                case 0x5204:
                    {
                        int r = (irqPending ? 0x80 : 0) | (inFrame ? 0x40 : 0);
                        irqPending = false;
                        UpdateIrq();
                        return (byte)r;
                    }
                case 0x5010:
                    {
                        // Bit 7 reports (and acknowledges) the PCM IRQ.
                        int r = pcmIrqPending && pcmIrqEnabled ? 0x80 : 0;
                        pcmIrqPending = false;
                        UpdateIrq();
                        return (byte)r;
                    }
                case 0x5015: return (byte)((pulse1.Length > 0 ? 1 : 0) | (pulse2.Length > 0 ? 2 : 0));
                case 0x5205: return (byte)(mulA * mulB);
                case 0x5206: return (byte)((mulA * mulB) >> 8);
            }
            return (byte)nes.OpenBus;
        }

        public override void WriteCpu(int a, byte v)
        {
            if (a >= 0x6000)
            {
                int s = (a - 0x6000) >> 13;
                if (cpuIsRam[s] && ramProtect1 == 2 && ramProtect2 == 1) cpuMem[s][cpuOff[s] + (a & 0x1FFF)] = v;
                return;
            }
            if (a >= 0x5C00)
            {
                if (exMode != 3) exram[a - 0x5C00] = v;
                return;
            }
            if (a >= 0x5000 && a <= 0x5015) { WriteAudio(a, v); return; }
            if (a >= 0x5113 && a <= 0x5117) { prgRegs[a - 0x5113] = v; UpdatePrg(); return; }
            if (a >= 0x5120 && a <= 0x5127) { chrA[a - 0x5120] = v | (chrUpper << 8); lastWasA = true; return; }
            if (a >= 0x5128 && a <= 0x512B) { chrB[a - 0x5128] = v | (chrUpper << 8); lastWasA = false; return; }
            switch (a)
            {
                case 0x5100: prgMode = v & 3; UpdatePrg(); break;
                case 0x5101: chrMode = v & 3; break;
                case 0x5102: ramProtect1 = v & 3; break;
                case 0x5103: ramProtect2 = v & 3; break;
                case 0x5104: exMode = v & 3; break;
                case 0x5105: ntMapping = v; break;
                case 0x5106: fillTile = v; break;
                case 0x5107: fillAttr = v & 3; break;
                case 0x5130: chrUpper = v & 3; break;
                case 0x5203: irqTarget = v; break;
                case 0x5204: irqEnabled = (v & 0x80) != 0; UpdateIrq(); break;
                case 0x5205: mulA = v; break;
                case 0x5206: mulB = v; break;
            }
        }

        public override void OnPpuRegWrite(int reg, int v)
        {
            if (reg == 0) tallSprites = (v & 0x20) != 0;
            else if (reg == 1 && (v & 0x18) == 0) inFrame = false;
        }

        public override void OnScanline(int scanline)
        {
            if (scanline == 0) { inFrame = true; scanlineCounter = 0; }
            else scanlineCounter++;
            if (irqTarget != 0 && scanlineCounter == irqTarget)
            {
                irqPending = true;
                UpdateIrq();
            }
        }

        public override void OnVblank() { inFrame = false; }

        int ChrAddress(int a)
        {
            int kind = nes.Ppu.FetchKind;
            if (exMode == 1 && kind == 1)
            {
                int bank = (lastExByte & 0x3F) | (chrUpper << 6);
                return bank * 0x1000 + (a & 0x0FFF);
            }
            bool useB = tallSprites ? (kind == 1 || (kind == 0 && !lastWasA)) : !lastWasA;
            switch (chrMode)
            {
                case 0: return (useB ? chrB[3] : chrA[7]) * 0x2000 + a;
                case 1: return (useB ? chrB[3] : chrA[(a >> 12) * 4 + 3]) * 0x1000 + (a & 0x0FFF);
                case 2: return (useB ? chrB[((a >> 11) & 1) * 2 + 1] : chrA[(a >> 11) * 2 + 1]) * 0x800 + (a & 0x07FF);
                default: return (useB ? chrB[(a >> 10) & 3] : chrA[a >> 10]) * 0x400 + (a & 0x03FF);
            }
        }

        public override byte ReadChr(int a)
        {
            return chr[ChrAddress(a) % chr.Length];
        }

        public override void WriteChr(int a, byte v)
        {
            if (chrIsRam) chr[ChrAddress(a) % chr.Length] = v;
        }

        public override byte ReadNt(int a)
        {
            int slot = (a >> 10) & 3;
            int off = a & 0x3FF;
            if (exMode == 1 && nes.Ppu.FetchKind == 1)
            {
                if (off < 0x3C0) lastExByte = exram[off];
                else
                {
                    int p = lastExByte >> 6;
                    return (byte)(p | (p << 2) | (p << 4) | (p << 6));
                }
            }
            switch ((ntMapping >> (slot * 2)) & 3)
            {
                case 0: return ciram[off];
                case 1: return ciram[0x400 + off];
                case 2: return exMode <= 1 ? exram[off] : (byte)0;
                default: return off < 0x3C0 ? (byte)fillTile : (byte)(fillAttr * 0x55);
            }
        }

        public override void WriteNt(int a, byte v)
        {
            int slot = (a >> 10) & 3;
            int off = a & 0x3FF;
            switch ((ntMapping >> (slot * 2)) & 3)
            {
                case 0: ciram[off] = v; break;
                case 1: ciram[0x400 + off] = v; break;
                case 2: exram[off] = v; break;
            }
        }
    }
}
