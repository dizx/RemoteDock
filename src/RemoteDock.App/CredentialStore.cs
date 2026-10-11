using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDock.App;

internal sealed record SavedCredential(string UserName, string Password);

/// <summary>Passwords live in the current user's Windows Credential Manager, not the connection file.</summary>
internal static class CredentialStore
{
    public static string ConnectionTarget(Guid id) => $"RemoteDock/Connection/{id:D}";
    public static string GroupTarget(Guid id) => $"RemoteDock/Group/{id:D}";

    public static SavedCredential? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Could not read the saved Windows credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            string password = credential.BlobSize == 0 ? "" : Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)) ?? "";
            return new(credential.UserName ?? "", password);
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string target, string userName, string password)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(password);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, UserName = userName,
                Comment = "RemoteDock saved connection credential", Persist = 2,
                BlobSize = (uint)bytes.Length, Blob = buffer
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the password in Windows Credential Manager.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void Delete(string target)
    {
        if (CredDelete(target, 1, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error, "Could not remove the saved Windows credential.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
