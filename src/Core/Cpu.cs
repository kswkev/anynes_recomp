using System;

namespace AnyNes.Core
{
    /// <summary>
    /// Cycle-stepped 6502 (2A03). Every bus access advances the rest of the machine by one CPU cycle,
    /// so dummy reads/writes are performed exactly as the hardware does them.
    /// </summary>
    [Serializable]
    public sealed class Cpu
    {
        const int C = 0x01, Z = 0x02, I = 0x04, D = 0x08, B = 0x10, U = 0x20, V = 0x40, N = 0x80;

        public int A, X, Y, S, P, PC;
        public long Cycles;
        public bool Jammed;

        readonly Nes nes;
        bool nmiLinePrev, needNmi, prevNeedNmi, runIrq, prevRunIrq;
        bool oamDmaPending;
        int oamDmaPage;

        public Cpu(Nes nes) { this.nes = nes; }

        public void Reset(bool hard)
        {
            if (hard) { A = X = Y = 0; S = 0; P = I | U; }
            // Reset performs three dummy stack reads.
            S = (S - 3) & 0xFF;
            P |= I;
            Jammed = false;
            PC = nes.Peek(0xFFFC) | (nes.Peek(0xFFFD) << 8);
            for (int i = 0; i < 7; i++) nes.Tick();
            Cycles += 7;
        }

        public void StartOamDma(int page) { oamDmaPending = true; oamDmaPage = page; }

        // ---------------------------------------------------------------- bus

        byte Read(int addr)
        {
            if (oamDmaPending) DoOamDma();
            if (nes.Apu.DmcNeedsFetch) DoDmcFetch();
            Cycles++;
            nes.Tick();
            byte v = nes.CpuRead(addr);
            EndCycle();
            return v;
        }

        void Write(int addr, int v)
        {
            Cycles++;
            nes.Tick();
            nes.CpuWrite(addr, (byte)v);
            EndCycle();
        }

        void DummyCycle()
        {
            Cycles++;
            nes.Tick();
            EndCycle();
        }

        void EndCycle()
        {
            prevNeedNmi = needNmi;
            bool line = nes.Ppu.NmiLine;
            if (line && !nmiLinePrev) needNmi = true;
            nmiLinePrev = line;
            prevRunIrq = runIrq;
            runIrq = nes.IrqLine && (P & I) == 0;
        }

        void DoOamDma()
        {
            oamDmaPending = false;
            DummyCycle();
            if ((Cycles & 1) != 0) DummyCycle();
            int baseAddr = oamDmaPage << 8;
            for (int i = 0; i < 256; i++)
            {
                Cycles++; nes.Tick();
                byte v = nes.CpuRead(baseAddr + i);
                EndCycle();
                Cycles++; nes.Tick();
                nes.CpuWrite(0x2004, v);
                EndCycle();
            }
        }

        void DoDmcFetch()
        {
            // Approximation of the DMC DMA stall (3-4 cycles).
            for (int i = 0; i < 3; i++) DummyCycle();
            Cycles++; nes.Tick();
            byte v = nes.CpuRead(nes.Apu.DmcFetchAddress);
            EndCycle();
            nes.Apu.DmcFetchComplete(v);
        }

        int ReadWord(int addr) { int lo = Read(addr); return lo | (Read(addr + 1) << 8); }
        void Push(int v) { Write(0x100 | S, v); S = (S - 1) & 0xFF; }
        int Pop() { S = (S + 1) & 0xFF; return Read(0x100 | S); }

        // ---------------------------------------------------------------- addressing

        int Fetch() { int v = Read(PC); PC = (PC + 1) & 0xFFFF; return v; }
        int FetchWord() { int lo = Fetch(); return lo | (Fetch() << 8); }

        int AddrZp() { return Fetch(); }
        int AddrZpX() { int a = Fetch(); Read(a); return (a + X) & 0xFF; }
        int AddrZpY() { int a = Fetch(); Read(a); return (a + Y) & 0xFF; }
        int AddrAbs() { return FetchWord(); }

        int AddrAbsIdx(int idx, bool write)
        {
            int b = FetchWord();
            int a = (b + idx) & 0xFFFF;
            if (write || ((a ^ b) & 0xFF00) != 0) Read((b & 0xFF00) | (a & 0xFF));
            return a;
        }

        int AddrIndX()
        {
            int z = Fetch();
            Read(z);
            z = (z + X) & 0xFF;
            int lo = Read(z);
            return lo | (Read((z + 1) & 0xFF) << 8);
        }

        int AddrIndY(bool write)
        {
            int z = Fetch();
            int lo = Read(z);
            int b = lo | (Read((z + 1) & 0xFF) << 8);
            int a = (b + Y) & 0xFFFF;
            if (write || ((a ^ b) & 0xFF00) != 0) Read((b & 0xFF00) | (a & 0xFF));
            return a;
        }

        // The "standard" 8 addressing modes encoded by bits 2-4 of most opcodes.
        int GroupAddr(int mode, bool write, bool useY)
        {
            switch (mode)
            {
                case 0: return AddrIndX();
                case 1: return AddrZp();
                case 3: return AddrAbs();
                case 4: return AddrIndY(write);
                case 5: return useY ? AddrZpY() : AddrZpX();
                case 6: return AddrAbsIdx(Y, write);
                default: return AddrAbsIdx(useY ? Y : X, write);
            }
        }

        // ---------------------------------------------------------------- ALU

        void SetZN(int v)
        {
            v &= 0xFF;
            P &= ~(Z | N);
            if (v == 0) P |= Z;
            P |= v & N;
        }

        void Ora(int v) { A |= v; SetZN(A); }
        void And(int v) { A &= v; SetZN(A); }
        void Eor(int v) { A ^= v; SetZN(A); }

        void Adc(int v)
        {
            int r = A + v + (P & C);
            P &= ~(C | V);
            if (r > 0xFF) P |= C;
            if (((A ^ r) & (v ^ r) & 0x80) != 0) P |= V;
            A = r & 0xFF;
            SetZN(A);
        }

        void Sbc(int v) { Adc(v ^ 0xFF); }

        void Compare(int reg, int v)
        {
            int r = reg - v;
            P &= ~C;
            if (r >= 0) P |= C;
            SetZN(r & 0xFF);
        }

        int Asl(int v) { P = (P & ~C) | (v >> 7); v = (v << 1) & 0xFF; SetZN(v); return v; }
        int Lsr(int v) { P = (P & ~C) | (v & 1); v >>= 1; SetZN(v); return v; }
        int Rol(int v) { int c = P & C; P = (P & ~C) | (v >> 7); v = ((v << 1) | c) & 0xFF; SetZN(v); return v; }
        int Ror(int v) { int c = P & C; P = (P & ~C) | (v & 1); v = (v >> 1) | (c << 7); SetZN(v); return v; }
        int Inc(int v) { v = (v + 1) & 0xFF; SetZN(v); return v; }
        int Dec(int v) { v = (v - 1) & 0xFF; SetZN(v); return v; }

        void Bit(int v)
        {
            P = (P & ~(Z | V | N)) | (v & (V | N));
            if ((A & v) == 0) P |= Z;
        }

        void Branch(bool cond)
        {
            int off = (sbyte)Fetch();
            if (!cond) return;
            // Taken branches delay interrupt polling when no page is crossed.
            Read(PC);
            int np = (PC + off) & 0xFFFF;
            if (((np ^ PC) & 0xFF00) != 0) Read((PC & 0xFF00) | (np & 0xFF));
            PC = np;
        }

        int Rmw(int addr, int op)
        {
            int v = Read(addr);
            Write(addr, v);
            switch (op)
            {
                case 0: v = Asl(v); break;
                case 1: v = Rol(v); break;
                case 2: v = Lsr(v); break;
                case 3: v = Ror(v); break;
                case 6: v = Dec(v); break;
                default: v = Inc(v); break;
            }
            Write(addr, v);
            return v;
        }

        // ---------------------------------------------------------------- interrupts

        void Interrupt(bool brk)
        {
            if (!brk) { Read(PC); Read(PC); }
            Push(PC >> 8);
            Push(PC & 0xFF);
            bool nmi = needNmi;
            if (nmi) needNmi = false;
            Push(brk ? (P | B | U) : ((P | U) & ~B));
            P |= I;
            PC = ReadWord(nmi ? 0xFFFA : 0xFFFE);
            // An NMI arriving during the last cycles is delayed by one instruction.
            prevNeedNmi = false;
        }

        // ---------------------------------------------------------------- execution

        public void Step()
        {
            if (prevNeedNmi || prevRunIrq)
            {
                Interrupt(false);
                return;
            }
            if (Jammed) { DummyCycle(); return; }

            int op = Fetch();
            int cc = op & 3, mode = (op >> 2) & 7, aaa = op >> 5;

            if (cc == 1)
            {
                // ORA AND EOR ADC STA LDA CMP SBC
                if (aaa == 4)
                {
                    if (mode == 2) { Fetch(); return; } // $89 NOP #imm
                    Write(GroupAddr(mode, true, false), A);
                    return;
                }
                int v = mode == 2 ? Fetch() : Read(GroupAddr(mode, false, false));
                Alu(aaa, v);
                return;
            }

            if (cc == 3)
            {
                ExecIllegal(op, mode, aaa);
                return;
            }

            switch (op)
            {
                // ---- cc == 0
                case 0x00: Fetch(); Interrupt(true); break;
                case 0x20: { int lo = Fetch(); Read(0x100 | S); Push(PC >> 8); Push(PC & 0xFF); PC = lo | (Fetch() << 8); break; }
                case 0x40: Fetch(); Read(0x100 | S); P = (Pop() & ~B) | U; PC = Pop(); PC |= Pop() << 8; break;
                case 0x60: Fetch(); Read(0x100 | S); PC = Pop(); PC |= Pop() << 8; Fetch(); break;
                case 0x08: Read(PC); Push(P | B | U); break;
                case 0x28: Read(PC); Read(0x100 | S); P = (Pop() & ~B) | U; break;
                case 0x48: Read(PC); Push(A); break;
                case 0x68: Read(PC); Read(0x100 | S); A = Pop(); SetZN(A); break;
                case 0x4C: PC = FetchWord(); break;
                case 0x6C:
                    {
                        int ptr = FetchWord();
                        int lo = Read(ptr);
                        PC = lo | (Read((ptr & 0xFF00) | ((ptr + 1) & 0xFF)) << 8);
                        break;
                    }
                case 0x24: Bit(Read(AddrZp())); break;
                case 0x2C: Bit(Read(AddrAbs())); break;
                case 0x84: Write(AddrZp(), Y); break;
                case 0x94: Write(AddrZpX(), Y); break;
                case 0x8C: Write(AddrAbs(), Y); break;
                case 0x9C: { int a = AddrAbsIdx(X, true); Shx(a, Y); break; } // SHY
                case 0xA0: Y = Fetch(); SetZN(Y); break;
                case 0xA4: Y = Read(AddrZp()); SetZN(Y); break;
                case 0xB4: Y = Read(AddrZpX()); SetZN(Y); break;
                case 0xAC: Y = Read(AddrAbs()); SetZN(Y); break;
                case 0xBC: Y = Read(AddrAbsIdx(X, false)); SetZN(Y); break;
                case 0xC0: Compare(Y, Fetch()); break;
                case 0xC4: Compare(Y, Read(AddrZp())); break;
                case 0xCC: Compare(Y, Read(AddrAbs())); break;
                case 0xE0: Compare(X, Fetch()); break;
                case 0xE4: Compare(X, Read(AddrZp())); break;
                case 0xEC: Compare(X, Read(AddrAbs())); break;
                case 0x88: Read(PC); Y = (Y - 1) & 0xFF; SetZN(Y); break;
                case 0xA8: Read(PC); Y = A; SetZN(Y); break;
                case 0xC8: Read(PC); Y = (Y + 1) & 0xFF; SetZN(Y); break;
                case 0xE8: Read(PC); X = (X + 1) & 0xFF; SetZN(X); break;
                case 0x98: Read(PC); A = Y; SetZN(A); break;
                case 0x18: Read(PC); P &= ~C; break;
                case 0x38: Read(PC); P |= C; break;
                case 0x58: Read(PC); P &= ~I; break;
                case 0x78: Read(PC); P |= I; break;
                case 0xB8: Read(PC); P &= ~V; break;
                case 0xD8: Read(PC); P &= ~D; break;
                case 0xF8: Read(PC); P |= D; break;
                case 0x10: Branch((P & N) == 0); break;
                case 0x30: Branch((P & N) != 0); break;
                case 0x50: Branch((P & V) == 0); break;
                case 0x70: Branch((P & V) != 0); break;
                case 0x90: Branch((P & C) == 0); break;
                case 0xB0: Branch((P & C) != 0); break;
                case 0xD0: Branch((P & Z) == 0); break;
                case 0xF0: Branch((P & Z) != 0); break;
                // unofficial NOPs in the cc==0 column
                case 0x80: Fetch(); break;
                case 0x04: case 0x44: case 0x64: Read(AddrZp()); break;
                case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: Read(AddrZpX()); break;
                case 0x0C: Read(AddrAbs()); break;
                case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: Read(AddrAbsIdx(X, false)); break;

                // ---- cc == 2
                case 0x0A: Read(PC); A = Asl(A); break;
                case 0x2A: Read(PC); A = Rol(A); break;
                case 0x4A: Read(PC); A = Lsr(A); break;
                case 0x6A: Read(PC); A = Ror(A); break;
                case 0x8A: Read(PC); A = X; SetZN(A); break;
                case 0xAA: Read(PC); X = A; SetZN(X); break;
                case 0xCA: Read(PC); X = (X - 1) & 0xFF; SetZN(X); break;
                case 0xEA: case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: Read(PC); break;
                case 0x9A: Read(PC); S = X; break;
                case 0xBA: Read(PC); X = S; SetZN(X); break;
                case 0xA2: X = Fetch(); SetZN(X); break;
                case 0x82: case 0xC2: case 0xE2: Fetch(); break;
                case 0x86: Write(AddrZp(), X); break;
                case 0x96: Write(AddrZpY(), X); break;
                case 0x8E: Write(AddrAbs(), X); break;
                case 0x9E: { int a = AddrAbsIdx(Y, true); Shx(a, X); break; } // SHX
                case 0xA6: X = Read(AddrZp()); SetZN(X); break;
                case 0xB6: X = Read(AddrZpY()); SetZN(X); break;
                case 0xAE: X = Read(AddrAbs()); SetZN(X); break;
                case 0xBE: X = Read(AddrAbsIdx(Y, false)); SetZN(X); break;
                case 0x02: case 0x12: case 0x22: case 0x32: case 0x42: case 0x52:
                case 0x62: case 0x72: case 0x92: case 0xB2: case 0xD2: case 0xF2:
                    Jammed = true; break;
                default:
                    // Remaining cc==2 opcodes are the shift/inc/dec read-modify-write family.
                    {
                        int a;
                        switch (mode)
                        {
                            case 1: a = AddrZp(); break;
                            case 3: a = AddrAbs(); break;
                            case 5: a = AddrZpX(); break;
                            default: a = AddrAbsIdx(X, true); break;
                        }
                        Rmw(a, aaa);
                        break;
                    }
            }
        }

        // SHX/SHY (approximation): store reg & (high address byte + 1).
        void Shx(int addr, int reg)
        {
            Write(addr, reg & ((addr >> 8) + 1) & 0xFF);
        }

        void Alu(int aaa, int v)
        {
            switch (aaa)
            {
                case 0: Ora(v); break;
                case 1: And(v); break;
                case 2: Eor(v); break;
                case 3: Adc(v); break;
                case 5: A = v; SetZN(A); break;
                case 6: Compare(A, v); break;
                case 7: Sbc(v); break;
            }
        }

        void ExecIllegal(int op, int mode, int aaa)
        {
            if (mode == 2)
            {
                int v = Fetch();
                switch (op)
                {
                    case 0x0B: case 0x2B: And(v); P = (P & ~C) | ((A >> 7) & 1); break;          // ANC
                    case 0x4B: And(v); A = Lsr(A); break;                                         // ALR
                    case 0x6B:                                                                    // ARR
                        And(v);
                        A = ((A >> 1) | ((P & C) << 7)) & 0xFF;
                        SetZN(A);
                        P = (P & ~(C | V)) | ((A >> 6) & 1) | (((A >> 6) ^ (A >> 5)) & 1) << 6;
                        break;
                    case 0x8B: A = (A | 0xEE) & X & v; SetZN(A); break;                          // XAA
                    case 0xAB: A = X = (A | 0xEE) & v; SetZN(A); break;                          // LXA
                    case 0xCB: { int r = (A & X) - v; X = r & 0xFF; P = (P & ~C) | (r >= 0 ? C : 0); SetZN(X); break; } // AXS
                    default: Sbc(v); break;                                                       // $EB SBC
                }
                return;
            }

            if (aaa == 4)
            {
                switch (op)
                {
                    case 0x93: { int a = AddrIndY(true); Write(a, A & X & ((a >> 8) + 1)); break; }      // AHX
                    case 0x9B: { int a = AddrAbsIdx(Y, true); S = A & X; Write(a, S & ((a >> 8) + 1)); break; } // TAS
                    case 0x9F: { int a = AddrAbsIdx(Y, true); Write(a, A & X & ((a >> 8) + 1)); break; } // AHX
                    default: Write(GroupAddr(mode, true, true), A & X); break;                       // SAX
                }
                return;
            }

            if (aaa == 5)
            {
                if (op == 0xBB) { int v = Read(AddrAbsIdx(Y, false)) & S; A = X = S = v; SetZN(v); return; } // LAS
                int lv = Read(GroupAddr(mode, false, true));                                          // LAX
                A = X = lv;
                SetZN(lv);
                return;
            }

            // SLO RLA SRE RRA DCP ISC: RMW then ALU.
            int addr = GroupAddr(mode, true, false);
            int rop;
            switch (aaa)
            {
                case 0: rop = 0; break;
                case 1: rop = 1; break;
                case 2: rop = 2; break;
                case 3: rop = 3; break;
                case 6: rop = 6; break;
                default: rop = 7; break;
            }
            int carryIn = P & C;
            int r2 = Read(addr);
            Write(addr, r2);
            switch (rop)
            {
                case 0: P = (P & ~C) | (r2 >> 7); r2 = (r2 << 1) & 0xFF; Write(addr, r2); Ora(r2); break;
                case 1: P = (P & ~C) | (r2 >> 7); r2 = ((r2 << 1) | carryIn) & 0xFF; Write(addr, r2); And(r2); break;
                case 2: P = (P & ~C) | (r2 & 1); r2 >>= 1; Write(addr, r2); Eor(r2); break;
                case 3: P = (P & ~C) | (r2 & 1); r2 = (r2 >> 1) | (carryIn << 7); Write(addr, r2); Adc(r2); break;
                case 6: r2 = (r2 - 1) & 0xFF; Write(addr, r2); Compare(A, r2); break;
                default: r2 = (r2 + 1) & 0xFF; Write(addr, r2); Sbc(r2); break;
            }
        }
    }
}
