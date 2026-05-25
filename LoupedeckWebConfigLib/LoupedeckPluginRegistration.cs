// Stores plugin-level metadata shown on the shared configuration page.
namespace LoupedeckWebConfigLib;

public sealed record LoupedeckPluginRegistration(
    string PluginId,
    string Title,
    string? Heading = null,
    string? Description = null);
