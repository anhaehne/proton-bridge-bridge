using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;

namespace ProtonBridgeBridge;

public static class BridgeConnection
{
    /// <summary>Creates a credential-free client with the same TLS policy for sessions and health probes.</summary>
    public static ImapClient Create(Options options)
    {
        var client = new ImapClient
        {
            Timeout = 60000,
            CheckCertificateRevocation = false
        };

        if (options.BridgeTrust is not { } file)
        {
            return client;
        }

        // Store only the public PEM text; each validation disposes its temporary certificate.
        try
        {
            var pem = File.ReadAllText(file);
            client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                using var root = X509Certificate2.CreateFromPem(pem);
                return Trust.Validate(certificate, errors, root);
            };
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
