using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class HandleProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    // NtQuerySystemInformation SystemExtendedHandleInformation (64)
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    public static string Probe(int pid)
    {
        const int SystemExtendedHandleInformation = 64;
        var results = new Dictionary<ushort, int>();
        for (int retry = 0; retry < 3; retry++)
        {
            int len = 0;
            NtQuerySystemInformation(SystemExtendedHandleInformation, IntPtr.Zero, 0, out len);
            // len is insufficient info; use 16MB buffer
            int bufLen = 32 * 1024 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufLen);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufLen, out len);
                if (status == 0)
                {
                    int count = Marshal.ReadInt32(buffer);
                    int offset = 8; // 8 = header
                    for (int i = 0; i < count; i++)
                    {
                        SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX e = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(
                            IntPtr.Add(buffer, offset + i * Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>()),
                            typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                        if (e.UniqueProcessId.ToInt64() == pid)
                        {
                            results.TryGetValue(e.ObjectTypeIndex, out int c);
                            results[e.ObjectTypeIndex] = c + 1;
                        }
                    }
                    var sb = new StringBuilder();
                    foreach (var kv in results)
                    {
                        sb.AppendFormat("type#{0}={1} ", kv.Key, kv.Value);
                    }
                    return sb.ToString();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return "query failed";
    }
}