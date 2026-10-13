using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VlcRemote.Companion.Interop;

/// <summary>
/// Командная строка чужого процесса того же пользователя: <c>NtQueryInformationProcess</c>
/// с <c>ProcessCommandLineInformation</c> (Windows 8.1+, достаточно <c>PROCESS_QUERY_LIMITED_INFORMATION</c>) —
/// то же, что <c>Win32_Process.CommandLine</c> в WMI, но без пакета System.Management.
/// </summary>
internal static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Командная строка процесса или <c>null</c> (нет доступа, процесс завершился).</summary>
    public static string? TryGet(int pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } — Buffer выровнен по указателю.
            var bytes = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Разбить командную строку по правилам Windows (<c>CommandLineToArgvW</c>).</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
