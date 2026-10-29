using System.Security.Cryptography;
using MailKit;

namespace ProtonBridgeBridge;

internal static class RawMessages
{
    public static async Task<string> HashAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
    {
        // The bounded folder enforces literal limits; hashing the returned stream avoids a second full-body allocation.
        using var raw = await folder.GetStreamAsync(uid, cancellationToken);
        return Convert.ToHexString(await SHA256.HashDataAsync(raw, cancellationToken));
    }

    public static async Task<byte[]> ReadAsync(IMailFolder folder, UniqueId uid, int maxBytes, CancellationToken cancellationToken)
    {
        using var raw = await folder.GetStreamAsync(uid, cancellationToken);
        if (raw.Length > maxBytes)
        {
            throw new ResourceLimitException();
        }

        var bytes = new byte[checked((int)raw.Length)];
        await raw.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }
}
