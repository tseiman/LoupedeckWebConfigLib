// Defines a single configurable parameter exposed by a Loupedeck action.
namespace LoupedeckWebConfigLib;

public sealed record ConfigParameterOption(string Value, string Label);

public sealed record ConfigParameterDefinition(
    string Name,
    ConfigParameterType Type,
    string? Label = null,
    string? DefaultValue = null,
    IReadOnlyList<ConfigParameterOption>? Options = null,
    bool Required = false);
