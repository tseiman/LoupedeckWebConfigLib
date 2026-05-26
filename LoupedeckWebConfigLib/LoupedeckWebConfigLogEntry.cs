// Represents one log event emitted by the web configuration service.
namespace LoupedeckWebConfigLib;

public sealed record LoupedeckWebConfigLogEntry(
    LoupedeckWebConfigLogLevel Level,
    string Message,
    Exception? Exception = null);
