using System;

namespace AnyNes.Core
{
    [Serializable]
    public sealed class Apu
    {
        public const int SampleRate = 44100;

        static readonly byte[] LengthTable =
        {
            10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
        };
        static readonly byte[][] DutyTable =
        {
            new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 },
            new byte[] { 0, 1, 1, 0, 0, 0, 0, 0 },
            new byte[] { 0, 1, 1, 1, 1, 0, 0, 0 },
            new byte[] { 1, 0, 0, 1, 1, 1, 1, 1 },
        };
        static readonly byte[] TriTable =
        {
            15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        };
        static readonly int[] NoisePeriodsNtsc = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        static readonly int[] NoisePeriodsPal = { 4, 8, 14, 30, 60, 88, 118, 148, 188, 236, 354, 472, 708, 944, 1890, 3778 };
        static readonly int[] DmcRatesNtsc = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54 };
        static readonly int[] DmcRatesPal = { 398, 354, 316, 298, 276, 236, 210, 198, 176, 148, 132, 118, 98, 78, 66, 50 };

        // Frame sequencer step points in CPU cycles: quarter 1, half 1, quarter 2, IRQ/half 2 (three
        // cycles around the 4-step end), and the 5-step mode's last step + wrap.
        static readonly int[] FrameStepsNtsc = { 7457, 14913, 22371, 29828, 29829, 29830, 37281, 37282 };
        static readonly int[] FrameStepsPal = { 8313, 16627, 24939, 33252, 33253, 33254, 41565, 41566 };
        internal static readonly float[] PulseMix = BuildPulseMix();
        static readonly float[] TndMix = BuildTndMix();

        static float[] BuildPulseMix()
        {
            var t = new float[31];
            for (int i = 1; i < 31; i++) t[i] = (float)(95.52 / (8128.0 / i + 100));
            return t;
        }

        static float[] BuildTndMix()
        {
            var t = new float[203];
            for (int i = 1; i < 203; i++) t[i] = (float)(163.67 / (24329.0 / i + 100));
            return t;
        }

        // ------------------------------------------------------------ channels

        [Serializable]
        internal sealed class Envelope
        {
            public bool Start, Loop, Constant;
            public int Volume, Divider, Decay;

            public void Clock()
            {
                if (Start) { Start = false; Decay = 15; Divider = Volume; return; }
                if (Divider > 0) { Divider--; return; }
                Divider = Volume;
                if (Decay > 0) Decay--;
                else if (Loop) Decay = 15;
            }

            public int Output { get { return Constant ? Volume : Decay; } }
        }

        /// <summary>2A03 pulse channel; also used for the MMC5's pulses (no sweep unit).</summary>
        [Serializable]
        internal sealed class Pulse
        {
            public readonly Envelope Env = new Envelope();
            public readonly bool Second, HasSweep;
            public bool Enabled;
            public int Duty, Step, Timer, Period, Length;
            public bool SweepEnabled, SweepNegate, SweepReload;
            public int SweepPeriod, SweepShift, SweepDivider;

            public Pulse(bool second) : this(second, true) { }

            public Pulse(bool second, bool hasSweep)
            {
                Second = second;
                HasSweep = hasSweep;
            }

            public void Write(int reg, int v)
            {
                switch (reg)
                {
                    case 0:
                        Duty = v >> 6;
                        Env.Loop = (v & 0x20) != 0;
                        Env.Constant = (v & 0x10) != 0;
                        Env.Volume = v & 15;
                        break;
                    case 1:
                        SweepEnabled = (v & 0x80) != 0;
                        SweepPeriod = (v >> 4) & 7;
                        SweepNegate = (v & 8) != 0;
                        SweepShift = v & 7;
                        SweepReload = true;
                        break;
                    case 2: Period = (Period & 0x700) | v; break;
                    case 3:
                        Period = (Period & 0xFF) | ((v & 7) << 8);
                        if (Enabled) Length = LengthTable[v >> 3];
                        Step = 0;
                        Env.Start = true;
                        break;
                }
            }

            public void ClockTimer()
            {
                if (Timer == 0) { Timer = Period; Step = (Step + 1) & 7; }
                else Timer--;
            }

            int Target()
            {
                int change = Period >> SweepShift;
                if (!SweepNegate) return Period + change;
                return Period - change - (Second ? 0 : 1);
            }

            bool Muted { get { return HasSweep && (Period < 8 || (!SweepNegate && Target() > 0x7FF)); } }

            public void ClockHalf()
            {
                if (!Env.Loop && Length > 0) Length--;
                if (!HasSweep) return;
                if (SweepDivider == 0 && SweepEnabled && SweepShift > 0 && !Muted)
                {
                    int tg = Target();
                    if (tg >= 0) Period = tg;
                }
                if (SweepDivider == 0 || SweepReload) { SweepDivider = SweepPeriod; SweepReload = false; }
                else SweepDivider--;
            }

            public int Output
            {
                get
                {
                    if (Length == 0 || Muted || DutyTable[Duty][Step] == 0) return 0;
                    return Env.Output;
                }
            }
        }

        [Serializable]
        sealed class Triangle
        {
            public bool Enabled, Control, ReloadFlag;
            public int LinearReload, Linear, Period, Timer, Step, Length;

            public void ClockTimer()
            {
                if (Timer == 0)
                {
                    Timer = Period;
                    if (Length > 0 && Linear > 0 && Period > 1) Step = (Step + 1) & 31;
                }
                else Timer--;
            }

            public void ClockQuarter()
            {
                if (ReloadFlag) Linear = LinearReload;
                else if (Linear > 0) Linear--;
                if (!Control) ReloadFlag = false;
            }

            public void ClockHalf() { if (!Control && Length > 0) Length--; }

            public int Output { get { return TriTable[Step]; } }
        }

        [Serializable]
        sealed class Noise
        {
            public readonly Envelope Env = new Envelope();
            public bool Enabled, Mode;
            public int Period = 4, Timer, Length, Shift = 1;

            public void ClockTimer()
            {
                if (Timer > 0) { Timer--; return; }
                Timer = Period - 1;
                int fb = (Shift & 1) ^ ((Shift >> (Mode ? 6 : 1)) & 1);
                Shift = (Shift >> 1) | (fb << 14);
            }

            public void ClockHalf() { if (!Env.Loop && Length > 0) Length--; }

            public int Output { get { return (Length == 0 || (Shift & 1) != 0) ? 0 : Env.Output; } }
        }

        [Serializable]
        sealed class Dmc
        {
            public bool IrqEnabled, Loop, Irq;
            public int Rate = 428, Timer, Output, SampleAddr = 0xC000, SampleLen = 1;
            public int CurAddr, Remaining, Buffer, ShiftReg, Bits = 8;
            public bool BufferEmpty = true, Silence = true;

            public void ClockTimer()
            {
                if (Timer > 0) { Timer--; return; }
                Timer = Rate - 1;
                if (!Silence)
                {
                    if ((ShiftReg & 1) != 0) { if (Output <= 125) Output += 2; }
                    else if (Output >= 2) Output -= 2;
                }
                ShiftReg >>= 1;
                if (--Bits == 0)
                {
                    Bits = 8;
                    if (BufferEmpty) Silence = true;
                    else { Silence = false; ShiftReg = Buffer; BufferEmpty = true; }
                }
            }

            public void Restart() { CurAddr = SampleAddr; Remaining = SampleLen; }
        }

        readonly Pulse p1 = new Pulse(false), p2 = new Pulse(true);
        readonly Triangle tri = new Triangle();
        readonly Noise noise = new Noise();
        readonly Dmc dmc = new Dmc();
        readonly Nes nes;

        int fcCycle, fcResetDelay = -1;
        bool fcFiveStep, irqInhibit;
        public bool FrameIrq;
        bool evenCycle;

        readonly int[] noisePeriods, dmcRates, frameSteps;

        // resampling / filtering
        double sampleClock;
        readonly double cyclesPerSample;
        float accum;
        int accumCount;
        float hp1Prev, hp1Out, hp2Prev, hp2Out, lpOut;
        readonly short[] samples = new short[4096];
        int sampleCount;

        public Apu(Nes nes)
        {
            this.nes = nes;
            bool pal = nes.Region == Region.Pal;
            noisePeriods = pal ? NoisePeriodsPal : NoisePeriodsNtsc;
            dmcRates = pal ? DmcRatesPal : DmcRatesNtsc;
            frameSteps = pal ? FrameStepsPal : FrameStepsNtsc;
            cyclesPerSample = nes.CpuClockHz / SampleRate;
        }

        public bool DmcIrq { get { return dmc.Irq; } }
        public bool DmcNeedsFetch { get { return dmc.BufferEmpty && dmc.Remaining > 0; } }
        public int DmcFetchAddress { get { return dmc.CurAddr; } }

        public void DmcFetchComplete(byte v)
        {
            dmc.Buffer = v;
            dmc.BufferEmpty = false;
            dmc.CurAddr = dmc.CurAddr == 0xFFFF ? 0x8000 : dmc.CurAddr + 1;
            if (--dmc.Remaining == 0)
            {
                if (dmc.Loop) dmc.Restart();
                else if (dmc.IrqEnabled) dmc.Irq = true;
            }
        }

        public void Reset()
        {
            WriteRegister(0x4015, 0);
            WriteRegister(0x4017, 0);
            fcCycle = 0;
        }

        public void WriteRegister(int addr, int v)
        {
            switch (addr)
            {
                case 0x4000: case 0x4001: case 0x4002: case 0x4003: p1.Write(addr & 3, v); break;
                case 0x4004: case 0x4005: case 0x4006: case 0x4007: p2.Write(addr & 3, v); break;
                case 0x4008: tri.Control = (v & 0x80) != 0; tri.LinearReload = v & 0x7F; break;
                case 0x400A: tri.Period = (tri.Period & 0x700) | v; break;
                case 0x400B:
                    tri.Period = (tri.Period & 0xFF) | ((v & 7) << 8);
                    if (tri.Enabled) tri.Length = LengthTable[v >> 3];
                    tri.ReloadFlag = true;
                    break;
                case 0x400C:
                    noise.Env.Loop = (v & 0x20) != 0;
                    noise.Env.Constant = (v & 0x10) != 0;
                    noise.Env.Volume = v & 15;
                    break;
                case 0x400E: noise.Mode = (v & 0x80) != 0; noise.Period = noisePeriods[v & 15]; break;
                case 0x400F:
                    if (noise.Enabled) noise.Length = LengthTable[v >> 3];
                    noise.Env.Start = true;
                    break;
                case 0x4010:
                    dmc.IrqEnabled = (v & 0x80) != 0;
                    dmc.Loop = (v & 0x40) != 0;
                    dmc.Rate = dmcRates[v & 15];
                    if (!dmc.IrqEnabled) dmc.Irq = false;
                    break;
                case 0x4011: dmc.Output = v & 0x7F; break;
                case 0x4012: dmc.SampleAddr = 0xC000 + v * 64; break;
                case 0x4013: dmc.SampleLen = v * 16 + 1; break;
                case 0x4015:
                    p1.Enabled = (v & 1) != 0; if (!p1.Enabled) p1.Length = 0;
                    p2.Enabled = (v & 2) != 0; if (!p2.Enabled) p2.Length = 0;
                    tri.Enabled = (v & 4) != 0; if (!tri.Enabled) tri.Length = 0;
                    noise.Enabled = (v & 8) != 0; if (!noise.Enabled) noise.Length = 0;
                    dmc.Irq = false;
                    if ((v & 0x10) == 0) dmc.Remaining = 0;
                    else if (dmc.Remaining == 0) dmc.Restart();
                    break;
                case 0x4017:
                    fcFiveStep = (v & 0x80) != 0;
                    irqInhibit = (v & 0x40) != 0;
                    if (irqInhibit) FrameIrq = false;
                    fcResetDelay = evenCycle ? 3 : 4;
                    break;
            }
        }

        public int ReadStatus()
        {
            int r = 0;
            if (p1.Length > 0) r |= 1;
            if (p2.Length > 0) r |= 2;
            if (tri.Length > 0) r |= 4;
            if (noise.Length > 0) r |= 8;
            if (dmc.Remaining > 0) r |= 0x10;
            if (FrameIrq) r |= 0x40;
            if (dmc.Irq) r |= 0x80;
            FrameIrq = false;
            return r;
        }

        void QuarterFrame()
        {
            p1.Env.Clock(); p2.Env.Clock(); noise.Env.Clock();
            tri.ClockQuarter();
        }

        void HalfFrame()
        {
            p1.ClockHalf(); p2.ClockHalf(); tri.ClockHalf(); noise.ClockHalf();
        }

        /// <summary>Advances one CPU cycle.</summary>
        public void Clock()
        {
            evenCycle = !evenCycle;

            if (fcResetDelay >= 0 && --fcResetDelay < 0)
            {
                fcCycle = 0;
                if (fcFiveStep) { QuarterFrame(); HalfFrame(); }
            }

            fcCycle++;
            int[] fs = frameSteps;
            if (fcCycle == fs[0] || fcCycle == fs[2]) QuarterFrame();
            else if (fcCycle == fs[1]) { QuarterFrame(); HalfFrame(); }
            else if (!fcFiveStep)
            {
                if (fcCycle == fs[3]) { if (!irqInhibit) FrameIrq = true; }
                else if (fcCycle == fs[4]) { QuarterFrame(); HalfFrame(); if (!irqInhibit) FrameIrq = true; }
                else if (fcCycle == fs[5]) { if (!irqInhibit) FrameIrq = true; fcCycle = 0; }
            }
            else
            {
                if (fcCycle == fs[6]) { QuarterFrame(); HalfFrame(); }
                else if (fcCycle == fs[7]) fcCycle = 0;
            }

            if (evenCycle) { p1.ClockTimer(); p2.ClockTimer(); }
            tri.ClockTimer();
            noise.ClockTimer();
            dmc.ClockTimer();

            float s = PulseMix[p1.Output + p2.Output] + TndMix[3 * tri.Output + 2 * noise.Output + dmc.Output];
            if (nes.Mapper.HasAudio) s += nes.Mapper.AudioOutput();
            accum += s;
            accumCount++;

            sampleClock += 1.0;
            if (sampleClock >= cyclesPerSample)
            {
                sampleClock -= cyclesPerSample;
                EmitSample(accum / accumCount);
                accum = 0;
                accumCount = 0;
            }
        }

        void EmitSample(float x)
        {
            // NES output stage: two high-pass filters (90 Hz, 440 Hz) and a 14 kHz low-pass.
            const float hpA1 = 0.98736f;   // exp(-2*pi*90/44100)
            const float hpA2 = 0.93926f;   // exp(-2*pi*440/44100)
            const float lpA = 0.86825f;    // 1-exp(-2*pi*14000/44100)
            hp1Out = hpA1 * (hp1Out + x - hp1Prev); hp1Prev = x;
            hp2Out = hpA2 * (hp2Out + hp1Out - hp2Prev); hp2Prev = hp1Out;
            lpOut += lpA * (hp2Out - lpOut);
            int v = (int)(lpOut * 60000f);
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            if (sampleCount < samples.Length) samples[sampleCount++] = (short)v;
        }

        /// <summary>Copies out the samples produced since the last call.</summary>
        public int TakeSamples(short[] dest)
        {
            int n = Math.Min(sampleCount, dest.Length);
            Array.Copy(samples, dest, n);
            sampleCount = 0;
            return n;
        }
    }
}
