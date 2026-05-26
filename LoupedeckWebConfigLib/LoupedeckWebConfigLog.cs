// Provides helpers for routing web configuration service logs to host logging APIs.
namespace LoupedeckWebConfigLib;

public static class LoupedeckWebConfigLog
{
    public static Action<LoupedeckWebConfigLogEntry> FromDelegates(
        Action<string>? verbose = null,
        Action<string>? info = null,
        Action<string>? warning = null,
        Action<string>? error = null,
        Action<Exception, string>? verboseException = null,
        Action<Exception, string>? infoException = null,
        Action<Exception, string>? warningException = null,
        Action<Exception, string>? errorException = null)
    {
        return entry =>
        {
            if (entry.Exception is not null)
            {
                var exceptionWriter = entry.Level switch
                {
                    LoupedeckWebConfigLogLevel.Verbose => verboseException,
                    LoupedeckWebConfigLogLevel.Info => infoException,
                    LoupedeckWebConfigLogLevel.Warning => warningException,
                    LoupedeckWebConfigLogLevel.Error => errorException,
                    _ => null
                };

                if (exceptionWriter is not null)
                {
                    exceptionWriter(entry.Exception, entry.Message);
                    return;
                }
            }

            var writer = entry.Level switch
            {
                LoupedeckWebConfigLogLevel.Verbose => verbose,
                LoupedeckWebConfigLogLevel.Info => info,
                LoupedeckWebConfigLogLevel.Warning => warning,
                LoupedeckWebConfigLogLevel.Error => error,
                _ => null
            };

            writer?.Invoke(entry.Exception is null ? entry.Message : $"{entry.Message} {entry.Exception}");
        };
    }

    public static void WriteToConsole(LoupedeckWebConfigLogEntry entry)
    {
        var text = $"[LoupedeckWebConfigLib] {entry.Level}: {entry.Message}";
        if (entry.Exception is not null)
        {
            text = $"{text} {entry.Exception}";
        }

        if (entry.Level >= LoupedeckWebConfigLogLevel.Warning)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.WriteLine(text);
        }
    }
}
