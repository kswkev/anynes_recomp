using System;
using System.IO;
using System.Windows.Forms;
using AnyNes.Core;

namespace AnyNes.Player
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            byte[] romBytes;
            string title;
            bool? zapper = null;
            try
            {
                var pkg = GamePackage.FromCurrentExe();
                if (pkg != null)
                {
                    romBytes = pkg.Rom;
                    title = pkg.Title;
                    zapper = pkg.Zapper;
                }
                else
                {
                    // Bare player stub: play a ROM given on the command line or picked in a dialog.
                    string path = args.Length > 0 ? args[0] : null;
                    if (path == null)
                    {
                        using (var dlg = new OpenFileDialog { Filter = "NES ROMs (*.nes;*.zip)|*.nes;*.zip|All files|*.*", Title = "Open NES ROM" })
                        {
                            if (dlg.ShowDialog() != DialogResult.OK) return 0;
                            path = dlg.FileName;
                        }
                    }
                    if (Array.IndexOf(args, "--zapper") >= 0) zapper = true;
                    else if (Array.IndexOf(args, "--no-zapper") >= 0) zapper = false;
                    string inner;
                    romBytes = RomLoader.Load(path, out inner);
                    title = RomLoader.CleanTitle(inner);
                }

                var rom = RomImage.Parse(romBytes);
                if (!MapperFactory.IsSupported(rom.Mapper))
                    throw new NotSupportedException("This game uses " + MapperFactory.Name(rom.Mapper) + ", which is not supported.");
                Application.Run(new PlayerForm(rom, title, zapper ?? Zapper.IsZapperGame(rom.Crc32)));
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AnyNES", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
