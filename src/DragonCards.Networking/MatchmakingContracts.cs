namespace DragonCards.Networking;

/// <summary>
/// Provider-neutral boundary for a future authenticated Internet matchmaking service.
/// The desktop client deliberately ships with an unavailable implementation; LAN direct play
/// remains independent and fully usable.
/// </summary>
public interface IMatchmakingClient
{
    Task<MatchmakingQueueResult> QueueAsync(MatchmakingQueueRequest request, CancellationToken cancellationToken = default);
    Task<MatchmakingStatus> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default);
    Task<MatchAssignment?> ReconnectAsync(MatchReconnectRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(string ticketId, CancellationToken cancellationToken = default);
}

public sealed record MatchmakingQueueRequest(
    string PlayerId,
    string ModeId,
    string DeckId,
    string DeckCode,
    string Region,
    string ClientVersion);

public sealed record MatchmakingQueueResult(
    bool Accepted,
    string TicketId,
    string Message,
    MatchmakingStatus? Status = null);

public sealed record MatchmakingStatus(
    string TicketId,
    MatchmakingQueueState State,
    string Message,
    int EstimatedWaitSeconds = 0,
    MatchAssignment? Assignment = null);

public enum MatchmakingQueueState
{
    Unavailable,
    Queued,
    Assigned,
    Cancelled,
    Failed
}

public sealed record MatchAssignment(
    string MatchId,
    Uri WebSocketEndpoint,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string OpponentDisplayName = "");

public sealed record MatchReconnectRequest(string PlayerId, string MatchId, string ResumeToken, string ClientVersion);

public sealed class UnavailableMatchmakingClient : IMatchmakingClient
{
    public const string Message = "Internet queueing is not configured in this build. Use LAN / direct play.";

    public Task<MatchmakingQueueResult> QueueAsync(MatchmakingQueueRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MatchmakingQueueResult(false, "", Message, new MatchmakingStatus("", MatchmakingQueueState.Unavailable, Message)));

    public Task<MatchmakingStatus> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MatchmakingStatus(ticketId, MatchmakingQueueState.Unavailable, Message));

    public Task<MatchAssignment?> ReconnectAsync(MatchReconnectRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<MatchAssignment?>(null);

    public Task CancelAsync(string ticketId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
