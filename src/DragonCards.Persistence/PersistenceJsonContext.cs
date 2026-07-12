using System.Text.Json;
using System.Text.Json.Serialization;
using DragonCards.Core;

namespace DragonCards.Persistence;

/// <summary>
/// AOT-safe JSON metadata for every profile-persistence document. The desktop publishes with
/// trimming/AOT enabled, so persistence never relies on reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocalProfileIndex))]
[JsonSerializable(typeof(DeckDefinition))]
[JsonSerializable(typeof(GameRulesConfig))]
[JsonSerializable(typeof(ProfileDataExport))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(ProfileAuditPayload))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;

internal sealed record BackupFileManifest(string RelativePath, string Sha256, long Bytes);
internal sealed record BackupManifest(DateTimeOffset CreatedUtc, IReadOnlyList<BackupFileManifest> Files);

internal sealed record ProfileAuditPayload(
    int? Revision = null,
    string? DeckId = null,
    long? Seed = null,
    string? AlgorithmVersion = null,
    string? Scenario = null);
