using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Hearth.Core;

/// <summary>Persistent, private host identity. A damaged or expired identity is never silently replaced.</summary>
public static class HostCertificate
{
    public static string FilePath(string dataDirectory) => Path.Combine(Path.GetFullPath(dataDirectory), "host-tls", "identity.pfx");
    public static string GetPin(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
    public static string NormalizePin(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("The host certificate identity is invalid. Copy a fresh invite or host connection link from its owner.");
        return value.ToLowerInvariant();
    }

    public static X509Certificate2 LoadOrCreate(string dataDirectory)
    {
        var file = FilePath(dataDirectory);
        var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The host certificate directory must not be a link.");
        ProtectDirectory(directory);
        if (!File.Exists(file))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Hearth host", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            names.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = generated.Export(X509ContentType.Pfx);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    ProtectFile(temporary);
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                try { File.Move(temporary, file, false); }
                catch (IOException) when (File.Exists(file)) { /* Another launch created the identity first; use that exact identity. */ }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return LoadExisting(dataDirectory);
    }

    /// <summary>Loads an existing identity for comparison; never generates or replaces one.</summary>
    public static X509Certificate2 LoadExisting(string dataDirectory)
    {
        var file = FilePath(dataDirectory);
        if (!File.Exists(file)) throw new FileNotFoundException("The saved host certificate is missing. Its identity cannot be verified.", file);
        if ((File.GetAttributes(Path.GetDirectoryName(file)!) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The host certificate directory must not be a link.");
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("The host certificate must not be a link.");
        ProtectFile(file);
        X509Certificate2 certificate;
        try
        {
            // Windows Schannel cannot serve TLS with an ephemeral imported private key.
            // UserKeySet uses only this account's key container; without PersistKeySet
            // .NET removes that temporary import when the certificate is disposed.
            // This does not install a certificate or trust root in any Windows store.
            var storage = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(file, null, storage);
        }
        catch (CryptographicException exception)
        {
            throw new IOException("The saved host certificate cannot be read. Restore the host-tls folder from a backup; Hearth will not replace your host identity automatically.", exception);
        }
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new IOException("The saved host certificate is expired, not yet valid, or missing its private key. Check this PC's clock or restore host-tls from a backup. Its identity has not been changed.");
        }
        return certificate;
    }

    private static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) ProtectWindows(path, true);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows()) ProtectWindows(path, false);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(string path, bool directory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows could not identify the host certificate owner.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var principal in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
}
