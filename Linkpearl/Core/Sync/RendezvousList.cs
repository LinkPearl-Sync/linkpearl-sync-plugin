using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Un service de rendez-vous de la liste de l'utilisateur.</summary>
/// <remarks>
/// L'interrupteur permet d'en écarter un sans perdre son adresse : un service
/// en panne se désactive le temps qu'il revienne, plutôt que de se retaper.
/// </remarks>
public sealed record RendezvousEntry(RendezvousAddress Address, string Label, bool Enabled);

/// <summary>
/// La liste des services de l'utilisateur, et sa migration.
/// </summary>
/// <remarks>
/// La migration vit ici et non dans <c>Configuration</c>, qui dépend de Dalamud
/// et ne se testerait pas sous Linux. Le plugin n'appelle qu'une fonction.
/// </remarks>
public static class RendezvousList
{
    /// <summary>Le service par défaut, par son nom.</summary>
    public const string DefaultHost = "rdv.linkpearl.eorzea.events";

    /// <summary>L'autorité du cercle ouvert : le service du projet, sur son port habituel.</summary>
    public static readonly RendezvousAddress Authority = new(DefaultHost, RendezvousAddress.DefaultPort);

    /// <summary>Le service de secours, posé par défaut à côté du principal.</summary>
    /// <remarks>
    /// Chez un autre hébergeur : si le principal tombe, pairages et visibilité
    /// continuent. Même opérateur, donc personne de plus n'apprend qui est en
    /// ligne. Distribué dans le plugin, ce nom doit répondre durablement.
    /// </remarks>
    public const string BackupHost = "rdv.linkpearl-2.eorzea.events";

    /// <summary>Libellés des deux services par défaut, tels qu'affichés dans les réglages.</summary>
    public const string DefaultLabel = "Eorzea Events Linkpearl Primary";

    public const string BackupLabel = "Eorzea Events Linkpearl Secondary";

    private static readonly RendezvousAddress Backup = new(BackupHost, RendezvousAddress.DefaultPort);

    /// <summary>
    /// L'adresse sous laquelle le service par défaut a d'abord été distribué.
    /// </summary>
    /// <remarks>
    /// Une IP nue attache chaque installation à une machine précise : un
    /// changement d'hébergeur aurait coupé tout le monde. Le nom se redirige.
    /// </remarks>
    public const string RetiredDefaultHost = "83.228.242.221";

    /// <summary>
    /// Remplace l'ancienne IP du service par défaut par son nom.
    /// </summary>
    /// <remarks>
    /// Rend la liste telle quelle si elle ne la contient pas, pour que
    /// l'appelant sache qu'il n'a rien à enregistrer. Le port, l'étiquette et
    /// l'interrupteur de l'entrée sont gardés : seul le nom change.
    /// </remarks>
    public static IReadOnlyList<RendezvousEntry> RenameRetiredDefault(IReadOnlyList<RendezvousEntry> current)
    {
        if (current.Any(entry => entry.Address.Host == RetiredDefaultHost) is false)
            return current;

        return current
            .Select(entry => entry.Address.Host == RetiredDefaultHost
                ? entry with { Address = entry.Address with { Host = DefaultHost } }
                : entry)
            .ToList();
    }

    /// <summary>
    /// Ajoute le service de secours à la liste d'origine.
    /// </summary>
    /// <remarks>
    /// N'agit que si la liste est exactement celle que le plugin posait seul :
    /// le service principal, sur son port, sans étiquette, activé. Toute autre
    /// liste est un choix de l'utilisateur, rendue telle quelle pour que
    /// l'appelant sache qu'il n'a rien à enregistrer.
    /// </remarks>
    public static IReadOnlyList<RendezvousEntry> AddBackupDefault(IReadOnlyList<RendezvousEntry> current)
    {
        if (current is not [{ Label: "", Enabled: true } only] || only.Address != Authority)
            return current;

        return [only, new RendezvousEntry(Backup, "", true)];
    }

    /// <summary>
    /// Donne leur libellé aux services par défaut qui n'en ont pas.
    /// </summary>
    /// <remarks>
    /// Vaut pour toute liste, y compris composée à la main : un libellé vide
    /// n'est le choix de personne, alors qu'un libellé saisi en est un et reste.
    /// Rend la liste telle quelle s'il n'y a rien à nommer.
    /// </remarks>
    public static IReadOnlyList<RendezvousEntry> NameDefaults(IReadOnlyList<RendezvousEntry> current)
    {
        static string? LabelOf(RendezvousAddress address)
            => address == Authority ? DefaultLabel
             : address == Backup    ? BackupLabel
             : null;

        if (current.Any(entry => entry.Label.Length == 0 && LabelOf(entry.Address) is not null) is false)
            return current;

        return current
            .Select(entry => entry.Label.Length == 0 && LabelOf(entry.Address) is { } label
                ? entry with { Label = label }
                : entry)
            .ToList();
    }

    /// <summary>
    /// Construit la liste depuis l'ancien réglage, si elle n'existe pas encore.
    /// </summary>
    /// <remarks>
    /// Rend la liste courante telle quelle dès qu'elle porte quelque chose : la
    /// migration ne doit jamais écraser un choix que l'utilisateur a fait depuis.
    /// </remarks>
    public static IReadOnlyList<RendezvousEntry> Migrate(
        string? legacyHost, int legacyPort, IReadOnlyList<RendezvousEntry>? current)
    {
        if (current is { Count: > 0 })
            return current;

        if (string.IsNullOrWhiteSpace(legacyHost))
            return [];

        var text = legacyPort == RendezvousAddress.DefaultPort ? legacyHost : $"{legacyHost}:{legacyPort}";

        return RendezvousAddress.TryParse(text, out var address, out _)
            ? [new RendezvousEntry(address, "", true)]
            : [];
    }
}
