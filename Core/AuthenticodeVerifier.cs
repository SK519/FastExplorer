using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public enum SignatureVerificationResult
    {
        Valid,
        NotSigned,
        Tampered,
        UntrustedRoot,
        Expired,
        ExplicitlyDistrusted,
        Invalid
    }

    public static class AuthenticodeVerifier
    {
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_STATEACTION_IGNORE = 0;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00000040;
        private const uint WTD_SAFER_FLAG = 0x00000100;

        private const int ERROR_SUCCESS = 0;
        private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
        private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
        private const int TRUST_E_BAD_DIGEST = unchecked((int)0x800B010D);
        private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
        private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
        private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public nint hFile;
            public nint pgKnownSubject;

            public WINTRUST_FILE_INFO(string filePath)
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
                pcwszFilePath = filePath;
                hFile = nint.Zero;
                pgKnownSubject = nint.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public nint pPolicyCallbackData;
            public nint pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public nint pFile;
            public uint dwStateAction;
            public nint hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public nint pSignatureSettings;

            public WINTRUST_DATA(nint pFileInfo)
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
                pPolicyCallbackData = nint.Zero;
                pSIPClientData = nint.Zero;
                dwUIChoice = WTD_UI_NONE;
                fdwRevocationChecks = WTD_REVOKE_NONE;
                dwUnionChoice = WTD_CHOICE_FILE;
                pFile = pFileInfo;
                dwStateAction = WTD_STATEACTION_IGNORE;
                hWVTStateData = nint.Zero;
                pwszURLReference = null;
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_SAFER_FLAG;
                dwUIContext = 0;
                pSignatureSettings = nint.Zero;
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            nint hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
            nint pWVTData);

        public static SignatureVerificationResult VerifyFile(string filePath, out string statusDescription)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                statusDescription = "ファイルが存在しません。";
                return SignatureVerificationResult.Invalid;
            }

            var fileInfo = new WINTRUST_FILE_INFO(filePath);
            nint pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            nint pWvtData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());

            try
            {
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);
                var wvtData = new WINTRUST_DATA(pFileInfo);
                Marshal.StructureToPtr(wvtData, pWvtData, false);

                int hr = WinVerifyTrust(nint.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pWvtData);

                switch (hr)
                {
                    case ERROR_SUCCESS:
                        statusDescription = "このデジタル署名は問題ありません。";
                        return SignatureVerificationResult.Valid;

                    case TRUST_E_NOSIGNATURE:
                    case TRUST_E_PROVIDER_UNKNOWN:
                        statusDescription = "ファイルにデジタル署名が見つかりません。";
                        return SignatureVerificationResult.NotSigned;

                    case TRUST_E_BAD_DIGEST:
                        statusDescription = "改ざんの可能性があります（ダイジェストが不一致）。";
                        return SignatureVerificationResult.Tampered;

                    case CERT_E_UNTRUSTEDROOT:
                        statusDescription = "信頼されていないルート証明書で署名されています。";
                        return SignatureVerificationResult.UntrustedRoot;

                    case CERT_E_EXPIRED:
                        statusDescription = "証明書の有効期限が切れています。";
                        return SignatureVerificationResult.Expired;

                    case TRUST_E_EXPLICIT_DISTRUST:
                        statusDescription = "証明書または署名者が明示的に信頼されていません。";
                        return SignatureVerificationResult.ExplicitlyDistrusted;

                    default:
                        statusDescription = $"署名検証エラー (コード: 0x{hr:X8})";
                        return SignatureVerificationResult.Invalid;
                }
            }
            catch (Exception ex)
            {
                statusDescription = $"検証処理の例外: {ex.Message}";
                return SignatureVerificationResult.Invalid;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFileInfo);
                Marshal.FreeHGlobal(pFileInfo);

                Marshal.DestroyStructure<WINTRUST_DATA>(pWvtData);
                Marshal.FreeHGlobal(pWvtData);
            }
        }
    }
}
