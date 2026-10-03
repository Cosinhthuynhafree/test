using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HoshinoTransfer.Windows.Services;

public interface ICredentialVault
{
    string? Read();
    void Write(string value);
    void Delete();
}

/// <summary>Per-Windows-user Credential Manager storage; Windows protects the secret with DPAPI.</summary>
public sealed class CredentialVault : ICredentialVault
{
    private const string Target = "HoshinoTransfer.Session.v1";
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public NativeFileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
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

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    public string? Read()
    {
        if (!CredRead(Target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the HoshinoTransfer session.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 2560) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length is 0 or > 2560) throw new ArgumentOutOfRangeException(nameof(value), "Credential payload is outside the supported size.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var structure = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = Target,
                Comment = "Encrypted Windows user-scoped HoshinoTransfer session",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName,
            };
            Marshal.StructureToPtr(credential, structure, false);
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the HoshinoTransfer session.");
        }
        finally
        {
            Marshal.FreeHGlobal(structure);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete()
    {
        if (CredDelete(Target, GenericCredential, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error, "Windows Credential Manager could not remove the HoshinoTransfer session.");
    }
}
