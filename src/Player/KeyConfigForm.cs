using System.Drawing;
using System.Windows.Forms;

namespace AnyNes.Player
{
    /// <summary>Click a button, then press a key to bind it. Esc cancels the capture, Delete clears it.</summary>
    sealed class KeyConfigForm : Form
    {
        readonly KeyMap map;
        readonly Button[,] buttons = new Button[2, 8];
        int capPort = -1, capIndex = -1;

        public KeyConfigForm(KeyMap map)
        {
            this.map = map;
            Text = "Controls";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            var table = new TableLayoutPanel
            {
                ColumnCount = 3, RowCount = 10, Dock = DockStyle.Fill, Padding = new Padding(12), AutoSize = true
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
            table.Controls.Add(new Label { Text = "Player 1", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
            table.Controls.Add(new Label { Text = "Player 2", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 2, 0);
            for (int i = 0; i < 8; i++)
            {
                table.Controls.Add(new Label { Text = KeyMap.Order[i].ToString(), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }, 0, i + 1);
                for (int p = 0; p < 2; p++)
                {
                    int pp = p, ii = i;
                    var b = new Button { Dock = DockStyle.Fill, Height = 28 };
                    b.Click += delegate { BeginCapture(pp, ii); };
                    buttons[p, i] = b;
                    table.Controls.Add(b, p + 1, i + 1);
                }
            }

            var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            var defaults = new Button { Text = "Defaults", Width = 80 };
            defaults.Click += delegate { map.SetDefaults(); Refresh2(); };
            bottom.Controls.AddRange(new Control[] { cancel, ok, defaults });
            table.Controls.Add(bottom, 0, 9);
            table.SetColumnSpan(bottom, 3);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Refresh2();
        }

        void Refresh2()
        {
            for (int p = 0; p < 2; p++)
                for (int i = 0; i < 8; i++)
                    buttons[p, i].Text = p == capPort && i == capIndex ? "press a key..." : map.Keys[p, i].ToString();
        }

        void BeginCapture(int port, int index)
        {
            capPort = port;
            capIndex = index;
            Refresh2();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (capPort >= 0)
            {
                Keys k = keyData & Keys.KeyCode;
                if (k == Keys.Escape) { }
                else if (k == Keys.Delete) map.Keys[capPort, capIndex] = Keys.None;
                else map.Keys[capPort, capIndex] = k;
                capPort = capIndex = -1;
                Refresh2();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
