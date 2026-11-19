using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BetterShot.Updates;

internal enum AuthenticodeResult { Valid, Unsigned, Invalid }

/// <summary>
/// Checks a file's Authenticode signature with WinVerifyTrust (no UI, no revocation network check). A file without a
/// signature reports <see cref="AuthenticodeResult.Unsigned"/>; any signature that is present but fails verification
/// (tampered, untrusted, expired, or distrusted) reports <see cref="AuthenticodeResult.Invalid"/>.
/// </summary>
internal static class AuthenticodeVerifier
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static AuthenticodeResult Check(SafeFileHandle file, string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        IntPtr pathPointer = Marshal.StringToCoTaskMemUni(path);
        IntPtr fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                File = file.DangerousGetHandle()
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify
            };
            Guid action = GenericVerifyV2;
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = WtdStateActionClose;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result switch
            {
                0 => AuthenticodeResult.Valid,
                TrustENoSignature or TrustESubjectFormUnknown => AuthenticodeResult.Unsigned,
                _ => AuthenticodeResult.Invalid
            };
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
            GC.KeepAlive(file);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructSize;
        internal IntPtr FilePath;
        internal IntPtr File;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
