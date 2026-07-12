using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DragonCards.Core;

public sealed record DeckCodePayload(string Name, string ModeId, IReadOnlyList<DeckCodeCardCount> Cards);
public sealed record DeckCodeCardCount(string CardId, int Count);

public static class DeckCode
{
    public const string Prefix = "DCD1-";
    private const int ChecksumBytes = 8;

    public static string Export(DeckDefinition deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        var payload = new DeckCodePayload(
            deck.Name.Trim(),
            deck.ModeId.Trim(),
            deck.Cards
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0 && !EnergySource.IsEnergySourceCardId(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new DeckCodeCardCount(entry.Key, entry.Value))
                .ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Prefix + Encode(bytes) + "." + Encode(SHA256.HashData(bytes).AsSpan(0, ChecksumBytes));
    }

    public static bool TryImport(string? code, out DeckDefinition? deck, out string error)
    {
        deck = null;
        error = "";
        if (string.IsNullOrWhiteSpace(code) || !code.Trim().StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "Deck code must start with DCD1-.";
            return false;
        }

        var sections = code.Trim()[Prefix.Length..].Split('.', StringSplitOptions.None);
        if (sections.Length != 2 || !TryDecode(sections[0], out var payloadBytes) || !TryDecode(sections[1], out var signature))
        {
            error = "Deck code syntax is invalid.";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, SHA256.HashData(payloadBytes).AsSpan(0, ChecksumBytes)))
        {
            error = "Deck code checksum does not match.";
            return false;
        }

        DeckCodePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeckCodePayload>(payloadBytes);
        }
        catch (JsonException)
        {
            error = "Deck code payload is invalid.";
            return false;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name) || payload.Name.Length > 64 ||
            string.IsNullOrWhiteSpace(payload.ModeId) || payload.Cards is null || payload.Cards.Count == 0 ||
            payload.Cards.Any(card => string.IsNullOrWhiteSpace(card.CardId) || card.Count <= 0) ||
            payload.Cards.Select(card => card.CardId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Cards.Count)
        {
            error = "Deck code contains invalid deck data.";
            return false;
        }

        deck = new DeckDefinition
        {
            Id = "imported-" + Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant()[..12],
            Name = payload.Name.Trim(),
            ModeId = payload.ModeId.Trim(),
            Cards = payload.Cards.OrderBy(card => card.CardId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(card => card.CardId, card => card.Count, StringComparer.OrdinalIgnoreCase)
        };
        return true;
    }

    private static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
