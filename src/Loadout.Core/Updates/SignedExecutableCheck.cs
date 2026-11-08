using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Loadout.Updates;

/// <summary>Says whether a downloaded Loadout may replace this one.</summary>
public interface IExecutableCheck
{
    /// <summary>Null when the file is fit to run, or why it isn't, in words for the user.</summary>
    string? Problem(string executable, AppVersion version);
}

/// <summary>
/// A downloaded LoadoutPS2.exe must carry a valid Authenticode signature (Windows' own check, as for any signed
/// program), made with a certificate in the publisher's name, and be the version the release says. The name is
/// checked rather than the certificate itself, so a renewed certificate still passes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SignedExecutableCheck : IExecutableCheck
{
    public const string Publisher = "Open Source Developer Ivan Vazharov";

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    private readonly string _publisher;

    public SignedExecutableCheck(string publisher = Publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        _publisher = publisher;
    }

    public string? Problem(string executable, AppVersion version)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(version);
        var trust = WinTrust.Verify(executable);
        if (trust != 0)
        {
            return trust switch
            {
                TrustENoSignature or TrustESubjectFormUnknown => "The download is not signed.",
                TrustEBadDigest => "The download was changed after it was signed.",
                _ => "Windows doesn't trust the download's signature (0x" + trust.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").",
            };
        }

        string? signer;
        try
        {
#pragma warning disable SYSLIB0057 // The loader has no way to read a program's signature; this is the API for it.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable));
#pragma warning restore SYSLIB0057
            signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "The download's signature could not be read.";
        }

        if (!string.Equals(signer, _publisher, StringComparison.Ordinal))
        {
            return "The download is signed by " + (string.IsNullOrEmpty(signer) ? "someone else" : signer) + ", not by " + _publisher + ".";
        }

        var product = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        if (!AppVersion.TryParse(product, out var actual) || actual != version)
        {
            return "The download is version " + (string.IsNullOrEmpty(product) ? "unknown" : product) + ", not " + version + ".";
        }

        return null;
    }

    // WinVerifyTrust with the generic Authenticode policy, no UI and no network (revocation is not checked).
    private static class WinTrust
    {
        private const uint UiNone = 2;
        private const uint RevokeNone = 0;
        private const uint ChoiceFile = 1;
        private const uint StateVerify = 1;
        private const uint StateClose = 2;
        private const uint CacheOnlyUrlRetrieval = 0x1000;

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static int Verify(string path)
        {
            var pathText = Marshal.StringToHGlobalUni(path);
            var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), FilePath = pathText }, fileInfo, false);
                var data = new Data
                {
                    Size = (uint)Marshal.SizeOf<Data>(),
                    UiChoice = UiNone,
                    RevocationChecks = RevokeNone,
                    UnionChoice = ChoiceFile,
                    File = fileInfo,
                    StateAction = StateVerify,
                    ProvFlags = CacheOnlyUrlRetrieval,
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                data.StateAction = StateClose;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfo);
                Marshal.FreeHGlobal(pathText);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref Data data);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Data
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProvFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}
