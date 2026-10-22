using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AnyNes.Core
{
    /// <summary>
    /// The game data embedded into a generated exe as an RT_RCDATA resource named <see cref="ResourceName"/>.
    /// </summary>
    public sealed class GamePackage
    {
        public const string ResourceName = "ANYNESGAME";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("ANYNES01");

        public string Title;
        public string RomName;
        public byte[] Rom;
        /// <summary>Plug a Zapper (mouse-aimed) into port 2 by default.</summary>
        public bool Zapper;
        /// <summary>Console timing; null (packages from older builders) means auto-detect.</summary>
        public Region? Region;

        const byte FlagZapper = 1, FlagPal = 2, FlagRegionSet = 4;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Title ?? "");
                w.Write(RomName ?? "");
                w.Write(Rom.Length);
                w.Write(Rom);
                int flags = Zapper ? FlagZapper : 0;
                if (Region.HasValue) flags |= FlagRegionSet | (Region.Value == Core.Region.Pal ? FlagPal : 0);
                w.Write((byte)flags);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static GamePackage Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                var magic = r.ReadBytes(Magic.Length);
                for (int i = 0; i < Magic.Length; i++)
                    if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new InvalidDataException("Bad game package.");
                var p = new GamePackage();
                p.Title = r.ReadString();
                p.RomName = r.ReadString();
                p.Rom = r.ReadBytes(r.ReadInt32());
                if (ms.Position < ms.Length)
                {
                    int flags = r.ReadByte();
                    p.Zapper = (flags & FlagZapper) != 0;
                    if ((flags & FlagRegionSet) != 0) p.Region = (flags & FlagPal) != 0 ? Core.Region.Pal : Core.Region.Ntsc;
                }
                return p;
            }
        }

        /// <summary>Returns the package embedded in the running exe, or null for the bare player stub.</summary>
        public static GamePackage FromCurrentExe()
        {
            IntPtr module = GetModuleHandle(null);
            IntPtr res = FindResource(module, ResourceName, (IntPtr)10);
            if (res == IntPtr.Zero) return null;
            int size = SizeofResource(module, res);
            IntPtr handle = LoadResource(module, res);
            IntPtr ptr = LockResource(handle);
            if (ptr == IntPtr.Zero || size <= 0) return null;
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, size);
            return Deserialize(data);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResource(IntPtr module, string name, IntPtr type);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll")] static extern int SizeofResource(IntPtr module, IntPtr res);
    }
}
