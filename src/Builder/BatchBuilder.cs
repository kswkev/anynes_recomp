using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnyNes.Core;

namespace AnyNes.Builder
{
    static class BatchBuilder
    {
        public sealed class Item
        {
            public string Source, Output, Title, Error;
            public int Mapper = -1;
        }

        public static bool IsRomFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".nes" || ext == ".zip";
        }

        /// <summary>Builds one exe; returns the output path. Throws on unsupported/invalid ROMs.</summary>
        public static string BuildOne(string romPath, string outPath, string title, int iconSeconds, Func<bool> cancelled)
        {
            string inner;
            byte[] bytes = RomLoader.Load(romPath, out inner);
            var rom = RomImage.Parse(bytes);
            if (!MapperFactory.IsSupported(rom.Mapper))
                throw new NotSupportedException(MapperFactory.Name(rom.Mapper) + " is not supported");
            if (string.IsNullOrEmpty(title)) title = RomLoader.CleanTitle(inner);
            rom.Region = RomLoader.DetectRegion(rom, inner);

            var shots = Snapshots.Capture(rom, iconSeconds, cancelled);
            try
            {
                var pkg = new GamePackage { Title = title, RomName = inner, Rom = bytes, Zapper = Zapper.IsZapperGame(rom.Crc32), Region = rom.Region };
                ExeWriter.Build(pkg, shots.Frames.Count > 0 ? shots.Frames[shots.Best] : null, outPath);
            }
            finally
            {
                foreach (var b in shots.Frames) b.Dispose();
            }
            return outPath;
        }

        public static string SafeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return s.Length == 0 ? "game" : s;
        }

        /// <summary>Converts every .nes/.zip in a folder. Progress reports (done, total, item).</summary>
        public static List<Item> Run(string inDir, string outDir, Action<int, int, Item> progress, CancellationToken token)
        {
            var files = Directory.GetFiles(inDir).Where(IsRomFile).OrderBy(f => f).ToList();
            Directory.CreateDirectory(outDir);
            var items = new List<Item>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Work out titles and unique output names up front (cheap), then build in parallel.
            foreach (var f in files)
            {
                var it = new Item { Source = f };
                try
                {
                    string inner;
                    var rom = RomImage.Parse(RomLoader.Load(f, out inner));
                    it.Mapper = rom.Mapper;
                    it.Title = RomLoader.CleanTitle(inner);
                    if (!MapperFactory.IsSupported(rom.Mapper)) it.Error = MapperFactory.Name(rom.Mapper) + " is not supported";
                }
                catch (Exception ex) { it.Error = ex.Message; }

                string name = SafeFileName(it.Title ?? Path.GetFileNameWithoutExtension(f));
                string unique = name;
                for (int n = 2; usedNames.Contains(unique); n++) unique = name + " (" + n + ")";
                usedNames.Add(unique);
                it.Output = Path.Combine(outDir, unique + ".exe");
                items.Add(it);
            }

            int done = 0;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = token };
            try
            {
                Parallel.ForEach(items, opts, it =>
                {
                    if (it.Error == null)
                    {
                        try { BuildOne(it.Source, it.Output, it.Title, 12, () => token.IsCancellationRequested); }
                        catch (Exception ex) { it.Error = ex.Message; }
                    }
                    int d = Interlocked.Increment(ref done);
                    if (progress != null) progress(d, items.Count, it);
                });
            }
            catch (OperationCanceledException) { }

            var log = new List<string> { "Source\tOutput\tResult" };
            foreach (var it in items)
                log.Add(Path.GetFileName(it.Source) + "\t" + Path.GetFileName(it.Output) + "\t" + (it.Error ?? "ok"));
            File.WriteAllLines(Path.Combine(outDir, "AnyNES-batch-log.txt"), log);
            return items;
        }
    }
}
