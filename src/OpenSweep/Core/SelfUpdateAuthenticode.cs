using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OpenSweep.Core
{
    internal static class SelfUpdateAuthenticode
    {
        static readonly Guid VerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>Called with a held file handle that denies writing and deletion.</summary>
        internal static string GetTrustedPublicKey(FileStream file)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new InvalidOperationException("Windows signature verification is required.");
            var info = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                FilePath = file.Name,
                FileHandle = file.SafeFileHandle.DangerousGetHandle()
            };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(info, memory, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = memory,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x80 // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT; do not disable revocation
            };
            var action = VerifyV2;
            try
            {
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (result != 0) throw new InvalidDataException($"Windows could not establish a trusted, non-revoked signature for this file (0x{result:X8}).");
                // Read the certificate from the chain actually accepted by WinVerifyTrust.
                // CreateFromSignedFile could select a different embedded signature in a dual-signed PE.
                IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
                IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                IntPtr certificateInfo = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
                if (certificateInfo == IntPtr.Zero) throw new InvalidDataException("Windows did not return the verified signer certificate.");
                var verifiedCertificate = (ProviderCertificate)Marshal.PtrToStructure(certificateInfo, typeof(ProviderCertificate));
                if (verifiedCertificate.CertificateContext == IntPtr.Zero) throw new InvalidDataException("The verified signer certificate is unavailable.");
                using (var certificate = new X509Certificate2(verifiedCertificate.CertificateContext))
                {
                    bool codeSigning = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                        .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Any(o => o.Value == "1.3.6.1.5.5.7.3.3");
                    if (!codeSigning) throw new InvalidDataException("The signer does not explicitly allow code signing.");
                    // Pin the algorithm and public key, not a mutable publisher display name.
                    using (var identity = new MemoryStream())
                    using (var writer = new BinaryWriter(identity, Encoding.UTF8, true))
                    using (var sha = SHA256.Create())
                    {
                        writer.Write(certificate.PublicKey.Oid.Value ?? "");
                        var parameters = certificate.PublicKey.EncodedParameters.RawData;
                        var key = certificate.PublicKey.EncodedKeyValue.RawData;
                        writer.Write(parameters.Length); writer.Write(parameters);
                        writer.Write(key.Length); writer.Write(key); writer.Flush();
                        return BitConverter.ToString(sha.ComputeHash(identity.ToArray())).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE releases provider state even after failed verification.
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.DestroyStructure(memory, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(memory);
                GC.KeepAlive(file);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustData
        {
            public uint Size;
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

        [StructLayout(LayoutKind.Sequential)]
        struct ProviderCertificate
        {
            public uint Size;
            public IntPtr CertificateContext;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificateIndex);

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, ref WinTrustData data);
    }
}
