using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DragonCards.Networking;

public sealed record LanLobbyDiscoveryRequest
{
    public string ProtocolVersion { get; init; } = InviteCode.ProtocolVersion;
    public int LobbyToken { get; init; }
}

public sealed record LanLobbyDiscoveryResponse
{
    public NetworkInvite Invite { get; init; } = new();
}

/// <summary>
/// Resolves a compact lobby token to a direct TCP endpoint on the local network.
/// It deliberately does not provide Internet discovery, matchmaking, relays, or NAT traversal.
/// </summary>
public static class LanLobbyDiscovery
{
    public const int Port = 47287;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = DragonCardsNetworkingJsonContext.Default
    };

    public static async Task<NetworkInvite> ResolveAsync(
        int lobbyToken,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (lobbyToken is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lobbyToken));
        }

        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
        var token = timeoutCancellation.Token;
        var request = JsonSerializer.SerializeToUtf8Bytes(new LanLobbyDiscoveryRequest { LobbyToken = lobbyToken }, JsonOptions);

        try
        {
            var targets = new[] { IPAddress.Broadcast }
                .Concat(LocalNetworkAddress.BroadcastAddresses())
                .Append(IPAddress.Loopback)
                .Distinct()
                .Select(address => new IPEndPoint(address, Port));
            foreach (var target in targets)
            {
                try
                {
                    await client.SendAsync(request, target, token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Keep trying the other adapter broadcasts and loopback fallback.
                }
            }

            while (true)
            {
                var result = await client.ReceiveAsync(token).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<LanLobbyDiscoveryResponse>(result.Buffer, JsonOptions);
                if (response is null || response.Invite.LobbyToken != lobbyToken)
                {
                    continue;
                }

                // The UDP response address is the host interface that can route back to this guest.
                // It is more reliable than a host's preferred adapter when Wi-Fi, Ethernet, or VPNs coexist.
                var resolvedInvite = response.Invite with { Host = result.RemoteEndPoint.Address.ToString() };
                ValidateResponse(resolvedInvite, lobbyToken);
                return resolvedInvite;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("No LAN lobby answered that code. Confirm both players are on the same network and the host lobby is open.");
        }
    }

    private static void ValidateResponse(NetworkInvite invite, int lobbyToken)
    {
        if (invite.LobbyToken != lobbyToken || string.IsNullOrWhiteSpace(invite.Host) || invite.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The LAN lobby response was invalid.");
        }

        if (!invite.ProtocolVersion.Equals(InviteCode.ProtocolVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The LAN lobby uses an incompatible protocol version.");
        }
    }
}

public sealed class LanLobbyDiscoveryHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = DragonCardsNetworkingJsonContext.Default
    };

    private readonly NetworkInvite _invite;
    private readonly UdpClient _client;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _runTask;
    private bool _disposed;

    private LanLobbyDiscoveryHost(NetworkInvite invite, UdpClient client)
    {
        _invite = invite;
        _client = client;
        _runTask = RunAsync();
    }

    public static LanLobbyDiscoveryHost Start(NetworkInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        if (invite.LobbyToken is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("LAN discovery requires a lobby token between 1 and 65535.", nameof(invite));
        }

        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, LanLobbyDiscovery.Port));
        return new LanLobbyDiscoveryHost(invite, client);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _client.Dispose();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                var result = await _client.ReceiveAsync(_cancellation.Token).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<LanLobbyDiscoveryRequest>(result.Buffer, JsonOptions);
                if (request is null || request.LobbyToken != _invite.LobbyToken ||
                    !request.ProtocolVersion.Equals(InviteCode.ProtocolVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var response = JsonSerializer.SerializeToUtf8Bytes(new LanLobbyDiscoveryResponse { Invite = _invite }, JsonOptions);
                await _client.SendAsync(response, result.RemoteEndPoint, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (JsonException)
            {
                // Ignore unrelated UDP traffic on this port and keep the lobby discoverable.
            }
            catch (SocketException) when (!_cancellation.IsCancellationRequested)
            {
                // A transient send/receive error should not make an otherwise open lobby disappear.
            }
        }
    }
}
