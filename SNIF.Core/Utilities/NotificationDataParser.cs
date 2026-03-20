using System.Text.Json;

namespace SNIF.Core.Utilities;

public static class NotificationDataParser
{
    public static bool MatchesMatchId(string? data, string matchId)
    {
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(matchId))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return document.RootElement.TryGetProperty("matchId", out var matchIdElement)
                && matchIdElement.ValueKind == JsonValueKind.String
                && string.Equals(matchIdElement.GetString(), matchId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}