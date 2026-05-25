// Defines the JSON document written to persistent plugin storage.
using System.Text.Json.Nodes;

namespace LoupedeckWebConfigLib;

internal sealed record PersistedLoupedeckConfig(
    int Version,
    IReadOnlyDictionary<string, JsonNode?> ActionConfigurations,
    JsonNode? PluginConfiguration = null);
