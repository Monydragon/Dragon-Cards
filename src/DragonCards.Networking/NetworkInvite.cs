using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DragonCards.Core;

namespace DragonCards.Networking;

public sealed record NetworkInvite
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 47288;
    public string ModeId { get; init; } = "dragon-duel";
    public string ProtocolVersion { get; init; } = InviteCode.ProtocolVersion;
    public string DeckHash { get; init; } = "";
    public string RulesHash { get; init; } = "";
}

public sealed record NetworkCommand
{
    public string Kind { get; init; } = "";
    public int PlayerIndex { get; init; }
    public int Sequence { get; init; }
    public string PayloadJson { get; init; } = "";
}

public sealed record NetworkPlayerHandshake
{
    public string ProtocolVersion { get; init; } = InviteCode.ProtocolVersion;
    public string ModeId { get; init; } = "dragon-duel";
    public string PlayerName { get; init; } = "Player";
    public DeckDefinition Deck { get; init; } = new();
    public GameRulesConfig Rules { get; init; } = GameRulesConfig.ForPreset(GameRulesPreset.Standard);
    public string DeckHash { get; init; } = "";
    public string RulesHash { get; init; } = "";
}

public sealed record NetworkMatchStart
{
    public string ProtocolVersion { get; init; } = InviteCode.ProtocolVersion;
    public string ModeId { get; init; } = "dragon-duel";
    public int Seed { get; init; }
    public NetworkPlayerHandshake Host { get; init; } = new();
    public NetworkPlayerHandshake Joiner { get; init; } = new();
}

public sealed record NetworkWireMessage
{
    public string Type { get; init; } = "";
    public string PayloadJson { get; init; } = "";
}

public static class InviteCode
{
    public const string Prefix = "DC1-";
    public const string ProtocolVersion = "1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = DragonCardsNetworkingJsonContext.Default
    };

    public static string Encode(NetworkInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Validate(invite);
        var json = JsonSerializer.SerializeToUtf8Bytes(invite, JsonOptions);
        return Prefix + Base64Url.EncodeToString(json);
    }

    public static NetworkInvite Decode(string inviteCode)
    {
        if (!TryDecode(inviteCode, out var invite, out var error))
        {
            throw new FormatException(error);
        }

        return invite;
    }

    public static bool TryDecode(string inviteCode, out NetworkInvite invite, out string error)
    {
        invite = new NetworkInvite();
        error = "";
        if (string.IsNullOrWhiteSpace(inviteCode) ||
            !inviteCode.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Invite codes must start with {Prefix}.";
            return false;
        }

        try
        {
            var payload = inviteCode[Prefix.Length..].Trim();
            var bytes = Base64Url.DecodeFromChars(payload);
            var decoded = JsonSerializer.Deserialize<NetworkInvite>(bytes, JsonOptions);
            if (decoded is null)
            {
                error = "Invite code did not contain an invite.";
                return false;
            }

            Validate(decoded);
            invite = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string DeckHash(IReadOnlyDictionary<string, int> cards)
    {
        var canonical = string.Join("|", cards
            .Where(item => item.Value > 0)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}:{item.Value}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string RulesHash(GameRulesConfig rules)
    {
        var normalized = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        var canonical = string.Join("|",
            normalized.Preset,
            normalized.Playstyle,
            normalized.ProgressionEnabled,
            normalized.AllUnlocks,
            normalized.UnlimitedDeckBuilder,
            normalized.StarterUnlockOverride,
            normalized.RewardMultiplier,
            normalized.AiDifficultyModifier,
            normalized.EnforceDeckOwnership,
            normalized.UsesDefaultDeckRules);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void Validate(NetworkInvite invite)
    {
        if (string.IsNullOrWhiteSpace(invite.Host))
        {
            throw new ArgumentException("Invite host is required.");
        }

        if (invite.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Invite port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(invite.ModeId))
        {
            throw new ArgumentException("Invite mode id is required.");
        }

        if (!invite.ProtocolVersion.Equals(ProtocolVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported protocol version '{invite.ProtocolVersion}'.");
        }
    }
}

[JsonSerializable(typeof(NetworkInvite))]
[JsonSerializable(typeof(NetworkCommand))]
[JsonSerializable(typeof(NetworkPlayerHandshake))]
[JsonSerializable(typeof(NetworkMatchStart))]
[JsonSerializable(typeof(NetworkWireMessage))]
[JsonSerializable(typeof(DeckDefinition))]
[JsonSerializable(typeof(GameRulesConfig))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class DragonCardsNetworkingJsonContext : JsonSerializerContext;
