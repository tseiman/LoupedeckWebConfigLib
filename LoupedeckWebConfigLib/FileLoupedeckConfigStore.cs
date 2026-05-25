// Stores the persisted configuration JSON in a normal file for tests or non-SDK hosts.
using System.Text;

namespace LoupedeckWebConfigLib;

public sealed class FileLoupedeckConfigStore : ILoupedeckConfigStore
{
    private readonly string _filePath;

    public FileLoupedeckConfigStore(string filePath)
    {
        _filePath = filePath;
    }

    public string? Load()
    {
        return File.Exists(_filePath) ? File.ReadAllText(_filePath, Encoding.UTF8) : null;
    }

    public void Save(string json)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, json, Encoding.UTF8);
    }
}
