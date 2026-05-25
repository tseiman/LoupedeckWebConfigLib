// Loads text files embedded into an assembly by suffix, such as HTML snippets.
using System.Reflection;
using System.Text;

namespace LoupedeckWebConfigLib;

public static class EmbeddedTextResource
{
    public static string Load<TAnchor>(string resourceNameSuffix)
    {
        return Load(typeof(TAnchor).Assembly, resourceNameSuffix);
    }

    public static string Load(Assembly assembly, string resourceNameSuffix)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceNameSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new FileNotFoundException($"Embedded resource ending with '{resourceNameSuffix}' was not found in '{assembly.FullName}'.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
