using System;

namespace AnyNes.Core
{
    /// <summary>Namco 163 (banking, CIRAM/CHR nametables, cycle IRQ, internal RAM, wavetable audio).</summary>
    [Serializable]
    sealed class Namco163 : Mapper
    {
        readonly int[] chrReg = new int[8];
        readonly int[] ntReg = new int[4];
        readonly byte[] chipRam = new byte[128];
        int chipAddr, irqCounter;
        bool autoInc, ciramLo = true, ciramHi = true;

        // Wavetable audio: channel registers live in chip RAM $40-$7F (channel 7 at $78), waveforms
        // are 4-bit samples anywhere in the RAM. One channel is updated every 15 CPU cycles.
        readonly int[] channelOut = new int[8];
        int audioTick, audioChannel = 7;
        bool soundDisabled;

        protected override void Init()
        {
            NeedsCpuClock = true;
            HasAudio = true;
            SetPrg8(3, -1);
            UpdateChr();
        }

        int ActiveChannels { get { return ((chipRam[0x7F] >> 4) & 7) + 1; } }

        void UpdateAudioChannel()
        {
            int count = ActiveChannels;
            if (audioChannel < 8 - count) audioChannel = 7;
            int ch = audioChannel;
            int b = 0x40 + ch * 8;

            int freq = chipRam[b] | (chipRam[b + 2] << 8) | ((chipRam[b + 4] & 3) << 16);
            int phase = chipRam[b + 1] | (chipRam[b + 3] << 8) | (chipRam[b + 5] << 16);
            int length = 256 - (chipRam[b + 4] & 0xFC);
            phase = (phase + freq) % (length << 16);
            chipRam[b + 1] = (byte)phase;
            chipRam[b + 3] = (byte)(phase >> 8);
            chipRam[b + 5] = (byte)(phase >> 16);

            int idx = ((phase >> 16) + chipRam[b + 6]) & 0xFF;
            int sample = (chipRam[idx >> 1] >> ((idx & 1) * 4)) & 0x0F;
            channelOut[ch] = (sample - 8) * (chipRam[b + 7] & 0x0F);

            audioChannel = ch - 1 < 8 - count ? 7 : ch - 1;
        }

        public override float AudioOutput()
        {
            if (soundDisabled) return 0;
            // The chip time-multiplexes its channels, so the audible level is their average.
            int count = ActiveChannels, sum = 0;
            for (int ch = 8 - count; ch < 8; ch++) sum += channelOut[ch];
            return sum * (0.3f / 120f) / count;
        }

        public override byte ReadCpu(int a)
        {
            if (a >= 0x4800 && a < 0x5000)
            {
                byte r = chipRam[chipAddr];
                if (autoInc) chipAddr = (chipAddr + 1) & 0x7F;
                return r;
            }
            if (a >= 0x5000 && a < 0x5800) return (byte)irqCounter;
            if (a >= 0x5800 && a < 0x6000) return (byte)(irqCounter >> 8);
            return base.ReadCpu(a);
        }

        public override void WriteCpu(int a, byte v)
        {
            if (a < 0x4800 || (a >= 0x6000 && a < 0x8000)) { base.WriteCpu(a, v); return; }
            if (a < 0x5000)
            {
                chipRam[chipAddr] = v;
                if (autoInc) chipAddr = (chipAddr + 1) & 0x7F;
            }
            else if (a < 0x5800) { irqCounter = (irqCounter & 0xFF00) | v; Irq = false; }
            else if (a < 0x6000) { irqCounter = (irqCounter & 0x00FF) | (v << 8); Irq = false; }
            else if (a < 0xC000) { chrReg[(a - 0x8000) >> 11] = v; UpdateChr(); }
            else if (a < 0xE000) { ntReg[(a - 0xC000) >> 11] = v; UpdateChr(); }
            else if (a < 0xE800) { SetPrg8(0, v & 0x3F); soundDisabled = (v & 0x40) != 0; }
            else if (a < 0xF000)
            {
                SetPrg8(1, v & 0x3F);
                ciramLo = (v & 0x40) == 0;
                ciramHi = (v & 0x80) == 0;
                UpdateChr();
            }
            else if (a < 0xF800) SetPrg8(2, v & 0x3F);
            else { chipAddr = v & 0x7F; autoInc = (v & 0x80) != 0; }
        }

        void UpdateChr()
        {
            for (int i = 0; i < 8; i++)
            {
                int v = chrReg[i];
                if (v >= 0xE0 && (i < 4 ? ciramLo : ciramHi))
                {
                    chrMem[i] = ciram;
                    chrOff[i] = (v & 1) * 0x400;
                    chrWritable[i] = true;
                }
                else SetChr1(i, v);
            }
            for (int i = 0; i < 4; i++)
            {
                int v = ntReg[i];
                if (v >= 0xE0) SetNtCiram(i, v & 1);
                else
                {
                    ntMem[i] = chr;
                    ntOff[i] = (v % ChrBanks1) * 0x400;
                    ntWritable[i] = chrIsRam;
                }
            }
        }

        public override void CpuClock()
        {
            if (++audioTick >= 15) { audioTick = 0; UpdateAudioChannel(); }

            if ((irqCounter & 0x8000) == 0 || (irqCounter & 0x7FFF) == 0x7FFF) return;
            irqCounter++;
            if ((irqCounter & 0x7FFF) == 0x7FFF) Irq = true;
        }
    }

    /// <summary>Sunsoft FME-7 / 5B, including the 5B's three square-wave channels.</summary>
    [Serializable]
    sealed class Fme7 : Mapper
    {
        int command, ramBank, irqCounter;
        bool ramSelect, ramEnable, irqEnable, counterEnable;

        // 5B audio
        int audioReg;
        readonly int[] toneRegs = new int[16];
        readonly int[] toneCounter = new int[3];
        readonly bool[] toneOut = new bool[3];
        int audioDivider;
        static readonly float[] Volume = BuildVolume();

        static float[] BuildVolume()
        {
            var t = new float[16];
            for (int i = 1; i < 16; i++) t[i] = (float)(0.07 * Math.Pow(10, (i - 15) * 3.0 / 20.0));
            return t;
        }

        protected override void Init()
        {
            NeedsCpuClock = true;
            HasAudio = true;
            SetPrg8(3, -1);
        }

        public override byte ReadCpu(int a)
        {
            if (a >= 0x6000 && a < 0x8000)
            {
                if (ramSelect) return ramEnable ? prgRam[a & 0x1FFF] : (byte)nes.OpenBus;
                return prg[(ramBank % PrgBanks8) * 0x2000 + (a & 0x1FFF)];
            }
            return base.ReadCpu(a);
        }

        public override void WriteCpu(int a, byte v)
        {
            if (a >= 0x6000 && a < 0x8000)
            {
                if (ramSelect && ramEnable) prgRam[a & 0x1FFF] = v;
                return;
            }
            base.WriteCpu(a, v);
        }

        protected override void WriteRegister(int a, byte v)
        {
            switch (a & 0xE000)
            {
                case 0x8000: command = v & 0x0F; break;
                case 0xA000: Command(v); break;
                case 0xC000: audioReg = v & 0x0F; break;
                case 0xE000: toneRegs[audioReg] = v; break;
            }
        }

        void Command(int v)
        {
            switch (command)
            {
                case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7:
                    SetChr1(command, v);
                    break;
                case 8:
                    ramBank = v & 0x3F;
                    ramSelect = (v & 0x40) != 0;
                    ramEnable = (v & 0x80) != 0;
                    break;
                case 9: case 10: case 11:
                    SetPrg8(command - 9, v & 0x3F);
                    break;
                case 12:
                    switch (v & 3)
                    {
                        case 0: SetMirroring(Mirroring.Vertical); break;
                        case 1: SetMirroring(Mirroring.Horizontal); break;
                        case 2: SetMirroring(Mirroring.SingleLow); break;
                        default: SetMirroring(Mirroring.SingleHigh); break;
                    }
                    break;
                case 13:
                    irqEnable = (v & 1) != 0;
                    counterEnable = (v & 0x80) != 0;
                    Irq = false;
                    break;
                case 14: irqCounter = (irqCounter & 0xFF00) | v; break;
                case 15: irqCounter = (irqCounter & 0x00FF) | (v << 8); break;
            }
        }

        public override void CpuClock()
        {
            if (counterEnable)
            {
                irqCounter = (irqCounter - 1) & 0xFFFF;
                if (irqCounter == 0xFFFF && irqEnable) Irq = true;
            }

            if (++audioDivider >= 16)
            {
                audioDivider = 0;
                for (int c = 0; c < 3; c++)
                {
                    int period = toneRegs[c * 2] | ((toneRegs[c * 2 + 1] & 0x0F) << 8);
                    if (++toneCounter[c] >= Math.Max(1, period))
                    {
                        toneCounter[c] = 0;
                        toneOut[c] = !toneOut[c];
                    }
                }
            }
        }

        public override float AudioOutput()
        {
            float s = 0;
            int mixer = toneRegs[7];
            for (int c = 0; c < 3; c++)
            {
                if ((mixer & (1 << c)) != 0) continue;   // tone disabled
                if (toneOut[c]) s += Volume[toneRegs[8 + c] & 0x0F];
            }
            return s;
        }
    }
}
