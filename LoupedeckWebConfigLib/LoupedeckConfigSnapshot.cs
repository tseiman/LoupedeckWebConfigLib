// Represents the complete configuration state returned as JSON by the service.
using System.Text.Json.Nodes;

namespace LoupedeckWebConfigLib;

public sealed record LoupedeckConfigSnapshot(
    LoupedeckPluginRegistration? Plugin,
    IReadOnlyList<LoupedeckActionRegistration> Actions,
    IReadOnlyDictionary<Guid, JsonNode?> ActionConfigurations);
