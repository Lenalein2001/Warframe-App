using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarframeCompanion.Core;

public sealed record Equipment(long Count, long Xp);
public sealed record FoundryJob(string Recipe, double? Done);
public sealed record Currencies(long? Platinum, long? Credits, long? Endo, long? Ducats);
public sealed record Affiliation(long Standing, long Title);
public sealed record Progress(Dictionary<string, long> Nodes, Dictionary<string, long> Intrinsics);
public sealed record Standing(Dictionary<string, Affiliation> Syndicates, Dictionary<string, long> Daily, long? DailyFocus);

// Collections are owned by the snapshot. Persistence serializes on entry and never caches
// mutable caller objects; a caller cannot change committed state by changing a dictionary.
public sealed record InventorySnapshot(
    int SchemaVersion, string AccountId, bool IdentityVerified, string Provider,
    DateTimeOffset ReadAt, double? SourceTimestamp, bool Complete, string[] Warnings,
    Dictionary<string, long> Counts, Dictionary<string, Equipment> Equipment,
    Dictionary<string, long> Xp, Dictionary<string, List<long>>? Upgrades,
    FoundryJob[] Foundry, Currencies Currencies, long Mods, long? MasteryRank,
    Progress? Progress, Standing? Standing);

public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class InventoryException(string message) : Exception(message);
