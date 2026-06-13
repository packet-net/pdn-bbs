using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Bbs.Smtp;

/// <summary>
/// Resolves the X.509 certificate for the implicit-TLS SMTP submission listener: loads an
/// operator-supplied PKCS#12, or — when none is configured and self-signed generation is enabled —
/// generates a self-signed certificate on first start and persists it (a PKCS#12) so it is stable
/// across restarts. The twin of <c>ImapTlsCertificate</c>: RSA-2048 / SHA-256, the serverAuth EKU,
/// and a SAN of <c>localhost</c> + the machine name + the bind address.
/// </summary>
/// <remarks>
/// Every path that cannot produce a usable certificate returns null (logged), so the caller skips the
/// TLS listener rather than crashing — a TLS misconfiguration disables the (opt-in) feature, it does
/// not take the BBS down. A generated cert encrypts the channel but is untrusted; an operator who wants
/// a trusted cert (so an iPhone connects without a warning) supplies one via the certificate path.
/// </remarks>
public static partial class SmtpTlsCertificate
{
    // serverAuth EKU OID — a TLS client expects the server cert to assert it.
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Resolves the SMTP TLS certificate, or null when none can be produced. <paramref name="certificatePath"/>
    /// (when set) is an operator PKCS#12 and wins; otherwise a self-signed cert is generated and persisted at
    /// <paramref name="selfSignedPath"/> when <paramref name="generateSelfSigned"/>. <paramref name="bindAddress"/>
    /// is added to the SAN so a client reaching the node by that IP validates the name.
    /// </summary>
    public static X509Certificate2? Resolve(
        string? certificatePath,
        string? certificatePassword,
        bool generateSelfSigned,
        string selfSignedPath,
        string bindAddress,
        TimeProvider clock,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        try
        {
            // 1) Operator-supplied cert wins.
            if (!string.IsNullOrWhiteSpace(certificatePath))
            {
                if (!File.Exists(certificatePath))
                {
                    if (logger is not null)
                    {
                        LogSuppliedCertMissing(logger, certificatePath);
                    }

                    return null;
                }

                return X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword);
            }

            // 2) No supplied cert and generation disabled → can't serve TLS.
            if (!generateSelfSigned)
            {
                if (logger is not null)
                {
                    LogNoCertConfigured(logger);
                }

                return null;
            }

            // 3) Reuse a persisted self-signed cert while it is still valid; else (re)generate.
            if (File.Exists(selfSignedPath))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(selfSignedPath, null);
                if (existing.NotAfter.ToUniversalTime() > clock.GetUtcNow().UtcDateTime.AddDays(1))
                {
                    return existing;
                }

                existing.Dispose();
                if (logger is not null)
                {
                    LogSelfSignedExpired(logger, selfSignedPath);
                }
            }

            return GenerateAndPersist(selfSignedPath, bindAddress, clock, logger);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            if (logger is not null)
            {
                LogResolveFault(logger, ex);
            }

            return null;
        }
    }

    private static X509Certificate2 GenerateAndPersist(
        string path, string bindAddress, TimeProvider clock, ILogger? logger)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            san.AddDnsName(Environment.MachineName);
        }

        if (IPAddress.TryParse(bindAddress, out IPAddress? ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
        {
            san.AddIpAddress(ip);
        }
        else if (!string.IsNullOrWhiteSpace(bindAddress) && !IPAddress.TryParse(bindAddress, out _))
        {
            san.AddDnsName(bindAddress);
        }

        request.CertificateExtensions.Add(san.Build());

        var now = clock.GetUtcNow();
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));

        // Persist as a PKCS#12 (cert + key) and reload from those bytes so the returned cert's key is
        // backed identically to a fresh-start load (avoids ephemeral-key quirks on reuse next run).
        byte[] pfx = cert.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, pfx);
        TrySetOwnerOnlyPermissions(path);

        DateTime notAfter = cert.NotAfter.ToUniversalTime();
        if (logger is not null)
        {
            LogGenerated(logger, path, notAfter);
        }

        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    // The PKCS#12 holds the private key — keep it owner-only (best-effort; no-op off Unix).
    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; the state directory is already owner-restricted.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP TLS: configured certificatePath '{Path}' does not exist; TLS listener not started.")]
    private static partial void LogSuppliedCertMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP TLS: no certificatePath and generateSelfSigned is false; TLS listener not started.")]
    private static partial void LogNoCertConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP TLS: persisted self-signed cert at '{Path}' expired; regenerating.")]
    private static partial void LogSelfSignedExpired(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP TLS: generated a self-signed certificate at '{Path}' (valid until {NotAfter:u}); clients warn until it is trusted.")]
    private static partial void LogGenerated(ILogger logger, string path, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "SMTP TLS: failed to resolve a certificate; TLS listener not started.")]
    private static partial void LogResolveFault(ILogger logger, Exception ex);
}
