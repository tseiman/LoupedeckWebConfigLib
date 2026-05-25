// Abstracts persistent storage so plugins can use Logi/Loupedeck settings or files.
namespace LoupedeckWebConfigLib;

public interface ILoupedeckConfigStore
{
    string? Load();

    void Save(string json);
}
