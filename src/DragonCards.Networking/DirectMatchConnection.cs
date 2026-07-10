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

    private DirectMatchConnection(TcpClient client, bool isHost, int localPlayerIndex, NetworkLobby lobby)
    {
        _client = client;
        IsHost = isHost;
        LocalPlayerIndex = localPlayerIndex;
        Lobby = lobby;
        MatchStart = new NetworkMatchStart();
        var stream = client.GetStream();
        _reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public bool IsHost { get; }
    public int LocalPlayerIndex { get; }
    public int RemotePlayerIndex => 1 - LocalPlayerIndex;
    public NetworkLobby Lobby { get; private set; }
    public NetworkMatchStart MatchStart { get; private set; }

    public static async Task<DirectMatchConnection> HostAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake hostHandshake,
        int seed,
        CancellationToken cancellationToken = default)
    {
        var connection = await HostLobbyAsync(invite, hostHandshake, cancellationToken).ConfigureAwait(false);
        await connection.StartMatchAsync(seed, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static async Task<DirectMatchConnection> JoinAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake joinerHandshake,
        CancellationToken cancellationToken = default)
    {
        var connection = await JoinLobbyAsync(invite, joinerHandshake, cancellationToken).ConfigureAwait(false);
        await connection.WaitForMatchStartAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static async Task<DirectMatchConnection> HostLobbyAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake hostHandshake,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(hostHandshake);

        var host = NormalizeHandshake(hostHandshake);
        LanLobbyDiscoveryHost? discovery = null;
        try
        {
            if (invite.LobbyToken > 0)
            {
                discovery = LanLobbyDiscoveryHost.Start(invite);
            }

            using var listener = new TcpListener(IPAddress.Any, invite.Port);
            listener.Start(1);
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var connection = new DirectMatchConnection(client, isHost: true, localPlayerIndex: 0, new NetworkLobby());
            try
            {
                var joiner = NormalizeHandshake(await connection.ReadPayloadAsync<NetworkPlayerHandshake>("hello", cancellationToken).ConfigureAwait(false));
                ValidateHandshake(joiner, host.ModeId, host.ProtocolVersion, host.RulesHash, invite.LobbyToken);

                connection.Lobby = new NetworkLobby
                {
                    ProtocolVersion = host.ProtocolVersion,
                    ModeId = host.ModeId,
                    Host = host,
                    Joiner = joiner
                };
                await connection.SendPayloadAsync("lobby", connection.Lobby, cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (discovery is not null)
            {
                await discovery.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task<DirectMatchConnection> JoinLobbyAsync(
        NetworkInvite invite,
        NetworkPlayerHandshake joinerHandshake,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ArgumentNullException.ThrowIfNull(joinerHandshake);

        var joiner = NormalizeHandshake(joinerHandshake);
        var client = new TcpClient();
        await client.ConnectAsync(invite.Host, invite.Port, cancellationToken).ConfigureAwait(false);
        var connection = new DirectMatchConnection(client, isHost: false, localPlayerIndex: 1, new NetworkLobby());
        try
        {
            await connection.SendPayloadAsync("hello", joiner, cancellationToken).ConfigureAwait(false);
            var lobby = await connection.ReadPayloadAsync<NetworkLobby>("lobby", cancellationToken).ConfigureAwait(false);
            ValidateLobby(lobby, invite, joiner);
            connection.Lobby = lobby;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static NetworkPlayerHandshake CreateHandshake(string playerName, string modeId, DeckDefinition deck, GameRulesConfig rules, int lobbyToken = 0)
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
            RulesHash = InviteCode.RulesHash(rules),
            LobbyToken = Math.Max(0, lobbyToken)
        };
    }

    public async Task<NetworkMatchStart> StartMatchAsync(int seed, CancellationToken cancellationToken = default)
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("Only the hosting player can start a direct-match lobby.");
        }

        var matchStart = new NetworkMatchStart
        {
            ProtocolVersion = Lobby.ProtocolVersion,
            ModeId = Lobby.ModeId,
            Seed = seed,
            Host = Lobby.Host,
            Joiner = Lobby.Joiner
        };
        await SendPayloadAsync("start", matchStart, cancellationToken).ConfigureAwait(false);
        MatchStart = matchStart;
        return matchStart;
    }

    public async Task<NetworkMatchStart> WaitForMatchStartAsync(CancellationToken cancellationToken = default)
    {
        if (IsHost)
        {
            throw new InvalidOperationException("The host must start the lobby instead of waiting for a start message.");
        }

        var matchStart = await ReadPayloadAsync<NetworkMatchStart>("start", cancellationToken).ConfigureAwait(false);
        ValidateMatchStart(matchStart, Lobby);
        MatchStart = matchStart;
        return matchStart;
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
            RulesHash = string.IsNullOrWhiteSpace(handshake.RulesHash) ? InviteCode.RulesHash(rules) : handshake.RulesHash,
            LobbyToken = Math.Max(0, handshake.LobbyToken)
        };
    }

    private static void ValidateHandshake(NetworkPlayerHandshake handshake, string modeId, string protocolVersion, string rulesHash, int expectedLobbyToken = 0)
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

        if (expectedLobbyToken > 0 && handshake.LobbyToken != expectedLobbyToken)
        {
            throw new InvalidOperationException("The invite code does not match this hosted lobby.");
        }
    }

    private static void ValidateLobby(NetworkLobby lobby, NetworkInvite invite, NetworkPlayerHandshake localJoiner)
    {
        if (!lobby.ProtocolVersion.Equals(invite.ProtocolVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported protocol version '{lobby.ProtocolVersion}'.");
        }

        ValidateHandshake(lobby.Host, invite.ModeId, invite.ProtocolVersion, localJoiner.RulesHash, invite.LobbyToken);
        ValidateHandshake(lobby.Joiner, invite.ModeId, invite.ProtocolVersion, localJoiner.RulesHash, invite.LobbyToken);
    }

    private static void ValidateMatchStart(NetworkMatchStart matchStart, NetworkLobby lobby)
    {
        if (!matchStart.ProtocolVersion.Equals(lobby.ProtocolVersion, StringComparison.OrdinalIgnoreCase) ||
            !matchStart.ModeId.Equals(lobby.ModeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The host started a match with incompatible lobby settings.");
        }

        ValidateHandshake(matchStart.Host, lobby.ModeId, lobby.ProtocolVersion, lobby.Host.RulesHash, lobby.Host.LobbyToken);
        ValidateHandshake(matchStart.Joiner, lobby.ModeId, lobby.ProtocolVersion, lobby.Joiner.RulesHash, lobby.Joiner.LobbyToken);
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
