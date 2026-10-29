using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProtonBridgeProxy;

public sealed class IdentityException : Exception;
public sealed record Fingerprint(uint Uid, string Hash);
public sealed record Mapping(uint SourceValidity, uint SourceUid, uint LabelValidity, uint? LabelUid, string Hash);

public static class Identity
{
    public static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    public static HashSet<uint> Resolve(uint sourceValidity, uint labelValidity,
        IReadOnlyList<Fingerprint> source, IReadOnlyList<Fingerprint> labels, IReadOnlyList<Mapping> mappings)
    {
        var result = new HashSet<uint>();
        var accountedLabels = new HashSet<uint>();
        foreach (var map in mappings.Where(m => m.SourceValidity == sourceValidity && m.LabelValidity == labelValidity))
        {
            var s = source.SingleOrDefault(s => s.Uid == map.SourceUid);
            if (s is null) continue;
            if (s.Hash != map.Hash) throw new IdentityException();
            if (map.LabelUid is { } uid)
            {
                var l = labels.SingleOrDefault(l => l.Uid == uid);
                if (l is null) continue; // Label removed externally: source is eligible again.
                if (l.Hash != s.Hash) throw new IdentityException();
                result.Add(s.Uid);
                accountedLabels.Add(l.Uid);
            }
        }
        foreach (var group in labels.Where(l => !accountedLabels.Contains(l.Uid)).GroupBy(l => l.Hash))
        {
            var candidates = source.Where(s => s.Hash == group.Key).ToArray();
            if (candidates.Length > 1) throw new IdentityException();
            if (candidates.Length == 1) result.Add(candidates[0].Uid);
        }
        // A pending copy of an identical delivery cannot be safely resolved after a crash.
        foreach (var pending in mappings.Where(m => m.LabelUid is null && m.SourceValidity == sourceValidity))
            if (!result.Contains(pending.SourceUid)) throw new IdentityException();
        return result;
    }
}

public sealed class Journal
{
    private readonly string path;
    public List<Mapping> Entries { get; private set; }
    public Journal(string directory, string account, string endpoint, string label)
    {
        Directory.CreateDirectory(directory);
        var key = Identity.Hash(Encoding.UTF8.GetBytes(endpoint + "\0" + account + "\0" + label));
        path = Path.Combine(directory, key + ".json");
        Entries = File.Exists(path) ? JsonSerializer.Deserialize<List<Mapping>>(File.ReadAllText(path)) ?? throw new InvalidDataException() : [];
    }
    public void ValidateEpoch(uint validity)
    {
        var epochPath = path + ".epoch";
        if (File.Exists(epochPath))
        {
            if (File.ReadAllText(epochPath) != validity.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new IdentityException();
            return;
        }
        using var file = new FileStream(epochPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Encoding.ASCII.GetBytes(validity.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        file.Flush(true);
    }
    public void Reload() => Entries = File.Exists(path) ? JsonSerializer.Deserialize<List<Mapping>>(File.ReadAllText(path)) ?? throw new InvalidDataException() : [];
    public void Save(Mapping entry)
    {
        Reload();
        Entries.RemoveAll(m => m.SourceValidity == entry.SourceValidity && m.SourceUid == entry.SourceUid);
        Entries.Add(entry);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, Entries);
            file.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
