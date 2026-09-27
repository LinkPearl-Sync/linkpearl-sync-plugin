using Linkpearl.Core.Identity;

namespace Linkpearl.Integration;

/// <summary>
/// Où vivent les affaires d'un personnage, et comment celles d'avant y entrent.
/// </summary>
/// <remarks>
/// Sous le dossier de configuration que Dalamud donne au plugin, et non sous
/// un chemin choisi par nous : c'est là que l'utilisateur, une sauvegarde ou un
/// désinstalleur iront chercher ce qui appartient à ce plugin.
///
/// Le cache de blobs fait exception et reste ailleurs : il pèse des gigaoctets
/// et se régénère, ce qui n'a rien à faire dans un profil itinérant.
/// </remarks>
public static class CharacterStorage
{
    /// <summary>Ce qui appartient à un personnage, et à lui seul.</summary>
    public static readonly string[] Belongings = ["identity.key", "pairs.json", "invitations.json", "groups.json"];

    /// <summary>
    /// Écarte un fichier sans le détruire, et rend le nom sous lequel il reste.
    /// </summary>
    /// <remarks>
    /// Une identité illisible aujourd'hui peut redevenir lisible demain : un
    /// profil Windows restauré, une sauvegarde retrouvée. L'écraser par une
    /// neuve serait perdre tous les pairages sans retour, et sans que personne
    /// ne l'ait décidé.
    /// </remarks>
    public static string? SetAside(string path, string reason)
    {
        if (File.Exists(path) is false)
            return null;

        var aside = $"{path}.{reason}-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, aside, overwrite: true);
        return aside;
    }

    public static string Prepare(string baseRoot, ulong contentId, string legacyRoot, Action<string> log)
    {
        var root = Path.Combine(baseRoot, "characters", CharacterFolder.Name(contentId));
        Directory.CreateDirectory(root);

        Adopt(legacyRoot, root, log);

        return root;
    }

    /// <summary>
    /// Fait entrer l'identité d'avant la séparation par personnage.
    /// </summary>
    /// <remarks>
    /// Une seule fois, au profit du premier personnage qui se connecte : sans
    /// cela, les appairages déjà établis seraient perdus en silence, et l'autre
    /// côté continuerait de nous attendre sous une clé que nous n'avons plus.
    ///
    /// Les fichiers sont déplacés et non copiés : deux exemplaires de la même
    /// identité feraient deux pairs qui se croient seuls à la porter.
    /// </remarks>
    private static void Adopt(string legacyRoot, string root, Action<string> log)
    {
        if (File.Exists(Path.Combine(root, "identity.key")))
            return;

        if (File.Exists(Path.Combine(legacyRoot, "identity.key")) is false)
            return;

        var moved = 0;

        foreach (var name in Belongings)
        {
            var from = Path.Combine(legacyRoot, name);
            var to = Path.Combine(root, name);

            if (File.Exists(from) is false || File.Exists(to))
                continue;

            try
            {
                File.Move(from, to);
                moved++;
            }
            catch (IOException e)
            {
                // Un fichier verrouillé n'empêche pas les autres de suivre :
                // l'identité est le seul qui compte vraiment.
                log($"récupération de {name} impossible : {e.Message}");
            }
        }

        if (moved > 0)
            log($"identité et carnet récupérés depuis l'ancien emplacement ({moved} fichier(s)).");
    }
}
