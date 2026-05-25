// Defines the contract an action must implement to participate in web configuration updates.
using System.Text.Json.Nodes;

namespace LoupedeckWebConfigLib;

public interface ILoupedeckConfigAction
{
    LoupedeckActionRegistration Registration { get; }

    JsonNode? GetConfiguration();

    void OnConfigurationUpdated(JsonNode? configuration);
}
