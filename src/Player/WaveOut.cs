using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AnyNes.Player
{
    /// <summary>
    /// Minimal winmm streaming output. Write() blocks while all buffers are queued, which makes the
    /// audio device the master clock for emulation speed.
    /// </summary>
    sealed class WaveOut : IDisposable
    {
        const int BufferCount = 4;
        const int BufferSamples = 2048;
        const int WHDR_DONE = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx
        {
            public short wFormatTag, nChannels;
            public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr lpData;
            public int dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int deviceId, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        IntPtr hwo;
        readonly IntPtr[] headers = new IntPtr[BufferCount];
        readonly bool[] queued = new bool[BufferCount];
        readonly int hdrSize = Marshal.SizeOf(typeof(WaveHdr));
        readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwFlags");
        int next;

        public WaveOut(int sampleRate)
        {
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = sampleRate,
                nAvgBytesPerSec = sampleRate * 2, nBlockAlign = 2, wBitsPerSample = 16
            };
            int err = waveOutOpen(out hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0) throw new InvalidOperationException("waveOutOpen failed: " + err);
            for (int i = 0; i < BufferCount; i++)
            {
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                var h = new WaveHdr { lpData = Marshal.AllocHGlobal(BufferSamples * 2) };
                Marshal.StructureToPtr(h, headers[i], false);
            }
            timeBeginPeriod(1);
        }

        bool IsDone(int i) { return (Marshal.ReadInt32(headers[i], flagsOffset) & WHDR_DONE) != 0; }

        /// <summary>Queues samples, first waiting (up to ~200 ms) for a free buffer.</summary>
        public void Write(short[] samples, int count)
        {
            if (hwo == IntPtr.Zero) return;
            count = Math.Min(count, BufferSamples);
            int i = next;
            if (queued[i])
            {
                for (int spin = 0; spin < 200 && !IsDone(i); spin++) Thread.Sleep(1);
                waveOutUnprepareHeader(hwo, headers[i], hdrSize);
                queued[i] = false;
            }
            var h = (WaveHdr)Marshal.PtrToStructure(headers[i], typeof(WaveHdr));
            Marshal.Copy(samples, 0, h.lpData, count);
            h.dwBufferLength = count * 2;
            h.dwFlags = 0;
            Marshal.StructureToPtr(h, headers[i], false);
            waveOutPrepareHeader(hwo, headers[i], hdrSize);
            waveOutWrite(hwo, headers[i], hdrSize);
            queued[i] = true;
            next = (next + 1) % BufferCount;
        }

        /// <summary>Number of buffers still waiting to be played.</summary>
        public int Pending
        {
            get
            {
                int n = 0;
                for (int i = 0; i < BufferCount; i++) if (queued[i] && !IsDone(i)) n++;
                return n;
            }
        }

        public void Dispose()
        {
            if (hwo == IntPtr.Zero) return;
            waveOutReset(hwo);
            for (int i = 0; i < BufferCount; i++)
            {
                if (queued[i]) waveOutUnprepareHeader(hwo, headers[i], hdrSize);
                var h = (WaveHdr)Marshal.PtrToStructure(headers[i], typeof(WaveHdr));
                Marshal.FreeHGlobal(h.lpData);
                Marshal.FreeHGlobal(headers[i]);
            }
            waveOutClose(hwo);
            hwo = IntPtr.Zero;
            timeEndPeriod(1);
        }
    }
}
