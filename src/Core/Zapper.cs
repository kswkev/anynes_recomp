using System;

namespace AnyNes.Core
{
    /// <summary>
    /// NES Zapper light gun in controller port 2. Reads of $4017 report D4 = trigger held and
    /// D3 = 0 when the photodiode sees light, i.e. a bright pixel near the aim point was drawn by the
    /// PPU within the last few scanlines (the TV phosphor stays lit only briefly).
    /// </summary>
    [Serializable]
    public sealed class Zapper
    {
        const int DecayLines = 26;
        const int Radius = 3;

        public bool Enabled;
        /// <summary>Aim point in NES pixels (0-255, 0-239); negative when pointing off screen.</summary>
        public int X = -1, Y = -1;
        public bool Trigger;

        /// <summary>Known games that need the Zapper (CRC32 of PRG+CHR).</summary>
        public static bool IsZapperGame(uint crc)
        {
            switch (crc)
            {
                case 0x24598791: // Duck Hunt
                case 0xFF24D794: // Hogan's Alley
                case 0x5112DC21: // Wild Gunman
                case 0xBEB8AB01: // Gumshoe
                case 0x851EB9BE: // Shooting Range
                case 0x4318A2F8: // Barker Bill's Trick Shooting
                case 0x3E58A87E: // Freedom Force
                case 0x5EE6008E: // Mechanized Attack
                case 0xDE8FD935: // To the Earth
                case 0xB79F2651: // Chiller
                case 0xBBE40DC4: // Baby Boomer
                case 0xD26EFD78: // Super Mario Bros. + Duck Hunt
                case 0x60E63537: // Super Mario Bros. + Duck Hunt + World Class Track Meet
                    return true;
                default:
                    return false;
            }
        }

        public int Read(Ppu ppu)
        {
            int r = Trigger ? 0x10 : 0;
            if (!SeesLight(ppu)) r |= 0x08;
            return r;
        }

        bool SeesLight(Ppu ppu)
        {
            if (X < 0 || Y < 0 || X >= Ppu.Width || Y >= Ppu.Height) return false;
            int line = ppu.Scanline, dot = ppu.Dot;
            if (line == 261) return false;      // pre-render: nothing drawn yet this frame
            int[] frame = ppu.Frame;
            for (int y = Math.Max(0, Y - Radius); y <= Math.Min(Ppu.Height - 1, Y + Radius); y++)
            {
                // Only pixels the beam has already passed this frame, and recently enough to still glow.
                if (y > line) break;
                if (line - y > DecayLines) continue;
                for (int x = Math.Max(0, X - Radius); x <= Math.Min(Ppu.Width - 1, X + Radius); x++)
                {
                    if (y == line && x >= dot - 1) break;
                    int c = frame[y * Ppu.Width + x];
                    int luma = (((c >> 16) & 0xFF) * 299 + ((c >> 8) & 0xFF) * 587 + (c & 0xFF) * 114) / 1000;
                    if (luma >= 160) return true;
                }
            }
            return false;
        }
    }
}
