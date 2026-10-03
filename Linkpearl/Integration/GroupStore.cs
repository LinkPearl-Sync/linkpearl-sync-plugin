using System.Security.Cryptography;
using System.Text.Json;
using Linkpearl.Core.Groups;

namespace Linkpearl.Integration;

/// <summary>
/// Conserve les groupes du personnage, protégés par DPAPI.
/// </summary>
/// <remarks>
/// Protégés parce qu'ils contiennent les secrets de groupe : qui les lirait
/// verrait quels membres sont en ligne, et pourrait se présenter à eux.
/// </remarks>
public sealed class GroupStore(string path)
{
    private static readonly byte[] Entropy = "linkpearl:groups:v1"u8.ToArray();

    /// <summary>Un épinglage peut venir d'un handshake pendant que l'interface enregistre.</summary>
    private readonly Lock _writing = new();

    public void Load(GroupBook book)
    {
        try
        {
            if (ReadPlain() is { } plain)
                book.Load(GroupBookCodec.Decode(plain));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Même règle que le carnet : on démarre sans groupes plutôt que
            // d'empêcher le plugin de se charger, et le fichier est écarté,
            // sans quoi le premier enregistrement l'écraserait.
            CharacterStorage.SetAside(path, "illisible");
        }
    }

    public byte[]? ReadPlain()
        => File.Exists(path)
            ? ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)
            : null;

    public void WritePlain(byte[] plain)
    {
        lock (_writing)
            WriteUnderLock(plain);
    }

    /// <remarks>
    /// L'instantané se prend sous le même verrou que l'écriture. Pris avant,
    /// deux épinglages presque simultanés pourraient écrire le plus ancien en
    /// dernier, et le plus récent serait perdu jusqu'au prochain changement.
    /// Sans risque d'interblocage : le carnet lève <c>Changed</c> hors de son
    /// propre verrou, donc personne ne tient le sien en attendant celui-ci.
    /// </remarks>
    public void Save(GroupBook book)
    {
        lock (_writing)
            WriteUnderLock(GroupBookCodec.Encode(book.Stored));
    }

    private void WriteUnderLock(byte[] plain)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".part";
        File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temporary, path, overwrite: true);
    }
}
