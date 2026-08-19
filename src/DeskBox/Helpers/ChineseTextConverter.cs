using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

internal static class ChineseTextConverter
{
    private const uint SimplifiedChinese = 0x02000000;
    private const uint TraditionalChinese = 0x04000000;

    public static string ToTraditional(string? value) => Convert(value, TraditionalChinese);

    public static string ToSimplified(string? value) => Convert(value, SimplifiedChinese);

    private static string Convert(string? value, uint mapFlag)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        int requiredLength = LCMapStringEx(
            "zh-CN",
            mapFlag,
            value,
            value.Length,
            null,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (requiredLength <= 0)
        {
            return value;
        }

        var result = new StringBuilder(requiredLength);
        int written = LCMapStringEx(
            "zh-CN",
            mapFlag,
            value,
            value.Length,
            result,
            result.Capacity,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        return written > 0 ? result.ToString() : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        StringBuilder? destination,
        int destinationLength,
        IntPtr versionInformation,
        IntPtr reserved,
        IntPtr sortHandle);
}
