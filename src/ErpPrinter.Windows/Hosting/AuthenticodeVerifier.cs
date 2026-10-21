using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ErpPrinter.Windows.Hosting;

public sealed record SignatureResult(bool Valid, string Status, string? Thumbprint, string? Subject);

/// <summary>Checks a file's Authenticode signature with the Windows trust provider (<c>WinVerifyTrust</c>), like Get-AuthenticodeSignature.</summary>
public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureResult Verify(string path)
    {
        var status = WinVerifyTrustStatus(path);
        string? thumbprint = null, subject = null;
        try
        {
            // SYSLIB0057 steers certificate loading to X509CertificateLoader, but that only reads .cer/.pfx data, not the
            // signer of an Authenticode-signed file; this is the only API that does, so the warning is suppressed here.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            thumbprint = Convert.ToHexString(certificate.GetCertHash());
            subject = certificate.Subject;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            // Unsigned files have no certificate to read; the trust status below already says so.
        }
        return new SignatureResult(status == 0, Describe(status), thumbprint, subject);
    }

    private static string Describe(int status) => status switch
    {
        0 => "Valid",
        unchecked((int)0x800B0100) => "NotSigned",
        unchecked((int)0x800B0101) => "Expired",
        unchecked((int)0x800B0109) => "UntrustedRoot",
        unchecked((int)0x800B0111) => "Explicitly distrusted",
        unchecked((int)0x80096010) => "HashMismatch",
        unchecked((int)0x80096004) => "SignatureMismatch",
        _ => $"Error 0x{status:X8}",
    };

    private static int WinVerifyTrustStatus(string path)
    {
        var fileInfo = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,            // WTD_UI_NONE
                RevocationChecks = 0,    // WTD_REVOKE_NONE: a machine without internet must still be able to update from a share
                UnionChoice = 1,         // WTD_CHOICE_FILE
                FileInfo = fileInfoPtr,
                StateAction = 1,         // WTD_STATEACTION_VERIFY
            };
            var action = GenericVerifyV2;
            try
            {
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                data.StateAction = 2;    // WTD_STATEACTION_CLOSE releases the provider state
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
