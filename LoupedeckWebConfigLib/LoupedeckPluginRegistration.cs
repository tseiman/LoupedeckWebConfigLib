// Stores plugin-level metadata and optional plugin-wide configuration UI data.
namespace LoupedeckWebConfigLib;

public sealed record LoupedeckPluginRegistration(
    string PluginId,
    string Title,
    string? Heading = null,
    string? Description = null,
    IReadOnlyList<ConfigParameterDefinition>? Parameters = null,
    string? HtmlSnippet = null,
    string? ConfigurationKey = null);
