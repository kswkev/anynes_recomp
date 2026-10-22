using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AnyNes.Core;

namespace AnyNes.Builder
{
    sealed class BuilderForm : Form
    {
        static readonly Color Accent = Color.FromArgb(200, 40, 40);

        readonly TextBox romBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        readonly TextBox titleBox = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox outBox = new TextBox { Dock = DockStyle.Fill };
        readonly PictureBox preview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        readonly PictureBox icon48 = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.CenterImage };
        readonly PictureBox icon32 = new PictureBox { Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.CenterImage };
        readonly PictureBox icon16 = new PictureBox { Size = new Size(16, 16), SizeMode = PictureBoxSizeMode.CenterImage };
        readonly Label frameLabel = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.None };
        readonly Button prevBtn = new Button { Text = "◀", Width = 36 };
        readonly Button nextBtn = new Button { Text = "▶", Width = 36 };
        readonly Label mapperVal = new Label(), prgVal = new Label(), chrVal = new Label(), mirrorVal = new Label(),
            batteryVal = new Label(), crcVal = new Label(), supportVal = new Label();
        readonly CheckBox zapperBox = new CheckBox { Text = "Zapper light gun, aimed with the mouse", AutoSize = true };
        readonly ComboBox regionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        bool settingRegion;
        readonly Button buildBtn = new Button { Text = "Build EXE", Height = 34, Width = 130 };
        readonly Button testBtn = new Button { Text = "Test play", Height = 34, Width = 100 };
        readonly Button batchBtn = new Button { Text = "Convert a folder...", Height = 34, Width = 150 };
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        readonly LinkLabel openLink = new LinkLabel { Text = "Open folder", AutoSize = true, Visible = false, Padding = new Padding(0, 6, 0, 0) };
        readonly LinkLabel runLink = new LinkLabel { Text = "Run game", AutoSize = true, Visible = false, Padding = new Padding(0, 6, 0, 0) };

        string romPath, romInner;
        byte[] romBytes;
        RomImage rom;
        Snapshots.Result shots;
        int frameIndex;
        CancellationTokenSource captureCts;
        string lastBuilt;
        bool outEdited;

        public BuilderForm()
        {
            Text = "AnyNES Builder";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(860, 600);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            BackColor = SystemColors.Window;
            Icon = MakeAppIcon();

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(Banner(), 0, 0);

            // ROM row
            var romRow = Row(out romBox, "ROM file", "Browse...", BrowseRom);
            romBox.ReadOnly = true;
            root.Controls.Add(romRow, 0, 1);

            // Middle: preview + details
            var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 4, 12, 4) };
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            root.Controls.Add(mid, 0, 2);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview.Dock = DockStyle.Fill;
            preview.Paint += PreviewPaint;
            left.Controls.Add(preview, 0, 0);
            var nav = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false };
            prevBtn.Click += delegate { StepFrame(-1); };
            nextBtn.Click += delegate { StepFrame(1); };
            frameLabel.Padding = new Padding(8, 7, 8, 0);
            nav.Controls.AddRange(new Control[] { prevBtn, frameLabel, nextBtn });
            left.Controls.Add(nav, 0, 1);
            mid.Controls.Add(left, 0, 0);

            var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 0, 0, 0), AutoSize = true };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;
            AddInfo(info, r++, "Game title", titleBox);
            AddInfo(info, r++, "Support", supportVal);
            AddInfo(info, r++, "Mapper", mapperVal);
            AddInfo(info, r++, "PRG ROM", prgVal);
            AddInfo(info, r++, "CHR", chrVal);
            AddInfo(info, r++, "Mirroring", mirrorVal);
            AddInfo(info, r++, "Battery save", batteryVal);
            AddInfo(info, r++, "Port 2", zapperBox);
            regionBox.Items.AddRange(new object[] { "NTSC  (60 Hz: USA, Japan)", "PAL  (50 Hz: Europe, Australia)" });
            regionBox.SelectedIndexChanged += delegate { OnRegionPicked(); };
            AddInfo(info, r++, "Region", regionBox);
            AddInfo(info, r++, "CRC32", crcVal);
            var icons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            icon48.Margin = icon32.Margin = new Padding(0, 0, 10, 0);
            icons.Controls.AddRange(new Control[] { icon48, icon32, icon16 });
            AddInfo(info, r++, "Exe icon", icons);
            var hint = new Label
            {
                Text = "The icon is taken from the screenshot on the left. Use ◀ ▶ to pick another frame.",
                ForeColor = SystemColors.GrayText, AutoSize = true, MaximumSize = new Size(330, 0), Padding = new Padding(0, 8, 0, 0)
            };
            info.Controls.Add(hint, 0, r);
            info.SetColumnSpan(hint, 2);
            mid.Controls.Add(info, 1, 0);

            // Output row
            var outRow = Row(out outBox, "Output exe", "Browse...", BrowseOut);
            outBox.TextChanged += delegate { if (outBox.Focused) outEdited = true; };
            root.Controls.Add(outRow, 0, 3);

            // Buttons
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true, Padding = new Padding(12, 4, 12, 12) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var statusFlow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            statusFlow.Controls.AddRange(new Control[] { status, runLink, openLink });
            bottom.Controls.Add(batchBtn, 0, 0);
            bottom.Controls.Add(statusFlow, 1, 0);
            bottom.Controls.Add(testBtn, 3, 0);
            bottom.Controls.Add(buildBtn, 4, 0);
            buildBtn.BackColor = Accent;
            buildBtn.ForeColor = Color.White;
            buildBtn.FlatStyle = FlatStyle.Flat;
            buildBtn.FlatAppearance.BorderSize = 0;
            buildBtn.Font = new Font(Font, FontStyle.Bold);
            root.Controls.Add(bottom, 0, 4);

            buildBtn.Click += delegate { Build(); };
            testBtn.Click += delegate { TestPlay(); };
            batchBtn.Click += delegate { using (var f = new BatchForm()) f.ShowDialog(this); };
            openLink.LinkClicked += delegate { if (lastBuilt != null) Process.Start("explorer.exe", "/select,\"" + lastBuilt + "\""); };
            runLink.LinkClicked += delegate { if (lastBuilt != null) Process.Start(lastBuilt); };
            titleBox.TextChanged += delegate { if (!outEdited && romPath != null) outBox.Text = DefaultOut(); };

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) LoadRom(files[0]);
            };

            SetStatus("Choose a ROM, or drag a .nes / .zip file onto this window.", false);
            UpdateUi();
        }

        // ---------------------------------------------------------------- layout helpers

        Control Banner()
        {
            var p = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.FromArgb(32, 32, 36) };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var accent = new SolidBrush(Accent)) g.FillRectangle(accent, 0, p.Height - 4, p.Width, 4);
                using (var big = new Font("Segoe UI Semibold", 17f))
                using (var small = new Font("Segoe UI", 9.5f))
                {
                    g.DrawString("AnyNES Builder", big, Brushes.White, 16, 10);
                    using (var b = new SolidBrush(Color.FromArgb(190, 190, 200)))
                        g.DrawString("Turn an NES ROM (.nes, or a .zip containing one) into a standalone Windows game.", small, b, 19, 46);
                }
            };
            p.Resize += delegate { p.Invalidate(); };
            return p;
        }

        TableLayoutPanel Row(out TextBox box, string label, string button, Action onClick)
        {
            box = new TextBox { Dock = DockStyle.Fill };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Padding = new Padding(12, 10, 12, 2) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(box, 1, 0);
            var b = new Button { Text = button, AutoSize = true };
            b.Click += delegate { onClick(); };
            t.Controls.Add(b, 2, 0);
            return t;
        }

        static void AddInfo(TableLayoutPanel t, int row, string label, Control value)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 12, 5) }, 0, row);
            if (value is Label) { ((Label)value).AutoSize = true; value.Anchor = AnchorStyles.Left; }
            else value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(value, 1, row);
        }

        void PreviewPaint(object sender, PaintEventArgs e)
        {
            if (preview.Image != null) return;
            string text = rom == null ? "Drop a ROM here" : (shots == null ? "Capturing title screen..." : "No picture");
            using (var f = new Font("Segoe UI", 11f))
            {
                var sz = e.Graphics.MeasureString(text, f);
                e.Graphics.DrawString(text, f, Brushes.Gray, (preview.Width - sz.Width) / 2, (preview.Height - sz.Height) / 2);
            }
        }

        static Icon MakeAppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- actions

        void BrowseRom()
        {
            using (var dlg = new OpenFileDialog { Filter = "NES ROMs (*.nes;*.zip)|*.nes;*.zip|All files|*.*", Title = "Choose an NES ROM" })
            {
                if (romPath != null) dlg.InitialDirectory = Path.GetDirectoryName(romPath);
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadRom(dlg.FileName);
            }
        }

        void BrowseOut()
        {
            using (var dlg = new SaveFileDialog { Filter = "Windows program (*.exe)|*.exe", Title = "Save game exe as" })
            {
                if (outBox.Text.Length > 0)
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(outBox.Text);
                    dlg.FileName = Path.GetFileName(outBox.Text);
                }
                if (dlg.ShowDialog(this) == DialogResult.OK) { outBox.Text = dlg.FileName; outEdited = true; }
            }
        }

        string DefaultOut()
        {
            string name = BatchBuilder.SafeFileName(titleBox.Text.Length > 0 ? titleBox.Text : romInner ?? "game");
            return Path.Combine(Path.GetDirectoryName(romPath), name + ".exe");
        }

        public void LoadRom(string path)
        {
            if (captureCts != null) captureCts.Cancel();
            try
            {
                string inner;
                byte[] bytes = RomLoader.Load(path, out inner);
                var parsed = RomImage.Parse(bytes);
                romPath = path;
                romInner = inner;
                romBytes = bytes;
                rom = parsed;
            }
            catch (Exception ex)
            {
                SetStatus("Could not load ROM: " + ex.Message, true);
                return;
            }

            romBox.Text = path;
            outEdited = false;
            titleBox.Text = RomLoader.CleanTitle(romInner);
            outBox.Text = DefaultOut();
            lastBuilt = null;
            runLink.Visible = openLink.Visible = false;

            bool supported = MapperFactory.IsSupported(rom.Mapper);
            mapperVal.Text = MapperFactory.Name(rom.Mapper) + "  (iNES " + rom.Mapper + ")";
            supportVal.Text = supported ? "✔ Supported" : "✖ Not supported";
            supportVal.ForeColor = supported ? Color.FromArgb(0, 128, 60) : Accent;
            prgVal.Text = (rom.Prg.Length / 1024) + " KB";
            chrVal.Text = rom.Chr.Length > 0 ? (rom.Chr.Length / 1024) + " KB ROM" : (rom.ChrRamSize / 1024) + " KB RAM";
            mirrorVal.Text = rom.Mirroring.ToString();
            batteryVal.Text = rom.Battery ? "Yes (saved to %LOCALAPPDATA%\\AnyNES)" : "No";
            crcVal.Text = rom.Crc32.ToString("X8");
            zapperBox.Checked = Zapper.IsZapperGame(rom.Crc32);
            rom.Region = RomLoader.DetectRegion(rom, romInner);
            settingRegion = true;
            regionBox.SelectedIndex = rom.Region == Core.Region.Pal ? 1 : 0;
            settingRegion = false;

            if (supported) StartCapture();
            else
            {
                SetShots(null);
                SetStatus("This ROM uses " + MapperFactory.Name(rom.Mapper) + " hardware, which AnyNES does not emulate yet.", true);
            }
            UpdateUi();
        }

        void OnRegionPicked()
        {
            if (settingRegion || rom == null) return;
            rom.Region = regionBox.SelectedIndex == 1 ? Core.Region.Pal : Core.Region.Ntsc;
            if (MapperFactory.IsSupported(rom.Mapper)) StartCapture();
        }

        /// <summary>Runs the game headless in the background to collect icon candidate frames.</summary>
        void StartCapture()
        {
            if (captureCts != null) captureCts.Cancel();
            SetShots(null);
            SetStatus("Capturing a title-screen icon...", false);
            var cts = new CancellationTokenSource();
            captureCts = cts;
            var romCopy = rom;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Snapshots.Result res = null;
                string err = null;
                try { res = Snapshots.Capture(romCopy, 20, () => cts.IsCancellationRequested); }
                catch (Exception ex) { err = ex.Message; }
                if (cts.IsCancellationRequested || IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    if (cts != captureCts) return;
                    SetShots(res);
                    SetStatus(err != null ? "The game crashed during preview: " + err : "Ready to build.", err != null);
                });
            });
        }

        void SetShots(Snapshots.Result res)
        {
            if (shots != null) foreach (var b in shots.Frames) b.Dispose();
            shots = res;
            frameIndex = res != null ? res.Best : 0;
            ShowFrame();
        }

        void StepFrame(int d)
        {
            if (shots == null || shots.Frames.Count == 0) return;
            frameIndex = (frameIndex + d + shots.Frames.Count) % shots.Frames.Count;
            ShowFrame();
        }

        void ShowFrame()
        {
            Bitmap frame = CurrentFrame;
            preview.Image = frame;
            SetIcon(icon48, frame, 48);
            SetIcon(icon32, frame, 32);
            SetIcon(icon16, frame, 16);
            frameLabel.Text = shots != null && shots.Frames.Count > 0
                ? "Icon frame: " + (frameIndex + 1) + " s of " + shots.Frames.Count
                : "";
            prevBtn.Enabled = nextBtn.Enabled = frame != null;
            preview.Invalidate();
        }

        Bitmap CurrentFrame
        {
            get { return shots != null && shots.Frames.Count > 0 ? shots.Frames[frameIndex] : null; }
        }

        static void SetIcon(PictureBox box, Bitmap frame, int size)
        {
            var old = box.Image;
            box.Image = frame != null ? ExeWriter.RenderIcon(frame, size) : null;
            if (old != null) old.Dispose();
        }

        void UpdateUi()
        {
            bool ok = rom != null && MapperFactory.IsSupported(rom.Mapper);
            buildBtn.Enabled = ok;
            testBtn.Enabled = ok;
            titleBox.Enabled = outBox.Enabled = zapperBox.Enabled = regionBox.Enabled = rom != null;
        }

        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Accent : SystemColors.ControlText;
        }

        void Build()
        {
            if (rom == null) return;
            string outPath = outBox.Text.Trim();
            if (outPath.Length == 0) { SetStatus("Choose where to save the exe.", true); return; }
            if (!outPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) outPath += ".exe";
            if (File.Exists(outPath) &&
                MessageBox.Show(this, Path.GetFileName(outPath) + " already exists. Replace it?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            string title = titleBox.Text.Trim();
            if (title.Length == 0) title = RomLoader.CleanTitle(romInner);
            var pkg = new GamePackage { Title = title, RomName = romInner, Rom = romBytes, Zapper = zapperBox.Checked, Region = rom.Region };
            try
            {
                Cursor = Cursors.WaitCursor;
                ExeWriter.Build(pkg, CurrentFrame, outPath);
                lastBuilt = Path.GetFullPath(outPath);
                SetStatus("Built " + Path.GetFileName(outPath) + " (" + (new FileInfo(outPath).Length / 1024) + " KB).", false);
                runLink.Visible = openLink.Visible = true;
            }
            catch (Exception ex)
            {
                SetStatus("Build failed: " + ex.Message, true);
            }
            finally { Cursor = Cursors.Default; }
        }

        void TestPlay()
        {
            if (romPath == null) return;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "AnyNES");
                Directory.CreateDirectory(dir);
                string stub = Path.Combine(dir, "AnyNesPlayer.exe");
                File.WriteAllBytes(stub, ExeWriter.PlayerStub());
                string args = "\"" + romPath + "\" " + (zapperBox.Checked ? "--zapper" : "--no-zapper")
                    + (rom.Region == Core.Region.Pal ? " --pal" : " --ntsc");
                Process.Start(new ProcessStartInfo(stub, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                SetStatus("Could not start the player: " + ex.Message, true);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (captureCts != null) captureCts.Cancel();
            base.OnFormClosed(e);
        }
    }

    /// <summary>Converts a whole folder of ROMs with a progress bar.</summary>
    sealed class BatchForm : Form
    {
        readonly TextBox inBox = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox outBox = new TextBox { Dock = DockStyle.Fill };
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill, Height = 22 };
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill };
        readonly Button startBtn = new Button { Text = "Convert", Width = 100, Height = 30 };
        readonly Button closeBtn = new Button { Text = "Close", Width = 100, Height = 30 };
        CancellationTokenSource cts;

        public BatchForm()
        {
            Text = "Convert a folder of ROMs";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(640, 220);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, Padding = new Padding(12) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AddRow(t, 0, "ROM folder", inBox);
            AddRow(t, 1, "Output folder", outBox);
            t.Controls.Add(bar, 0, 2); t.SetColumnSpan(bar, 3);
            t.Controls.Add(status, 0, 3); t.SetColumnSpan(status, 3);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { closeBtn, startBtn });
            t.Controls.Add(buttons, 0, 4); t.SetColumnSpan(buttons, 3);
            Controls.Add(t);

            status.Text = "Every .nes / .zip in the ROM folder becomes an exe in the output folder.";
            startBtn.Click += delegate { Start(); };
            closeBtn.Click += delegate { if (cts != null) cts.Cancel(); else Close(); };
        }

        void AddRow(TableLayoutPanel t, int row, string label, TextBox box)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            t.Controls.Add(box, 1, row);
            var b = new Button { Text = "Browse...", AutoSize = true };
            b.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = box.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
            };
            t.Controls.Add(b, 2, row);
        }

        void Start()
        {
            string inDir = inBox.Text.Trim(), outDir = outBox.Text.Trim();
            if (!Directory.Exists(inDir)) { status.Text = "Pick an existing ROM folder."; return; }
            if (outDir.Length == 0) { status.Text = "Pick an output folder."; return; }
            startBtn.Enabled = false;
            closeBtn.Text = "Cancel";
            cts = new CancellationTokenSource();
            var token = cts.Token;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int ok = 0, failed = 0;
                string err = null;
                try
                {
                    var items = BatchBuilder.Run(inDir, outDir, (done, total, it) =>
                    {
                        if (it.Error == null) Interlocked.Increment(ref ok); else Interlocked.Increment(ref failed);
                        BeginInvoke((Action)delegate
                        {
                            bar.Maximum = total;
                            bar.Value = Math.Min(done, total);
                            status.Text = done + " / " + total + "  -  " + (it.Title ?? Path.GetFileName(it.Source));
                        });
                    }, token);
                }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)delegate
                {
                    status.Text = err != null ? "Failed: " + err
                        : (token.IsCancellationRequested ? "Cancelled. " : "Done. ") + ok + " built, " + failed + " skipped (see AnyNES-batch-log.txt).";
                    startBtn.Enabled = true;
                    closeBtn.Text = "Close";
                    cts = null;
                });
            });
        }
    }
}
