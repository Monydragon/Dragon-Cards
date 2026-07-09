using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DragonCards.Core;

namespace DragonCards.Networking;

public sealed class DirectMatchConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = DragonCardsNetworkingJsonContext.Default
    };

    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private DirectMatchConnection(TcpClient client, bool isHost, int localPlayerIndex, NetworkMatchStart matchStart)
    {
        _client = client;
        IsHost = isHost;
        LocalPlayerIndex = localPlayerIndex;
        MatchStart = matchStart;
        var stream = client.GetStream();
        _reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public bool IsHost { get; }
    public int LocalPlayerIndex { get; }
    public int RemotePlayerIndex => 1 - LocalPlayerIndex;
    public NetworkMatchStart MatchStart { get; private set; }

    public static async Task<DirectMatchConnection> HostAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake hostHandshake,
        int seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(hostHandshake);

        using var listener = new TcpListener(IPAddress.Any, invite.Port);
        listener.Start(1);
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var connection = new DirectMatchConnection(client, isHost: true, localPlayerIndex: 0, new NetworkMatchStart());
        var joiner = await connection.ReadPayloadAsync<NetworkPlayerHandshake>("hello", cancellationToken).ConfigureAwait(false);
        ValidateHandshake(joiner, invite.ModeId, invite.ProtocolVersion, invite.RulesHash);

        connection.MatchStart = new NetworkMatchStart
        {
            ProtocolVersion = invite.ProtocolVersion,
            ModeId = invite.ModeId,
            Seed = seed,
            Host = NormalizeHandshake(hostHandshake),
            Joiner = NormalizeHandshake(joiner)
        };
        await connection.SendPayloadAsync("start", connection.MatchStart, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static async Task<DirectMatchConnection> JoinAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake joinerHandshake,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(joinerHandshake);

        var client = new TcpClient();
        await client.ConnectAsync(invite.Host, invite.Port, cancellationToken).ConfigureAwait(false);
        var connection = new DirectMatchConnection(client, isHost: false, localPlayerIndex: 1, new NetworkMatchStart());
        await connection.SendPayloadAsync("hello", NormalizeHandshake(joinerHandshake), cancellationToken).ConfigureAwait(false);
        connection.MatchStart = await connection.ReadPayloadAsync<NetworkMatchStart>("start", cancellationToken).ConfigureAwait(false);
        ValidateHandshake(connection.MatchStart.Host, invite.ModeId, invite.ProtocolVersion, invite.RulesHash);
        ValidateHandshake(connection.MatchStart.Joiner, invite.ModeId, invite.ProtocolVersion, invite.RulesHash);
        return connection;
    }

    public static NetworkPlayerHandshake CreateHandshake(string playerName, string modeId, DeckDefinition deck, GameRulesConfig rules)
    {
        rules = (rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        return new NetworkPlayerHandshake
        {
            ProtocolVersion = InviteCode.ProtocolVersion,
            ModeId = modeId,
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim(),
            Deck = deck,
            Rules = rules,
            DeckHash = InviteCode.DeckHash(deck.Cards),
            RulesHash = InviteCode.RulesHash(rules)
        };
    }

    public async Task SendCommandAsync(NetworkCommand command, CancellationToken cancellationToken = default) =>
        await SendPayloadAsync("command", command, cancellationToken).ConfigureAwait(false);

    public async Task<NetworkCommand> ReadCommandAsync(CancellationToken cancellationToken = default) =>
        await ReadPayloadAsync<NetworkCommand>("command", cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private static NetworkPlayerHandshake NormalizeHandshake(NetworkPlayerHandshake handshake)
    {
        var rules = (handshake.Rules ?? GameRulesConfig.ForPreset(GameRulesPreset.Standard)).Normalize();
        return handshake with
        {
            ProtocolVersion = string.IsNullOrWhiteSpace(handshake.ProtocolVersion) ? InviteCode.ProtocolVersion : handshake.ProtocolVersion,
            ModeId = string.IsNullOrWhiteSpace(handshake.ModeId) ? "dragon-duel" : handshake.ModeId,
            PlayerName = string.IsNullOrWhiteSpace(handshake.PlayerName) ? "Player" : handshake.PlayerName.Trim(),
            Rules = rules,
            DeckHash = string.IsNullOrWhiteSpace(handshake.DeckHash) ? InviteCode.DeckHash(handshake.Deck.Cards) : handshake.DeckHash,
            RulesHash = string.IsNullOrWhiteSpace(handshake.RulesHash) ? InviteCode.RulesHash(rules) : handshake.RulesHash
        };
    }

    private static void ValidateHandshake(NetworkPlayerHandshake handshake, string modeId, string protocolVersion, string rulesHash)
    {
        if (!handshake.ProtocolVersion.Equals(protocolVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported protocol version '{handshake.ProtocolVersion}'.");
        }

        if (!handshake.ModeId.Equals(modeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected mode '{modeId}', got '{handshake.ModeId}'.");
        }

        if (!string.IsNullOrWhiteSpace(rulesHash) &&
            !handshake.RulesHash.Equals(rulesHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The remote player is using incompatible rules.");
        }
    }

    private async Task SendPayloadAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        var message = new NetworkWireMessage
        {
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions)
        };
        var line = JsonSerializer.Serialize(message, JsonOptions);
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadPayloadAsync<T>(string expectedType, CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new IOException("The remote peer disconnected.");
        }

        var message = JsonSerializer.Deserialize<NetworkWireMessage>(line, JsonOptions)
            ?? throw new JsonException("Network message was empty.");
        if (!message.Type.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{expectedType}' message, got '{message.Type}'.");
        }

        return JsonSerializer.Deserialize<T>(message.PayloadJson, JsonOptions)
            ?? throw new JsonException($"Network payload '{expectedType}' was empty.");
    }
}
