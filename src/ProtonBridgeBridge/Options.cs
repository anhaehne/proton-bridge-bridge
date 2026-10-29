using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailKit.Security;

namespace ProtonBridgeBridge;

public sealed record Options
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; } = 1993;
    public string HealthListenAddress { get; init; } = "127.0.0.1";
    public int HealthPort { get; init; } = 8080;
    public required string Certificate { get; init; }
    public required string PrivateKey { get; init; }
    public string BridgeHost { get; init; } = "localhost";
    public int BridgePort { get; init; } = 1143;
    public string BridgeTls { get; init; } = "StartTls";
    public string? BridgeTrust { get; init; }
    public string Label { get; init; } = "Paperless-imported";
    public bool AllowHashFallback { get; init; }
    public bool AllowLabelWrites { get; init; }
    public uint? AllowedWriteUidValidity { get; init; }
    public uint[] AllowedWriteUids { get; init; } = [];
    public uint[] PreflightUids { get; init; } = [];
    public long MaxScanBytes { get; init; } = 256L * 1024 * 1024;
    public string StateDirectory { get; init; } = "state";
    public int MaxMessageBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxMailboxMessages { get; init; } = 10000;
    public int MaxConnections { get; init; } = 8;
    public int SessionTimeoutSeconds { get; init; } = 300;

    public static Options Load(string path) => JsonSerializer.Deserialize<Options>(File.ReadAllText(path), new JsonSerializerOptions
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    }) ?? throw new InvalidDataException("Invalid proxy configuration.");

    public void Validate()
    {
        if (BridgeTls is not ("StartTls" or "SslOnConnect") || ListenPort is < 1 or > 65535 || BridgePort is < 1 or > 65535
            || HealthPort is < 0 or > 65535
            || string.IsNullOrWhiteSpace(Label) || Label.StartsWith('\\') || Label.Any(char.IsControl)
            || Label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || !IPAddress.TryParse(ListenAddress, out _) || !IPAddress.TryParse(HealthListenAddress, out _)
            || string.IsNullOrWhiteSpace(BridgeHost) || string.IsNullOrWhiteSpace(StateDirectory)
            || AllowedWriteUids is null || PreflightUids is null
            || AllowLabelWrites && AllowedWriteUids.Length > 0 && AllowedWriteUidValidity is not > 0
            || AllowedWriteUids.Any(uid => uid == 0) || PreflightUids.Any(uid => uid == 0)
            || AllowedWriteUids.Distinct().Count() != AllowedWriteUids.Length || PreflightUids.Distinct().Count() != PreflightUids.Length
            || MaxConnections < 1 || MaxMessageBytes < 1 || MaxScanBytes < 1 || MaxMailboxMessages < 1 || SessionTimeoutSeconds < 1)
        {
            throw new InvalidDataException("Invalid proxy configuration.");
        }
    }

    public SecureSocketOptions SocketOptions => Enum.Parse<SecureSocketOptions>(BridgeTls);
}

public static class Trust
{
    // A custom root changes trust anchors only; hostname, validity and chain checks remain mandatory.
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);

        // Local Bridge certificates have no revocation endpoint.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}
