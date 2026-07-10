using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
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
    public int LobbyToken { get; init; }
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
    public int LobbyToken { get; init; }
}

public sealed record NetworkLobby
{
    public string ProtocolVersion { get; init; } = InviteCode.ProtocolVersion;
    public string ModeId { get; init; } = "dragon-duel";
    public NetworkPlayerHandshake Host { get; init; } = new();
    public NetworkPlayerHandshake Joiner { get; init; } = new();
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
    public const string CompactPrefix = "DC2-";
    public const string ProtocolVersion = "1";
    public const int LobbyCodeLength = 5;
    private const string CompactAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int CompactPayloadLength = 9;
    private const int CompactChecksumLength = 2;

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

    public static string EncodeCompact(NetworkInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Validate(invite);
        if (!IPAddress.TryParse(invite.Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Compact invites require an IPv4 host address.");
        }

        if (invite.LobbyToken is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("Compact invites require a lobby token between 1 and 65535.");
        }

        var payload = new byte[CompactPayloadLength];
        address.GetAddressBytes().CopyTo(payload, 0);
        payload[4] = (byte)(invite.Port >> 8);
        payload[5] = (byte)invite.Port;
        payload[6] = ModeCode(invite.ModeId);
        payload[7] = (byte)(invite.LobbyToken >> 8);
        payload[8] = (byte)invite.LobbyToken;

        var codeBytes = new byte[CompactPayloadLength + CompactChecksumLength];
        payload.CopyTo(codeBytes, 0);
        SHA256.HashData(payload).AsSpan(0, CompactChecksumLength).CopyTo(codeBytes.AsSpan(CompactPayloadLength));
        return CompactPrefix + FormatCompact(Base32Encode(codeBytes));
    }

    /// <summary>
    /// Creates the small, case-insensitive code displayed in a LAN lobby. The code carries
    /// only the lobby token; LAN discovery resolves the host address and port.
    /// </summary>
    public static string EncodeLobbyCode(int lobbyToken)
    {
        if (lobbyToken is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lobbyToken), "Lobby tokens must be between 1 and 65535.");
        }

        Span<char> characters = stackalloc char[LobbyCodeLength];
        characters[0] = CompactAlphabet[lobbyToken >> 15 & 31];
        characters[1] = CompactAlphabet[lobbyToken >> 10 & 31];
        characters[2] = CompactAlphabet[lobbyToken >> 5 & 31];
        characters[3] = CompactAlphabet[lobbyToken & 31];
        characters[4] = CompactAlphabet[LobbyChecksum(lobbyToken)];
        return new string(characters);
    }

    public static bool TryDecodeLobbyCode(string code, out int lobbyToken, out string error)
    {
        lobbyToken = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(code))
        {
            error = $"Enter the {LobbyCodeLength}-character lobby code.";
            return false;
        }

        var normalized = new string(code
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length != LobbyCodeLength)
        {
            error = $"Lobby codes are {LobbyCodeLength} characters.";
            return false;
        }

        var values = new int[LobbyCodeLength];
        for (var index = 0; index < normalized.Length; index++)
        {
            values[index] = CompactAlphabet.IndexOf(normalized[index]);
            if (values[index] < 0)
            {
                error = "Lobby codes use 2-9 and letters without I or O.";
                return false;
            }
        }

        var decodedToken = values[0] << 15 | values[1] << 10 | values[2] << 5 | values[3];
        if (decodedToken is < 1 or > ushort.MaxValue || values[4] != LobbyChecksum(decodedToken))
        {
            error = "Lobby code was not recognized. Check each character and try again.";
            return false;
        }

        lobbyToken = decodedToken;
        return true;
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
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            error = $"Invite codes must start with {CompactPrefix} or {Prefix}.";
            return false;
        }

        try
        {
            var normalized = inviteCode.Trim();
            if (normalized.StartsWith(CompactPrefix, StringComparison.OrdinalIgnoreCase))
            {
                invite = DecodeCompact(normalized);
                return true;
            }

            if (!normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Invite codes must start with {CompactPrefix} or {Prefix}.";
                return false;
            }

            var payload = new string(normalized[Prefix.Length..].Where(character => !char.IsWhiteSpace(character)).ToArray());
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

    private static NetworkInvite DecodeCompact(string inviteCode)
    {
        var compact = new string(inviteCode[CompactPrefix.Length..]
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .ToArray());
        var bytes = Base32Decode(compact);
        if (bytes.Length != CompactPayloadLength + CompactChecksumLength)
        {
            throw new FormatException("Compact invite code length is invalid.");
        }

        var payload = bytes.AsSpan(0, CompactPayloadLength);
        var expectedChecksum = SHA256.HashData(payload).AsSpan(0, CompactChecksumLength);
        if (!expectedChecksum.SequenceEqual(bytes.AsSpan(CompactPayloadLength)))
        {
            throw new FormatException("Compact invite code checksum did not match.");
        }

        var port = payload[4] << 8 | payload[5];
        var token = payload[7] << 8 | payload[8];
        var invite = new NetworkInvite
        {
            Host = new IPAddress(payload[..4]).ToString(),
            Port = port,
            ModeId = ModeId(payload[6]),
            ProtocolVersion = ProtocolVersion,
            LobbyToken = token
        };
        Validate(invite);
        return invite;
    }

    private static byte ModeCode(string modeId) => modeId.ToLowerInvariant() switch
    {
        DragonCardsModeIds.DragonDuel => 1,
        DragonCardsModeIds.StarterClash => 2,
        DragonCardsModeIds.DragonAvatar => 3,
        DragonCardsModeIds.SealedGauntlet => 4,
        DragonCardsModeIds.SandboxLab => 5,
        _ => throw new ArgumentException($"Mode '{modeId}' is not available for compact invites.")
    };

    private static string ModeId(byte code) => code switch
    {
        1 => DragonCardsModeIds.DragonDuel,
        2 => DragonCardsModeIds.StarterClash,
        3 => DragonCardsModeIds.DragonAvatar,
        4 => DragonCardsModeIds.SealedGauntlet,
        5 => DragonCardsModeIds.SandboxLab,
        _ => throw new FormatException("Compact invite mode is not supported.")
    };

    private static string FormatCompact(string code) => string.Join('-', Enumerable.Range(0, (code.Length + 5) / 6)
        .Select(index => code.Substring(index * 6, Math.Min(6, code.Length - index * 6))));

    private static int LobbyChecksum(int lobbyToken)
    {
        Span<byte> token = stackalloc byte[2];
        token[0] = (byte)(lobbyToken >> 8);
        token[1] = (byte)lobbyToken;
        return SHA256.HashData(token)[0] & 31;
    }

    private static string Base32Encode(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitCount = 0;
        foreach (var value in bytes)
        {
            buffer = buffer << 8 | value;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                builder.Append(CompactAlphabet[buffer >> bitCount & 31]);
                buffer = bitCount == 0 ? 0 : buffer & (1 << bitCount) - 1;
            }
        }

        if (bitCount > 0)
        {
            builder.Append(CompactAlphabet[buffer << 5 - bitCount & 31]);
        }

        return builder.ToString();
    }

    private static byte[] Base32Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Compact invite payload is empty.");
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bitCount = 0;
        foreach (var character in text.ToUpperInvariant())
        {
            var value = CompactAlphabet.IndexOf(character);
            if (value < 0)
            {
                throw new FormatException($"'{character}' is not valid in a compact invite code.");
            }

            buffer = buffer << 5 | value;
            bitCount += 5;
            while (bitCount >= 8)
            {
                bitCount -= 8;
                bytes.Add((byte)(buffer >> bitCount & 255));
                buffer = bitCount == 0 ? 0 : buffer & (1 << bitCount) - 1;
            }
        }

        return bytes.ToArray();
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
[JsonSerializable(typeof(NetworkLobby))]
[JsonSerializable(typeof(NetworkMatchStart))]
[JsonSerializable(typeof(NetworkWireMessage))]
[JsonSerializable(typeof(LanLobbyDiscoveryRequest))]
[JsonSerializable(typeof(LanLobbyDiscoveryResponse))]
[JsonSerializable(typeof(DeckDefinition))]
[JsonSerializable(typeof(GameRulesConfig))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class DragonCardsNetworkingJsonContext : JsonSerializerContext;
