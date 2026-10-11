using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteDock.Core;

namespace RemoteDock.App;

internal static class WindowsDataProtection
{
    public static string UnprotectRdpPassword(byte[] encrypted)
    {
        var input = new DataBlob { Size = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        var output = new DataBlob();
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new ImportPasswordException("Windows cannot decrypt this .rdp password for the current account. Import it on the original account, or uncheck password import.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Size % 2 != 0) throw new ImportPasswordException("The saved RDP password has an unsupported encoding.");
            return (Marshal.PtrToStringUni(output.Data, output.Size / 2) ?? "").TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
