using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AnyNes.Builder
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        /// <summary>
        /// UI by default. Command line:
        ///   AnyNesBuilder rom.nes|rom.zip                 open the UI with a ROM loaded
        ///   AnyNesBuilder --build rom [out.exe] [title]   build one exe
        ///   AnyNesBuilder --batch romDir outDir           build every ROM in a folder
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--build" || args[0] == "--batch"))
            {
                AttachConsole(-1);
                Console.WriteLine();
                try { return args[0] == "--build" ? CliBuild(args) : CliBatch(args); }
                catch (Exception ex) { Console.WriteLine("error: " + ex.Message); return 1; }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new BuilderForm();
            if (args.Length > 0 && File.Exists(args[0])) form.Shown += delegate { form.LoadRom(args[0]); };
            Application.Run(form);
            return 0;
        }

        static int CliBuild(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: --build <rom> [out.exe] [title]"); return 1; }
            string rom = args[1];
            string title = args.Length > 3 ? args[3] : null;
            string outPath = args.Length > 2 ? args[2] : null;
            if (outPath == null)
            {
                string inner;
                AnyNes.Core.RomLoader.Load(rom, out inner);
                outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rom)),
                    BatchBuilder.SafeFileName(title ?? AnyNes.Core.RomLoader.CleanTitle(inner)) + ".exe");
            }
            BatchBuilder.BuildOne(rom, outPath, title, 20, null);
            Console.WriteLine("built " + outPath);
            return 0;
        }

        static int CliBatch(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("usage: --batch <rom dir> <out dir>"); return 1; }
            var items = BatchBuilder.Run(args[1], args[2], (done, total, it) =>
                Console.WriteLine("[{0}/{1}] {2}: {3}", done, total, Path.GetFileName(it.Source), it.Error ?? "ok"),
                CancellationToken.None);
            int ok = 0;
            foreach (var it in items) if (it.Error == null) ok++;
            Console.WriteLine("{0} of {1} built", ok, items.Count);
            return 0;
        }
    }
}
