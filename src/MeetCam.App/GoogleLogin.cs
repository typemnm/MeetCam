using System.Runtime.InteropServices;
using System.Text;

namespace MeetCam;

// The email is a local file. The password stays in Windows Credential Manager, never in meeting.json.
internal static class GoogleLogin
{
    private const string Target = "MeetCam.Google";
    private const int CredentialType = 1;
    private const int PersistLocalMachine = 2;
    private const int MaxBlobBytes = 2560;

    public static bool TryLoad(out string email, out string password)
    {
        email = "";
        password = "";
        try
        {
            if (File.Exists(EmailPath))
                email = File.ReadAllText(EmailPath).Trim();
            if (!CredRead(Target, CredentialType, 0, out var pointer))
                return email.Length > 0;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (email.Length == 0 && !string.IsNullOrWhiteSpace(credential.UserName))
                    email = credential.UserName.Trim();
                if (credential.CredentialBlob != IntPtr.Zero
                    && credential.CredentialBlobSize > 0
                    && credential.CredentialBlobSize <= MaxBlobBytes)
                {
                    var bytes = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    password = Encoding.UTF8.GetString(bytes);
                    Array.Clear(bytes, 0, bytes.Length);
                }
            }
            finally
            {
                CredFree(pointer);
            }

            return email.Length > 0 || password.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            email = "";
            password = "";
            return false;
        }
    }

    public static bool Save(string email, string password)
    {
        try
        {
            email = email.Trim();
            var directory = Path.GetDirectoryName(EmailPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            if (email.Length == 0)
            {
                if (File.Exists(EmailPath))
                    File.Delete(EmailPath);
                CredDelete(Target, CredentialType, 0);
                return true;
            }

            File.WriteAllText(EmailPath, email);
            if (password.Length == 0)
            {
                CredDelete(Target, CredentialType, 0);
                return true;
            }

            var bytes = Encoding.UTF8.GetBytes(password);
            if (bytes.Length == 0 || bytes.Length > MaxBlobBytes)
                return false;
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = CredentialType,
                    TargetName = Target,
                    UserName = email,
                    CredentialBlob = blob,
                    CredentialBlobSize = bytes.Length,
                    Persist = PersistLocalMachine,
                    Comment = "MeetCam",
                };
                return CredWrite(ref credential, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string EmailPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetCam",
        "google-account.txt");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
}
