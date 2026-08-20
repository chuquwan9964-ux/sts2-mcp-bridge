using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2McpBridge.Core;

public sealed record LegalAction(
    [property: JsonPropertyName("action_id")] string ActionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label);

public sealed record BridgeState(
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("screen")] string Screen,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("observation")] JsonElement Observation,
    [property: JsonPropertyName("legal_actions")] IReadOnlyList<LegalAction> LegalActions,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record RegisterRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("state")] BridgeState State);

public sealed record ActionRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("action_id")] string ActionId);

public sealed record ActionResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("message")] string Message);

public sealed record PendingAction(
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("action_id")] string ActionId);

public sealed record HistoryEntry(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("action_id")] string ActionId,
    [property: JsonPropertyName("status")] string Status);

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
