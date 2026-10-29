using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtonBridgeBridge;

public sealed class IdentityException : Exception;

public sealed record Fingerprint(uint Uid, string Hash);

public sealed record Mapping(uint SourceValidity, uint SourceUid, uint LabelValidity, uint? LabelUid, string Hash);

public static class Identity
{
    public static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    public static HashSet<uint> Resolve(uint sourceValidity, uint labelValidity,
        IReadOnlyList<Fingerprint> source, IReadOnlyList<Fingerprint> labels, IReadOnlyList<Mapping> mappings, bool allowHashFallback = false)
    {
        var result = new HashSet<uint>();
        var accountedLabels = new HashSet<uint>();
        foreach (var map in mappings.Where(m => m.SourceValidity == sourceValidity && m.LabelValidity == labelValidity))
        {
            var sourceMessage = source.SingleOrDefault(message => message.Uid == map.SourceUid);
            var labelMessage = map.LabelUid is { } labelUid ? labels.SingleOrDefault(message => message.Uid == labelUid) : null;
            if (labelMessage is not null)
            {
                if (labelMessage.Hash != map.Hash)
                {
                    throw new IdentityException();
                }

                // A known label copy remains accounted for even after its source leaves Inbox.
                accountedLabels.Add(labelMessage.Uid);
            }

            if (sourceMessage is null)
            {
                continue;
            }

            if (sourceMessage.Hash != map.Hash)
            {
                throw new IdentityException();
            }

            if (labelMessage is not null)
            {
                result.Add(sourceMessage.Uid);
            }
        }

        var unmatchedLabels = labels.Where(message => !accountedLabels.Contains(message.Uid)).ToArray();
        if (!allowHashFallback && (unmatchedLabels.Length > 0
            || mappings.Any(mapping => mapping.SourceValidity == sourceValidity && mapping.LabelUid is null)))
        {
            throw new IdentityException();
        }

        foreach (var group in unmatchedLabels.GroupBy(message => message.Hash))
        {
            var candidates = source.Where(s => s.Hash == group.Key).ToArray();
            if (candidates.Length > 1)
            {
                throw new IdentityException();
            }

            if (candidates.Length == 1)
            {
                result.Add(candidates[0].Uid);
            }
        }

        // A pending copy of an identical delivery cannot be safely resolved after a crash.
        foreach (var pending in mappings.Where(m => m.LabelUid is null && m.SourceValidity == sourceValidity))
        {
            if (!result.Contains(pending.SourceUid))
            {
                throw new IdentityException();
            }
        }

        return result;
    }
}

public sealed class Journal
{
    private const int MaxJournalBytes = 16 * 1024 * 1024;
    private readonly string _path;

    public List<Mapping> Entries { get; private set; }

    public Journal(string directory, string account, string endpoint, string label)
    {
        Directory.CreateDirectory(directory);
        var key = Identity.Hash(Encoding.UTF8.GetBytes(endpoint + "\0" + account + "\0" + label));
        _path = Path.Combine(directory, key + ".json");
        Entries = [];
        Reload();
    }

    public void ValidateEpoch(uint validity)
    {
        var epochPath = _path + ".epoch";
        if (File.Exists(epochPath))
        {
            if (File.ReadAllText(epochPath) != validity.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new IdentityException();
            }

            return;
        }

        using var file = new FileStream(epochPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Encoding.ASCII.GetBytes(validity.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        file.Flush(true);
    }

    public void Reload()
    {
        if (!File.Exists(_path))
        {
            Entries = [];
            return;
        }

        try
        {
            using var file = File.OpenRead(_path);
            if (file.Length > MaxJournalBytes)
            {
                throw new IdentityException();
            }

            var entries = JsonSerializer.Deserialize<List<Mapping>>(file, new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new IdentityException();
            foreach (var entry in entries)
            {
                Validate(entry);
            }

            if (entries.Select(entry => (entry.SourceValidity, entry.SourceUid)).Distinct().Count() != entries.Count)
            {
                throw new IdentityException();
            }

            Entries = entries;
        }
        catch (JsonException)
        {
            throw new IdentityException();
        }
    }

    private static void Validate(Mapping? entry)
    {
        if (entry is null || entry.SourceValidity == 0 || entry.SourceUid == 0 || entry.LabelValidity == 0 || entry.LabelUid == 0
            || entry.Hash is null || entry.Hash.Length != 64 || !entry.Hash.All(Uri.IsHexDigit))
        {
            throw new IdentityException();
        }
    }

    public void Save(Mapping entry)
    {
        Validate(entry);
        Reload();
        Entries.RemoveAll(m => m.SourceValidity == entry.SourceValidity && m.SourceUid == entry.SourceUid);
        Entries.Add(entry);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Entries);
        if (bytes.Length > MaxJournalBytes)
        {
            throw new IdentityException();
        }

        var temp = _path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }

        File.Move(temp, _path, true);
    }
}
