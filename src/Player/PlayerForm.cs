using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AnyNes.Core;

namespace AnyNes.Player
{
    /// <summary>Draws the 256-pixel-wide frame with nearest-neighbour scaling via GDI StretchDIBits.</summary>
    sealed class ScreenControl : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest,
            int xSrc, int ySrc, int wSrc, int hSrc, int[] bits, ref BitmapInfoHeader bmi, int usage, int rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

        public readonly object Sync = new object();
        public readonly int[] Pixels = new int[256 * 240];
        public int VisibleLines = 224;
        public string Overlay;
        public long OverlayUntil;
        IntPtr cachedHandle;

        // ---- Scanline filter: 0 = off, 1-3 = light/medium/strong darkening of the gap between lines.
        public static readonly string[] ScanlineNames = { "Off", "Light", "Medium", "Strong" };
        static readonly int[] ScanlineGap = { 256, 192, 128, 64 };     // brightness of gap rows, /256
        int scanlineLevel;
        int[] filtered = new int[0];

        public int ScanlineLevel
        {
            get { return scanlineLevel; }
            set { scanlineLevel = Math.Max(0, Math.Min(3, value)); Invalidate(); }
        }

        // ---- Zapper: mouse aim (NES pixel coordinates, -1 when off the picture) and trigger state.
        bool zapperMode;
        public volatile int AimX = -1, AimY = -1;
        public volatile bool LeftHeld, RightHeld;
        public volatile int LeftClicks, RightClicks;
        Point mouse;
        bool mouseInside;
        static Cursor blankCursor;

        public ScreenControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            BackColor = Color.Black;
        }

        public bool ZapperMode
        {
            get { return zapperMode; }
            set
            {
                zapperMode = value;
                UpdateAim();
                Invalidate();
            }
        }

        static Cursor BlankCursor
        {
            get
            {
                if (blankCursor == null)
                    using (var bmp = new Bitmap(32, 32))
                        blankCursor = new Cursor(bmp.GetHicon());
                return blankCursor;
            }
        }

        void UpdateAim()
        {
            Rectangle r = ImageRect();
            bool over = mouseInside && r.Contains(mouse);
            if (zapperMode && over)
            {
                AimX = (mouse.X - r.X) * 256 / r.Width;
                AimY = (mouse.Y - r.Y) * VisibleLines / r.Height + (240 - VisibleLines) / 2;
            }
            else AimX = AimY = -1;
            Cursor = zapperMode && over ? BlankCursor : Cursors.Default;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            mouseInside = true;
            UpdateAim();
            if (zapperMode) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouseInside = false;
            LeftHeld = RightHeld = false;
            UpdateAim();
            if (zapperMode) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { LeftHeld = true; LeftClicks++; }
            else if (e.Button == MouseButtons.Right) { RightHeld = true; RightClicks++; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) LeftHeld = false;
            else if (e.Button == MouseButtons.Right) RightHeld = false;
            base.OnMouseUp(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateAim();
        }

        void DrawCrosshair(Graphics g, Rectangle img)
        {
            if (!zapperMode || !mouseInside || !img.Contains(mouse)) return;
            float s = Math.Max(1f, img.Height / 224f);      // NES pixel size on screen
            float radius = 7 * s, gap = 2.5f * s, arm = 11 * s;
            float x = mouse.X, y = mouse.Y;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var outline = new Pen(Color.FromArgb(200, 0, 0, 0), Math.Max(3f, s * 1.6f)))
            using (var line = new Pen(Color.FromArgb(255, 255, 70, 50), Math.Max(1.5f, s * 0.7f)))
            {
                foreach (var pen in new[] { outline, line })
                {
                    g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
                    g.DrawLine(pen, x - arm, y, x - gap, y);
                    g.DrawLine(pen, x + gap, y, x + arm, y);
                    g.DrawLine(pen, x, y - arm, x, y - gap);
                    g.DrawLine(pen, x, y + gap, x, y + arm);
                }
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); cachedHandle = Handle; }
        protected override void OnHandleDestroyed(EventArgs e) { cachedHandle = IntPtr.Zero; base.OnHandleDestroyed(e); }

        /// <summary>Thread-safe repaint request.</summary>
        public void RequestPaint()
        {
            IntPtr h = cachedHandle;
            if (h != IntPtr.Zero) InvalidateRect(h, IntPtr.Zero, false);
        }

        public Rectangle ImageRect()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            double scale = Math.Min(w / 256.0, h / (double)VisibleLines);
            // Prefer integer scaling when it fills most of the area (crisper pixels).
            if (scale >= 2 && Math.Floor(scale) / scale > 0.92) scale = Math.Floor(scale);
            int dw = (int)(256 * scale), dh = (int)(VisibleLines * scale);
            return new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);
        }

        /// <summary>
        /// Expands the frame vertically to exactly <paramref name="height"/> rows (only the width is then
        /// stretched by GDI). Each NES line becomes a band of rows whose bottom third, at least one row,
        /// is darkened, which keeps the effect even at non-integer scales such as fullscreen.
        /// </summary>
        void BuildScanlines(int height)
        {
            if (filtered.Length != 256 * height) filtered = new int[256 * height];
            int lines = VisibleLines;
            uint k = (uint)ScanlineGap[scanlineLevel];
            lock (Sync)
            {
                for (int line = 0; line < lines; line++)
                {
                    int start = line * height / lines, end = (line + 1) * height / lines;
                    int band = end - start;
                    int dark = Math.Max(1, (band + 1) / 3);
                    int src = line * 256;
                    for (int y = start; y < end; y++)
                    {
                        int dst = y * 256;
                        if (y < end - dark)
                        {
                            Array.Copy(Pixels, src, filtered, dst, 256);
                            continue;
                        }
                        for (int x = 0; x < 256; x++)
                        {
                            // Scale R and B together and G separately (no carries between channels).
                            uint c = (uint)Pixels[src + x];
                            filtered[dst + x] = unchecked((int)(0xFF000000
                                | ((((c & 0xFF00FF) * k) >> 8) & 0xFF00FF)
                                | ((((c & 0x00FF00) * k) >> 8) & 0x00FF00)));
                        }
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Rectangle r = ImageRect();
            using (var black = new SolidBrush(Color.Black))
            {
                if (r.Top > 0) g.FillRectangle(black, 0, 0, Width, r.Top);
                if (r.Bottom < Height) g.FillRectangle(black, 0, r.Bottom, Width, Height - r.Bottom);
                if (r.Left > 0) g.FillRectangle(black, 0, r.Top, r.Left, r.Height);
                if (r.Right < Width) g.FillRectangle(black, r.Right, r.Top, Width - r.Right, r.Height);
            }

            IntPtr hdc = g.GetHdc();
            try
            {
                var bmi = new BitmapInfoHeader
                {
                    biSize = Marshal.SizeOf(typeof(BitmapInfoHeader)),
                    biWidth = 256,
                    biHeight = -VisibleLines,   // top-down
                    biPlanes = 1,
                    biBitCount = 32
                };
                SetStretchBltMode(hdc, 3);      // COLORONCOLOR = nearest neighbour
                // Scanlines need at least two screen rows per NES line to show anything.
                if (scanlineLevel > 0 && r.Height >= VisibleLines * 2)
                {
                    BuildScanlines(r.Height);
                    bmi.biHeight = -r.Height;
                    StretchDIBits(hdc, r.X, r.Y, r.Width, r.Height, 0, 0, 256, r.Height, filtered, ref bmi, 0, 0x00CC0020);
                }
                else
                {
                    lock (Sync)
                        StretchDIBits(hdc, r.X, r.Y, r.Width, r.Height, 0, 0, 256, VisibleLines, Pixels, ref bmi, 0, 0x00CC0020);
                }
            }
            finally { g.ReleaseHdc(hdc); }

            DrawCrosshair(g, r);

            string text = Overlay;
            if (text != null && Environment.TickCount - OverlayUntil < 0)
            {
                using (var f = new Font("Segoe UI", Math.Max(9f, r.Height / 40f), FontStyle.Bold))
                {
                    g.DrawString(text, f, Brushes.Black, r.X + 9, r.Y + 9);
                    g.DrawString(text, f, Brushes.White, r.X + 8, r.Y + 8);
                }
            }
        }
    }

    sealed class PlayerForm : Form
    {
        readonly string title;
        readonly string dataDir;
        readonly RomImage rom;
        readonly ScreenControl screen = new ScreenControl();
        readonly MenuStrip menu = new MenuStrip();
        readonly KeyMap keys = new KeyMap();
        readonly string keysPath;
        readonly object nesLock = new object();
        Nes nes;
        Thread emuThread;
        volatile bool running = true, paused, hasFocus = true, muted, fullscreen, pauseInBackground = true, zapper;
        int scale = 3;
        int slot = 1;
        bool cropOverscan = true;
        FormWindowState prevState;
        ToolStripMenuItem pauseItem, muteItem, cropItem, bgPauseItem, zapperItem, ntscItem, palItem;
        bool regionOverridden;
        readonly ToolStripMenuItem[] scaleItems = new ToolStripMenuItem[6];
        readonly ToolStripMenuItem[] scanlineItems = new ToolStripMenuItem[4];
        int scanlines;
        readonly ToolStripMenuItem[] slotItems = new ToolStripMenuItem[4];

        public PlayerForm(RomImage rom, string title, bool zapperDefault)
        {
            this.rom = rom;
            this.title = title;
            zapper = zapperDefault;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyNES");
            dataDir = Path.Combine(root, SafeName(title) + "-" + rom.Crc32.ToString("X8"));
            keysPath = Path.Combine(root, "controls.ini");
            keys.Load(keysPath);
            LoadSettings();

            nes = new Nes(rom);
            LoadBattery();

            Text = title;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            BackColor = Color.Black;
            KeyPreview = true;
            BuildMenu();
            screen.Dock = DockStyle.Fill;
            screen.VisibleLines = cropOverscan ? 224 : 240;
            screen.ZapperMode = zapper;
            screen.ScanlineLevel = scanlines;
            Controls.Add(screen);
            Controls.Add(menu);
            MainMenuStrip = menu;
            ApplyScale();
            StartPosition = FormStartPosition.CenterScreen;

            Activated += delegate { hasFocus = true; };
            Deactivate += delegate { hasFocus = false; };
            // Double-click toggles fullscreen, except when the mouse is a light gun (rapid shots).
            screen.DoubleClick += delegate { if (!zapper) ToggleFullscreen(); };
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        // ---------------------------------------------------------------- menu

        void BuildMenu()
        {
            var game = new ToolStripMenuItem("&Game");
            game.DropDownItems.Add(Item("&Reset", "Ctrl+R", delegate { DoReset(false); }));
            game.DropDownItems.Add(Item("&Power cycle", "Ctrl+Shift+R", delegate { DoReset(true); }));
            pauseItem = Item("P&ause", "Pause", delegate { TogglePause(); });
            game.DropDownItems.Add(pauseItem);
            game.DropDownItems.Add(new ToolStripSeparator());
            game.DropDownItems.Add(Item("&Save state", "F5", delegate { SaveState(); }));
            game.DropDownItems.Add(Item("&Load state", "F8", delegate { LoadState(); }));
            var slotMenu = new ToolStripMenuItem("State s&lot");
            for (int i = 0; i < 4; i++)
            {
                int n = i + 1;
                slotItems[i] = Item("Slot " + n, n.ToString(), delegate { SelectSlot(n); });
                slotMenu.DropDownItems.Add(slotItems[i]);
            }
            game.DropDownItems.Add(slotMenu);
            game.DropDownItems.Add(new ToolStripSeparator());
            game.DropDownItems.Add(Item("E&xit", "Alt+F4", delegate { Close(); }));

            var view = new ToolStripMenuItem("&View");
            for (int i = 0; i < 6; i++)
            {
                int s = i + 1;
                scaleItems[i] = Item(s + "x", null, delegate { scale = s; ApplyScale(); });
                view.DropDownItems.Add(scaleItems[i]);
            }
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Item("&Fullscreen", "F11 / Alt+Enter", delegate { ToggleFullscreen(); }));
            cropItem = Item("&Hide overscan", null, delegate
            {
                cropOverscan = !cropOverscan;
                screen.VisibleLines = cropOverscan ? 224 : 240;
                ApplyScale();
            });
            var scanlineMenu = new ToolStripMenuItem("&Scanlines");
            for (int i = 0; i < 4; i++)
            {
                int level = i;
                scanlineItems[i] = Item(ScreenControl.ScanlineNames[i], i == 0 ? "F9 cycles" : null, delegate { SetScanlines(level); });
                scanlineMenu.DropDownItems.Add(scanlineItems[i]);
            }
            view.DropDownItems.Add(scanlineMenu);
            view.DropDownItems.Add(cropItem);

            var options = new ToolStripMenuItem("&Options");
            options.DropDownItems.Add(Item("&Controls...", null, delegate { ConfigureKeys(); }));
            zapperItem = Item("&Zapper light gun (mouse, port 2)", null, delegate { SetZapper(!zapper); });
            options.DropDownItems.Add(zapperItem);
            var regionMenu = new ToolStripMenuItem("Console &region");
            ntscItem = Item("&NTSC (60 Hz: USA, Japan)", null, delegate { SetRegion(Core.Region.Ntsc); });
            palItem = Item("&PAL (50 Hz: Europe, Australia)", null, delegate { SetRegion(Core.Region.Pal); });
            regionMenu.DropDownItems.Add(ntscItem);
            regionMenu.DropDownItems.Add(palItem);
            options.DropDownItems.Add(regionMenu);
            muteItem = Item("&Mute", "M", delegate { muted = !muted; UpdateChecks(); });
            options.DropDownItems.Add(muteItem);
            bgPauseItem = Item("Pause when in &background", null, delegate { pauseInBackground = !pauseInBackground; UpdateChecks(); });
            options.DropDownItems.Add(bgPauseItem);

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Controls", "F1", delegate { ShowHelp(); }));

            menu.Items.AddRange(new ToolStripItem[] { game, view, options, help });
            UpdateChecks();
        }

        static ToolStripMenuItem Item(string text, string shortcut, EventHandler click)
        {
            var it = new ToolStripMenuItem(text, null, click);
            if (shortcut != null) it.ShortcutKeyDisplayString = shortcut;
            return it;
        }

        void UpdateChecks()
        {
            pauseItem.Checked = paused;
            muteItem.Checked = muted;
            cropItem.Checked = cropOverscan;
            bgPauseItem.Checked = pauseInBackground;
            zapperItem.Checked = zapper;
            ntscItem.Checked = rom.Region == Core.Region.Ntsc;
            palItem.Checked = rom.Region == Core.Region.Pal;
            for (int i = 0; i < 6; i++) scaleItems[i].Checked = scale == i + 1;
            for (int i = 0; i < 4; i++) scanlineItems[i].Checked = scanlines == i;
            for (int i = 0; i < 4; i++) slotItems[i].Checked = slot == i + 1;
        }

        void ApplyScale()
        {
            if (fullscreen) { UpdateChecks(); return; }
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            ClientSize = new Size(256 * scale, screen.VisibleLines * scale + menu.Height);
            UpdateChecks();
        }

        void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (fullscreen)
            {
                prevState = WindowState;
                menu.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
                Cursor.Hide();
            }
            else
            {
                Cursor.Show();
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = prevState;
                menu.Visible = true;
                ApplyScale();
            }
        }

        /// <summary>Switching console timing needs a power cycle (the whole machine is rebuilt).</summary>
        void SetRegion(Core.Region r)
        {
            if (rom.Region == r) return;
            rom.Region = r;
            regionOverridden = true;
            DoReset(true);
            UpdateChecks();
            ShowOverlay(r == Core.Region.Pal ? "PAL console (50 Hz) - power cycled" : "NTSC console (60 Hz) - power cycled");
        }

        void SetScanlines(int level)
        {
            scanlines = level;
            screen.ScanlineLevel = level;
            UpdateChecks();
            string msg = "Scanlines: " + ScreenControl.ScanlineNames[level];
            // At 1x there is only one screen row per NES line, so the effect can't be drawn.
            if (level > 0 && !fullscreen && scale < 2) msg += " (needs 2x or larger)";
            ShowOverlay(msg);
        }

        void SetZapper(bool on)
        {
            zapper = on;
            screen.ZapperMode = on;
            UpdateChecks();
            ShowOverlay(on ? "Zapper in port 2: aim with the mouse, click to shoot" : "Controller in port 2");
        }

        void ShowOverlay(string text)
        {
            screen.Overlay = text;
            screen.OverlayUntil = Environment.TickCount + 2000;
            screen.Invalidate();
        }

        void ShowHelp()
        {
            var sb = new System.Text.StringBuilder();
            string[] names = { "Up", "Down", "Left", "Right", "B", "A", "Select", "Start" };
            for (int i = 0; i < 8; i++)
                sb.AppendLine(names[i].PadRight(8) + "\t" + keys.Keys[0, i] + "\t(P2: " + keys.Keys[1, i] + ")");
            sb.AppendLine();
            sb.AppendLine("Xbox-compatible controllers work automatically (pad 1 = player 1, pad 2 = player 2).");
            sb.AppendLine();
            sb.AppendLine("Light-gun games (Options > Zapper): aim with the mouse, left-click to shoot,");
            sb.AppendLine("right-click to shoot away from the screen.");
            sb.AppendLine();
            sb.AppendLine("F5 save state, F8 load state, 1-4 select slot");
            sb.AppendLine("F11 / Alt+Enter fullscreen, Esc leaves fullscreen, F9 cycles scanlines");
            sb.AppendLine("Pause/Break pauses, M mutes, Ctrl+R resets (unless those keys are bound to the game)");
            MessageBox.Show(this, sb.ToString(), title + " - Controls", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ConfigureKeys()
        {
            bool was = paused;
            paused = true;
            using (var dlg = new KeyConfigForm(keys))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) keys.Save(keysPath);
                else keys.Load(keysPath);
            }
            paused = was;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Game keys win over hotkeys, and are swallowed so they don't trigger menu navigation or beeps.
            if ((keyData & Keys.Modifiers) == 0)
            {
                for (int p = 0; p < 2; p++)
                    for (int i = 0; i < 8; i++)
                        if (keys.Keys[p, i] == keyData) return true;
            }
            switch (keyData)
            {
                case Keys.F5: SaveState(); return true;
                case Keys.F8: LoadState(); return true;
                case Keys.F11:
                case Keys.Alt | Keys.Enter: ToggleFullscreen(); return true;
                case Keys.Escape: if (fullscreen) { ToggleFullscreen(); return true; } break;
                case Keys.Pause:
                case Keys.P: TogglePause(); return true;
                case Keys.M: muted = !muted; UpdateChecks(); ShowOverlay(muted ? "Muted" : "Sound on"); return true;
                case Keys.Control | Keys.R: DoReset(false); return true;
                case Keys.Control | Keys.Shift | Keys.R: DoReset(true); return true;
                case Keys.F1: ShowHelp(); return true;
                case Keys.F9: SetScanlines((scanlines + 1) % 4); return true;
                case Keys.D1: case Keys.D2: case Keys.D3: case Keys.D4:
                    SelectSlot(keyData - Keys.D0); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------------------------------------------------------- actions

        void TogglePause()
        {
            paused = !paused;
            UpdateChecks();
            ShowOverlay(paused ? "Paused" : "");
            if (paused) screen.OverlayUntil = int.MaxValue + (long)Environment.TickCount;
        }

        void DoReset(bool hard)
        {
            lock (nesLock)
            {
                if (hard)
                {
                    SaveBattery();
                    nes = new Nes(rom);
                    LoadBattery();
                }
                else nes.SoftReset();
            }
            ShowOverlay(hard ? "Power cycled" : "Reset");
        }

        void SelectSlot(int n)
        {
            slot = n;
            UpdateChecks();
            ShowOverlay("State slot " + n);
        }

        string StatePath { get { return Path.Combine(dataDir, "state" + slot + ".sav"); } }

        void SaveState()
        {
            try
            {
                byte[] data;
                lock (nesLock) data = nes.SaveState();
                Directory.CreateDirectory(dataDir);
                File.WriteAllBytes(StatePath, data);
                ShowOverlay("Saved state " + slot);
            }
            catch (Exception ex) { ShowOverlay("Save failed: " + ex.Message); }
        }

        void LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) { ShowOverlay("Slot " + slot + " is empty"); return; }
                var loaded = Nes.LoadState(File.ReadAllBytes(StatePath));
                lock (nesLock) nes = loaded;
                rom.Region = loaded.Region;     // a state carries its console's timing
                UpdateChecks();
                ShowOverlay("Loaded state " + slot);
            }
            catch (Exception ex) { ShowOverlay("Load failed: " + ex.Message); }
        }

        string BatteryPath { get { return Path.Combine(dataDir, "battery.sav"); } }

        void LoadBattery()
        {
            var ram = nes.Mapper.BatteryRam;
            if (ram == null) return;
            try
            {
                if (!File.Exists(BatteryPath)) return;
                var data = File.ReadAllBytes(BatteryPath);
                Array.Copy(data, ram, Math.Min(data.Length, ram.Length));
            }
            catch (Exception) { }
        }

        void SaveBattery()
        {
            var ram = nes.Mapper.BatteryRam;
            if (ram == null) return;
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllBytes(BatteryPath, ram);
            }
            catch (Exception) { }
        }

        string SettingsPath { get { return Path.Combine(dataDir, "settings.ini"); } }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    int n;
                    switch (parts[0])
                    {
                        case "scale": if (int.TryParse(parts[1], out n) && n >= 1 && n <= 6) scale = n; break;
                        case "crop": cropOverscan = parts[1] == "1"; break;
                        case "scanlines": if (int.TryParse(parts[1], out n) && n >= 0 && n <= 3) scanlines = n; break;
                        case "muted": muted = parts[1] == "1"; break;
                        case "bgpause": pauseInBackground = parts[1] == "1"; break;
                        case "zapper": zapper = parts[1] == "1"; break;
                        case "region":
                            rom.Region = parts[1] == "pal" ? Core.Region.Pal : Core.Region.Ntsc;
                            regionOverridden = true;
                            break;
                    }
                }
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                var lines = new System.Collections.Generic.List<string>
                {
                    "scale=" + scale, "crop=" + (cropOverscan ? 1 : 0), "scanlines=" + scanlines,
                    "muted=" + (muted ? 1 : 0), "bgpause=" + (pauseInBackground ? 1 : 0),
                    "zapper=" + (zapper ? 1 : 0)
                };
                // Only remember the region if the player picked one; otherwise follow the exe's default.
                if (regionOverridden) lines.Add("region=" + (rom.Region == Core.Region.Pal ? "pal" : "ntsc"));
                File.WriteAllLines(SettingsPath, lines);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- emulation thread

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            emuThread = new Thread(EmuLoop) { IsBackground = true, Name = "Emulation", Priority = ThreadPriority.AboveNormal };
            emuThread.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            if (emuThread != null) emuThread.Join(1000);
            lock (nesLock) SaveBattery();
            SaveSettings();
            if (fullscreen) Cursor.Show();
            base.OnFormClosing(e);
        }

        void EmuLoop()
        {
            WaveOut audio = null;
            try { audio = new WaveOut(Apu.SampleRate); }
            catch (Exception) { audio = null; }

            var samples = new short[4096];
            var silence = new short[samples.Length];
            var clock = Stopwatch.StartNew();
            double nextFrame = 0;
            int batteryTimer = 0;
            // A click is held for at least a few frames so games polling once per frame never miss it.
            int seenLeft = screen.LeftClicks, seenRight = screen.RightClicks, leftHold = 0, rightHold = 0;

            try
            {
                while (running)
                {
                    bool idle = paused || (pauseInBackground && !hasFocus && !fullscreen);
                    if (idle)
                    {
                        Thread.Sleep(15);
                        nextFrame = clock.Elapsed.TotalMilliseconds;
                        continue;
                    }

                    int count;
                    double frameMs;
                    lock (nesLock)
                    {
                        frameMs = 1000.0 / nes.FrameRate;
                        nes.Input[0] = InputPoller.Poll(0, keys, hasFocus);
                        nes.Input[1] = InputPoller.Poll(1, keys, hasFocus);

                        nes.Zapper.Enabled = zapper;
                        if (screen.LeftClicks != seenLeft) { seenLeft = screen.LeftClicks; leftHold = 3; }
                        if (screen.RightClicks != seenRight) { seenRight = screen.RightClicks; rightHold = 3; }
                        bool left = screen.LeftHeld || leftHold > 0;
                        bool right = screen.RightHeld || rightHold > 0;
                        if (leftHold > 0) leftHold--;
                        if (rightHold > 0) rightHold--;
                        // Right-click fires with the gun pointed away from the screen.
                        nes.Zapper.X = right ? -1 : screen.AimX;
                        nes.Zapper.Y = right ? -1 : screen.AimY;
                        nes.Zapper.Trigger = zapper && (left || right);

                        nes.RunFrame();
                        count = nes.Apu.TakeSamples(samples);
                        int first = cropOverscan ? 8 : 0;
                        lock (screen.Sync)
                            Array.Copy(nes.Ppu.Frame, first * 256, screen.Pixels, 0, screen.VisibleLines * 256);
                        // Persist battery RAM roughly once a minute in case the process is killed.
                        if (++batteryTimer >= 3600) { batteryTimer = 0; SaveBattery(); }
                    }
                    screen.RequestPaint();

                    if (audio != null)
                    {
                        audio.Write(muted ? silence : samples, count);
                    }
                    else
                    {
                        nextFrame += frameMs;
                        double wait = nextFrame - clock.Elapsed.TotalMilliseconds;
                        if (wait > 1) Thread.Sleep((int)wait);
                        else if (wait < -100) nextFrame = clock.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch (Exception ex)
            {
                BeginInvoke((Action)delegate
                {
                    MessageBox.Show(this, "The emulator stopped: " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
            finally
            {
                if (audio != null) audio.Dispose();
            }
        }
    }
}
