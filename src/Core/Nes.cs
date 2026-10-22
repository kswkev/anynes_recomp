using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace AnyNes.Core
{
    [Flags]
    public enum Buttons
    {
        None = 0, A = 1, B = 2, Select = 4, Start = 8, Up = 16, Down = 32, Left = 64, Right = 128
    }

    /// <summary>The whole console: CPU/PPU/APU, work RAM, controller ports and the cartridge mapper.</summary>
    [Serializable]
    public sealed class Nes
    {
        public readonly Cpu Cpu;
        public readonly Ppu Ppu;
        public readonly Apu Apu;
        public readonly Mapper Mapper;
        public readonly RomImage Rom;
        public readonly byte[] Ram = new byte[0x800];
        public int OpenBus;

        /// <summary>Live controller state for ports 1 and 2, set by the frontend.</summary>
        public readonly Buttons[] Input = new Buttons[2];

        /// <summary>Light gun; when enabled it replaces the controller in port 2.</summary>
        public readonly Zapper Zapper = new Zapper();
        readonly int[] shift = new int[2];
        bool strobe;

        public Nes(RomImage rom)
        {
            Rom = rom;
            Mapper = MapperFactory.Create(rom);
            Cpu = new Cpu(this);
            Ppu = new Ppu(this);
            Apu = new Apu(this);
            Mapper.Attach(this, rom);
            Apu.Reset();
            Cpu.Reset(true);
        }

        public void SoftReset()
        {
            Mapper.Reset();
            Ppu.Reset();
            Apu.Reset();
            Cpu.Reset(false);
        }

        public bool IrqLine { get { return Mapper.Irq || Apu.FrameIrq || Apu.DmcIrq; } }

        public void RunFrame()
        {
            Ppu.FrameReady = false;
            // A jammed CPU still ticks, so this always terminates.
            while (!Ppu.FrameReady) Cpu.Step();
        }

        /// <summary>One CPU cycle worth of PPU/APU/mapper time.</summary>
        public void Tick()
        {
            Ppu.Step();
            Ppu.Step();
            Ppu.Step();
            Apu.Clock();
            if (Mapper.NeedsCpuClock) Mapper.CpuClock();
        }

        public byte Peek(int addr)
        {
            if (addr >= 0x6000) return Mapper.ReadCpu(addr);
            if (addr < 0x2000) return Ram[addr & 0x7FF];
            return 0;
        }

        public byte CpuRead(int addr)
        {
            int v;
            if (addr < 0x2000) v = Ram[addr & 0x7FF];
            else if (addr < 0x4000) v = Ppu.ReadRegister(addr);
            else if (addr < 0x4020)
            {
                switch (addr)
                {
                    case 0x4015: v = Apu.ReadStatus() | (OpenBus & 0x20); break;
                    case 0x4016:
                    case 0x4017:
                        {
                            if (addr == 0x4017 && Zapper.Enabled)
                            {
                                v = Zapper.Read(Ppu) | (OpenBus & 0xE0);
                                break;
                            }
                            int p = addr & 1;
                            if (strobe) shift[p] = (int)Input[p];
                            v = (shift[p] & 1) | (OpenBus & 0xE0);
                            shift[p] = (shift[p] >> 1) | 0x80;
                            break;
                        }
                    default: v = OpenBus; break;
                }
            }
            else v = Mapper.ReadCpu(addr);
            OpenBus = v;
            return (byte)v;
        }

        public void CpuWrite(int addr, byte v)
        {
            OpenBus = v;
            if (addr < 0x2000) { Ram[addr & 0x7FF] = v; return; }
            if (addr < 0x4000)
            {
                Ppu.WriteRegister(addr, v);
                if (Mapper.SnoopPpuRegs) Mapper.OnPpuRegWrite(addr & 7, v);
                return;
            }
            if (addr < 0x4020)
            {
                if (addr == 0x4014) Cpu.StartOamDma(v);
                else if (addr == 0x4016)
                {
                    strobe = (v & 1) != 0;
                    if (strobe) { shift[0] = (int)Input[0]; shift[1] = (int)Input[1]; }
                }
                else Apu.WriteRegister(addr, v);
                return;
            }
            Mapper.WriteCpu(addr, v);
        }

        // ---------------------------------------------------------------- save states

        public byte[] SaveState()
        {
            using (var ms = new MemoryStream())
            {
                new BinaryFormatter().Serialize(ms, this);
                return ms.ToArray();
            }
        }

        public static Nes LoadState(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return (Nes)new BinaryFormatter().Deserialize(ms);
        }
    }
}
