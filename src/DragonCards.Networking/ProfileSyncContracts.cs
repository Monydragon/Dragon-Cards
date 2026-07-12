namespace DragonCards.Networking;

/// <summary>
/// Provider-neutral profile synchronization boundary for a future authenticated service.
/// LAN matches never use this contract and never receive another player's local profile database.
/// </summary>
public interface IProfileSyncClient
{
    Task<ProfileSyncPushResult> PushAsync(ProfileSyncPushRequest request, CancellationToken cancellationToken = default);
    Task<ProfileSyncPullResult> PullAsync(ProfileSyncPullRequest request, CancellationToken cancellationToken = default);
}

public sealed record ProfileSyncMutation(
    string MutationId,
    string ProfileId,
    string DeviceId,
    int BaseRevision,
    int Revision,
    ProfileSyncMutationKind Kind,
    string PayloadJson,
    DateTimeOffset OccurredUtc,
    string ClientVersion);

public enum ProfileSyncMutationKind
{
    ProfileSaved,
    DeckSaved,
    DeckDeleted,
    SeedApplied,
    ProfileDeleted
}

public sealed record ProfileSyncCursor(string ProfileId, int LastAcknowledgedRevision);

public sealed record ProfileSyncPushRequest(
    string AccountId,
    string DeviceId,
    IReadOnlyList<ProfileSyncMutation> Mutations,
    string ClientVersion);

public sealed record ProfileSyncPullRequest(
    string AccountId,
    string DeviceId,
    ProfileSyncCursor Cursor,
    string ClientVersion);

public enum ProfileSyncState
{
    Unavailable,
    Accepted,
    Conflict,
    Rejected,
    AuthenticationRequired
}

public sealed record ProfileSyncPushResult(
    ProfileSyncState State,
    string Message,
    ProfileSyncCursor? Cursor = null,
    IReadOnlyList<string>? RejectedMutationIds = null);

public sealed record ProfileSyncPullResult(
    ProfileSyncState State,
    string Message,
    ProfileSyncCursor Cursor,
    IReadOnlyList<ProfileSyncMutation> Mutations);

/// <summary>Shared client/server guardrails. The future service remains authoritative for identity and conflict resolution.</summary>
public static class ProfileSyncContract
{
    public const int MaximumPayloadBytes = 256 * 1024;

    public static bool TryValidate(ProfileSyncMutation mutation, out string? error)
    {
        if (string.IsNullOrWhiteSpace(mutation.MutationId) || string.IsNullOrWhiteSpace(mutation.ProfileId) || string.IsNullOrWhiteSpace(mutation.DeviceId))
        {
            error = "Profile sync mutations require mutation, profile, and device identifiers.";
            return false;
        }

        if (mutation.BaseRevision < 0 || mutation.Revision < 1 || mutation.Revision <= mutation.BaseRevision)
        {
            error = "Profile sync revisions must advance beyond a non-negative base revision.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mutation.PayloadJson))
        {
            error = "Profile sync mutations require a JSON payload.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(mutation.PayloadJson) > MaximumPayloadBytes)
        {
            error = $"Profile sync payloads cannot exceed {MaximumPayloadBytes} UTF-8 bytes.";
            return false;
        }

        if (mutation.OccurredUtc.Offset != TimeSpan.Zero)
        {
            error = "Profile sync timestamps must be UTC.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mutation.ClientVersion))
        {
            error = "Profile sync mutations require a client version.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(ProfileSyncPushRequest request, out string? error)
    {
        if (string.IsNullOrWhiteSpace(request.AccountId) || string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.ClientVersion))
        {
            error = "Profile sync requests require account, device, and client-version data.";
            return false;
        }

        if (request.Mutations.Count == 0)
        {
            error = "Profile sync requests must contain at least one mutation.";
            return false;
        }

        foreach (var mutation in request.Mutations)
        {
            if (!mutation.DeviceId.Equals(request.DeviceId, StringComparison.Ordinal))
            {
                error = "Every profile sync mutation must belong to the requesting device.";
                return false;
            }

            if (!TryValidate(mutation, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }
}

/// <summary>Offline default that makes the local-only boundary explicit until a service is configured.</summary>
public sealed class UnavailableProfileSyncClient : IProfileSyncClient
{
    public const string Message = "Profile sync is not configured in this build. Your profile remains local and under your control.";

    public Task<ProfileSyncPushResult> PushAsync(ProfileSyncPushRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProfileSyncPushResult(ProfileSyncState.Unavailable, Message));

    public Task<ProfileSyncPullResult> PullAsync(ProfileSyncPullRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProfileSyncPullResult(ProfileSyncState.Unavailable, Message, request.Cursor, []));
}
