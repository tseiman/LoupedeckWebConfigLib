// Describes one Loupedeck action and its optional custom configuration UI snippet.
namespace LoupedeckWebConfigLib;

public sealed record LoupedeckActionRegistration(
    Guid ActionGuid,
    string Name,
    IReadOnlyList<ConfigParameterDefinition> Parameters,
    string? HtmlSnippet = null,
    string? ConfigurationKey = null);
