using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Program
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr info, int len, out int ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr src, IntPtr handle, IntPtr target, out IntPtr dup, uint access, bool inherit, uint opts);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object; public IntPtr UniqueProcessId; public IntPtr HandleValue;
        public uint GrantedAccess; public ushort CreatorBackTraceIndex; public ushort ObjectTypeIndex;
        public uint HandleAttributes; public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: hprobe <pid> <typeIndex> [countPerSample]"); return 1; }
        long pid = long.Parse(args[0]);
        ushort wantType = ushort.Parse(args[1]);
        int sampleLimit = args.Length > 2 ? int.Parse(args[2]) : 5;

        var samples = new List<IntPtr>();
        int total = 0;
        int countOfType = 0;
        int bufLen = 128 * 1024 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufLen);
        try
        {
            int status = NtQuerySystemInformation(64, buffer, bufLen, out _);
            if (status != 0) { Console.WriteLine("query failed 0x" + status.ToString("X")); return 2; }
            ulong count = (ulong)Marshal.ReadInt64(buffer, 0);
            int offset = 16, size = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            for (ulong i = 0; i < count && offset + (long)i * size + size <= bufLen; i++)
            {
                var e = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                    IntPtr.Add(buffer, offset + (int)i * size), typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                if (e.UniqueProcessId.ToInt64() == pid)
                {
                    total++;
                    if (e.ObjectTypeIndex == wantType)
                    {
                        countOfType++;
                        if (samples.Count < sampleLimit)
                            samples.Add(e.HandleValue);
                    }
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        Console.WriteLine("total_handles=" + total + " count_of_type" + wantType + "=" + countOfType);

        IntPtr proc = OpenProcess(0x40, false, (int)pid);
        if (proc == IntPtr.Zero) { Console.WriteLine("OpenProcess failed err=" + Marshal.GetLastWin32Error()); return 3; }
        foreach (var hv in samples)
        {
            Console.WriteLine("  sample handle=0x" + hv.ToInt64().ToString("X"));
            if (hv.ToInt64() == 0) { Console.WriteLine("    (null handle, skip)"); continue; }
            IntPtr dup;
            if (!DuplicateHandle(proc, hv, GetCurrentProcess(), out dup, 0, false, 2))
            {
                Console.WriteLine("    DuplicateHandle failed err=" + Marshal.GetLastWin32Error());
                continue;
            }
            Console.WriteLine("    dup=0x" + dup.ToInt64().ToString("X"));
            int len = 4096;
            IntPtr ob = Marshal.AllocHGlobal(len);
            try
            {
                int st = NtQueryObject(dup, 1, ob, len, out _);
                Console.WriteLine("    name_class_status=0x" + st.ToString("X"));
                if (st == 0)
                {
                    var u = (UNICODE_STRING)Marshal.PtrToStructure(ob, typeof(UNICODE_STRING));
                    string name = Marshal.PtrToStringUni(u.Buffer, u.Length / 2) ?? "(empty)";
                    Console.WriteLine("    object_name=" + name);
                }
                st = NtQueryObject(dup, 2, ob, len, out _);
                Console.WriteLine("    type_class_status=0x" + st.ToString("X"));
                if (st == 0)
                {
                    var u = (UNICODE_STRING)Marshal.PtrToStructure(ob, typeof(UNICODE_STRING));
                    string name = Marshal.PtrToStringUni(u.Buffer, u.Length / 2) ?? "?";
                    Console.WriteLine("    type_name=" + name);
                }
            }
            finally { Marshal.FreeHGlobal(ob); CloseHandle(dup); }
        }
        CloseHandle(proc);
        return 0;
    }
}