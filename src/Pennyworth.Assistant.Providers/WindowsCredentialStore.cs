using System.Runtime.InteropServices;
using System.Text;

namespace Pennyworth.Assistant.Providers;

/// <summary>Where API keys live. Never a settings file.</summary>
public interface ICredentialStore
{
    void Save(string profileId, string secret);

    string? Read(string profileId);

    void Delete(string profileId);
}

/// <summary>
/// API keys in the Windows Credential Manager (generic credentials, this user, this PC), under
/// <c>Pennyworth/Assistant/&lt;profile id&gt;</c>. Windows protects them with the user's sign-in.
/// </summary>
public sealed unsafe partial class WindowsCredentialStore(string prefix = WindowsCredentialStore.DefaultPrefix) : ICredentialStore
{
    public const string DefaultPrefix = "Pennyworth/Assistant/";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Save(string profileId, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* target = Target(profileId))
        fixed (char* user = "Pennyworth")
        fixed (byte* data = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(&credential, 0))
            {
                throw new IOException($"Could not save the key (Windows error {Marshal.GetLastPInvokeError()}).");
            }
        }

        Array.Clear(blob);
    }

    public string? Read(string profileId)
    {
        if (!CredRead(Target(profileId), CredTypeGeneric, 0, out Credential* credential))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new IOException($"Could not read the key (Windows error {error}).");
        }

        try
        {
            return credential->CredentialBlobSize == 0
                ? ""
                : Encoding.Unicode.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            CredFree(credential);
        }
    }

    public void Delete(string profileId)
    {
        if (!CredDelete(Target(profileId), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() != ErrorNotFound)
        {
            throw new IOException($"Could not delete the key (Windows error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private string Target(string profileId) => prefix + profileId;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);
}
