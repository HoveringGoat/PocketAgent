using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PocketAgent.Desktop;

internal static class CodexCredentialStore
{
    private const string TargetName = "PocketAgent/Codex/PlatformApiKey";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static string? Read()
    {
        if (!IsAvailable || !CredRead(TargetName, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CriticalCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public static bool Exists() => IsAvailable && CredRead(TargetName, CredTypeGeneric, 0, out var pointer) && FreeAndTrue(pointer);

    public static void Save(string value)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("Protected Codex credentials require Windows Credential Manager.");
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The API key cannot be empty.", nameof(value));
        var bytes = Encoding.UTF8.GetBytes(value.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CriticalCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException($"Windows Credential Manager could not save the Codex API key ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Clear()
    {
        if (IsAvailable && !CredDelete(TargetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new InvalidOperationException($"Windows Credential Manager could not remove the Codex API key ({error}).");
        }
    }

    private static bool FreeAndTrue(IntPtr pointer) { CredFree(pointer); return true; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CriticalCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CriticalCredential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
