using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class Watch
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);

    [StructLayout(LayoutKind.Sequential)]
    private struct H
    {
        public IntPtr Object; public IntPtr UniqueProcessId; public IntPtr HandleValue;
        public uint GrantedAccess; public ushort CreatorBackTraceIndex; public ushort ObjectTypeIndex;
        public uint HandleAttributes; public uint Reserved;
    }

    public static (int total, int evt, int sem, int timer, int wcp, int t56, int file) Probe(long pid)
    {
        int bufLen = 128 * 1024 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufLen);
        try
        {
            NtQuerySystemInformation(64, buffer, bufLen, out _);
            ulong count = (ulong)Marshal.ReadInt64(buffer, 0);
            int size = Marshal.SizeOf<H>();
            int total = 0, evt = 0, sem = 0, timer = 0, wcp = 0, t56 = 0, file = 0;
            for (ulong i = 0; i < count; i++)
            {
                var h = (H)Marshal.PtrToStructure(IntPtr.Add(buffer, 16 + (int)i * size), typeof(H));
                if (h.UniqueProcessId.ToInt64() != pid) continue;
                total++;
                switch (h.ObjectTypeIndex)
                {
                    case 21: evt++; break;
                    case 24: sem++; break;
                    case 25: timer++; break;
                    case 41: wcp++; break;
                    case 56: t56++; break;
                    case 42: file++; break;
                }
            }
            return (total, evt, sem, timer, wcp, t56, file);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: typewatch <pid> <intervalMs> <csv>"); return 1; }
        long pid = long.Parse(args[0]);
        int intervalMs = int.Parse(args[1]);
        string csv = args[2];
        using var fs = new StreamWriter(csv, true);
        while (true)
        {
            var r = Probe(pid);
            var p = System.Diagnostics.Process.GetProcessById((int)pid);
            fs.WriteLine($"{DateTime.Now:HH:mm:ss},{r.total},{r.evt},{r.sem},{r.timer},{r.wcp},{r.t56},{r.file},{p.WorkingSet64 / 1024 / 1024},{p.PrivateMemorySize64 / 1024 / 1024}");
            fs.Flush();
            Thread.Sleep(intervalMs);
        }
    }
}