using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AnyNes.Core;

namespace AnyNes.Player
{
    /// <summary>Keyboard bindings for both controller ports, persisted to an ini file.</summary>
    sealed class KeyMap
    {
        public static readonly Buttons[] Order =
        {
            Buttons.Up, Buttons.Down, Buttons.Left, Buttons.Right, Buttons.B, Buttons.A, Buttons.Select, Buttons.Start
        };

        public readonly Keys[,] Keys = new Keys[2, 8];

        public KeyMap() { SetDefaults(); }

        public void SetDefaults()
        {
            var p1 = new[] { System.Windows.Forms.Keys.Up, System.Windows.Forms.Keys.Down, System.Windows.Forms.Keys.Left, System.Windows.Forms.Keys.Right,
                             System.Windows.Forms.Keys.Z, System.Windows.Forms.Keys.X, System.Windows.Forms.Keys.RShiftKey, System.Windows.Forms.Keys.Return };
            var p2 = new[] { System.Windows.Forms.Keys.I, System.Windows.Forms.Keys.K, System.Windows.Forms.Keys.J, System.Windows.Forms.Keys.L,
                             System.Windows.Forms.Keys.U, System.Windows.Forms.Keys.O, System.Windows.Forms.Keys.Y, System.Windows.Forms.Keys.P };
            for (int i = 0; i < 8; i++) { Keys[0, i] = p1[i]; Keys[1, i] = p2[i]; }
        }

        public void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    for (int p = 0; p < 2; p++)
                        for (int i = 0; i < 8; i++)
                            if (k == "P" + (p + 1) + "." + Order[i])
                            {
                                Keys parsed;
                                if (Enum.TryParse(v, out parsed)) Keys[p, i] = parsed;
                            }
                }
            }
            catch (Exception) { }
        }

        public void Save(string path)
        {
            try
            {
                var lines = new List<string>();
                for (int p = 0; p < 2; p++)
                    for (int i = 0; i < 8; i++)
                        lines.Add("P" + (p + 1) + "." + Order[i] + "=" + Keys[p, i]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Polls the keyboard (GetAsyncKeyState) and XInput game pads.</summary>
    static class InputPoller
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        [StructLayout(LayoutKind.Sequential)]
        struct XInputState
        {
            public int PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState14(int index, out XInputState state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState910(int index, out XInputState state);

        static int xinputApi;               // 0 = untested, 1 = 1.4, 2 = 9.1.0, -1 = unavailable
        static readonly int[] retryAt = new int[4];
        static readonly bool[] connected = new bool[4];

        static bool GetPad(int index, out XInputState s)
        {
            s = new XInputState();
            if (xinputApi < 0) return false;
            // Querying a disconnected pad is slow, so only re-probe every two seconds.
            if (!connected[index] && Environment.TickCount - retryAt[index] < 0) return false;
            int r;
            try
            {
                if (xinputApi == 2) r = XInputGetState910(index, out s);
                else
                {
                    try { r = XInputGetState14(index, out s); xinputApi = 1; }
                    catch (DllNotFoundException) { xinputApi = 2; r = XInputGetState910(index, out s); }
                }
            }
            catch (Exception) { xinputApi = -1; return false; }
            connected[index] = r == 0;
            if (!connected[index]) retryAt[index] = Environment.TickCount + 2000;
            return connected[index];
        }

        public static Buttons Poll(int port, KeyMap map, bool keyboard)
        {
            Buttons b = Buttons.None;
            if (keyboard)
            {
                for (int i = 0; i < 8; i++)
                {
                    Keys k = map.Keys[port, i];
                    if (k != Keys.None && (GetAsyncKeyState((int)k) & 0x8000) != 0) b |= KeyMap.Order[i];
                }
            }

            XInputState s;
            if (GetPad(port, out s))
            {
                int w = s.Buttons;
                const int dead = 12000;
                if ((w & 0x0001) != 0 || s.ThumbLY > dead) b |= Buttons.Up;
                if ((w & 0x0002) != 0 || s.ThumbLY < -dead) b |= Buttons.Down;
                if ((w & 0x0004) != 0 || s.ThumbLX < -dead) b |= Buttons.Left;
                if ((w & 0x0008) != 0 || s.ThumbLX > dead) b |= Buttons.Right;
                if ((w & 0x0010) != 0) b |= Buttons.Start;
                if ((w & 0x0020) != 0) b |= Buttons.Select;
                // Pad layout mirrors the NES: bottom/left face buttons = B, right/top = A.
                if ((w & 0x1000) != 0 || (w & 0x4000) != 0) b |= Buttons.B;
                if ((w & 0x2000) != 0 || (w & 0x8000) != 0) b |= Buttons.A;
            }

            // The NES can't physically press opposite directions; some games crash if it happens.
            if ((b & (Buttons.Up | Buttons.Down)) == (Buttons.Up | Buttons.Down)) b &= ~(Buttons.Up | Buttons.Down);
            if ((b & (Buttons.Left | Buttons.Right)) == (Buttons.Left | Buttons.Right)) b &= ~(Buttons.Left | Buttons.Right);
            return b;
        }
    }
}
