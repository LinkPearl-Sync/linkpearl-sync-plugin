using System.Security.Cryptography;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;

namespace Linkpearl.Integration;

/// <summary>
/// Passe les personnages de ce poste vers un fichier de sauvegarde, et retour.
/// </summary>
/// <remarks>
/// Le format et ses vérifications vivent dans le noyau
/// (<see cref="IdentityBackup"/>), où ils se testent sous Linux. Ici ne reste
/// que ce qui touche au disque et à DPAPI.
///
/// Rien de ce qui est remplacé n'est détruit : les fichiers d'avant sont
/// écartés sous un autre nom, dans le même dossier.
/// </remarks>
public static class IdentityBackupService
{
    /// <summary>Tous les personnages de ce poste dont l'identité se relit.</summary>
    public static (List<BackupEntry> Entries, int Unreadable) Collect(string charactersRoot)
    {
        var entries = new List<BackupEntry>();
        var unreadable = 0;

        if (Directory.Exists(charactersRoot) is false)
            return (entries, unreadable);

        foreach (var directory in Directory.EnumerateDirectories(charactersRoot).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(directory);

            if (IdentityBackup.IsFolderName(folder) is false)
                continue;

            if (File.Exists(Path.Combine(directory, "identity.key")) is false)
                continue;

            try
            {
                var identity = new DpapiIdentityStore(Path.Combine(directory, "identity.key")).Load();

                if (identity is null)
                {
                    unreadable++;
                    continue;
                }

                var pairs = new PairBookStore(Path.Combine(directory, "pairs.json")).ReadPlain() ?? "[]"u8.ToArray();
                var groups = new GroupStore(Path.Combine(directory, "groups.json")).ReadPlain();
                entries.Add(new BackupEntry(folder, identity, pairs, groups));
            }
            catch (Exception e) when (e is CryptographicException or IOException)
            {
                unreadable++;
            }
        }

        return (entries, unreadable);
    }

    /// <summary>Écrit le fichier, par un fichier temporaire pour ne jamais laisser une sauvegarde tronquée.</summary>
    public static void WriteFile(string destination, byte[] content)
    {
        var temporary = destination + ".part";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>
    /// Pose les personnages d'une sauvegarde déjà lue et validée par le noyau.
    /// </summary>
    /// <remarks>
    /// Tout est vérifié avant la première écriture : une clé qui ne s'importe
    /// pas ou un carnet qui ne se relit pas font refuser le fichier entier.
    /// Une restauration à moitié faite laisserait croire que tout est revenu.
    ///
    /// L'appelant doit avoir lâché le personnage courant : un carnet encore
    /// chargé réécrirait l'ancien par-dessus celui qu'on vient de poser.
    /// </remarks>
    public static (bool Restored, string Message) Restore(string charactersRoot, IReadOnlyList<BackupEntry> entries)
    {
        foreach (var entry in entries)
        {
            // Sur P-256 et nulle autre : une clé d'une autre courbe passerait
            // l'import, puis empêcherait le personnage de se charger.
            if (IdentityKeyPair.TryImport(entry.Identity, out var key, out _) is false)
                return (false, "sauvegarde impossible : identité invalide.");

            key.Dispose();

            if (PairBookStore.IsValid(entry.Pairs) is false)
                return (false, "sauvegarde impossible : carnet de pairs illisible.");

            if (entry.Groups is not null && GroupBookCodec.IsValid(entry.Groups) is false)
                return (false, "sauvegarde impossible : liste de groupes illisible.");
        }

        foreach (var entry in entries)
        {
            // Le nom a été validé par le noyau : seize chiffres hexadécimaux,
            // qui ne peuvent pas sortir du dossier des personnages.
            var directory = Path.Combine(charactersRoot, entry.Folder);
            Directory.CreateDirectory(directory);

            // Les invitations en cours appartiennent à l'identité remplacée :
            // elles partent avec elle.
            foreach (var name in CharacterStorage.Belongings)
                CharacterStorage.SetAside(Path.Combine(directory, name), "avant-restauration");

            new DpapiIdentityStore(Path.Combine(directory, "identity.key")).Save(entry.Identity);
            new PairBookStore(Path.Combine(directory, "pairs.json")).WritePlain(entry.Pairs);

            if (entry.Groups is { } groups)
                new GroupStore(Path.Combine(directory, "groups.json")).WritePlain(groups);
        }

        return (true, entries.Count == 1
            ? "1 personnage restauré."
            : $"{entries.Count} personnages restaurés.");
    }
}
