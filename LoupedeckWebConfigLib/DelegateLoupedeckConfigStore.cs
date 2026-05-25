// Provides a simple adapter from delegates to the persistent storage interface.
namespace LoupedeckWebConfigLib;

public sealed class DelegateLoupedeckConfigStore : ILoupedeckConfigStore
{
    private readonly Func<string?> _load;
    private readonly Action<string> _save;

    public DelegateLoupedeckConfigStore(Func<string?> load, Action<string> save)
    {
        _load = load;
        _save = save;
    }

    public string? Load()
    {
        return _load();
    }

    public void Save(string json)
    {
        _save(json);
    }
}
