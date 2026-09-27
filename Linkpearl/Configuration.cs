using Dalamud.Configuration;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Sync;

namespace Linkpearl;

/// <summary>Réglages du plugin, conservés par Dalamud.</summary>
public sealed class Configuration : IPluginConfiguration, ICacheConfiguration
{
    /// <summary>Génération des réglages, pour les migrations à faire une seule fois.</summary>
    /// <remarks>
    /// Reste à 2 par défaut : une installation neuve passe ainsi par les mêmes
    /// migrations qu'une ancienne, et reçoit le service de secours.
    /// </remarks>
    public int Version { get; set; } = 2;

    /// <summary>
    /// Hôte du service de rendez-vous.
    /// </summary>
    /// <remarks>
    /// Pré-rempli, parce qu'un ticket de douze caractères ne peut pas porter le
    /// serveur : les deux personnes doivent donc avoir réglé le même. Un défaut
    /// évite d'avoir à le dire à chaque nouvel arrivant.
    ///
    /// Réglable, et c'est la raison d'être du projet : si ce service tombe ou
    /// reçoit une lettre d'avocat, on en change sans rien reconstruire.
    /// </remarks>
    public string RendezvousHost { get; set; } = RendezvousList.DefaultHost;

    public int RendezvousPort { get; set; } = 47900;

    /// <summary>
    /// Les services de rendez-vous, par ordre de préférence.
    /// </summary>
    /// <remarks>
    /// Une liste et non un service unique : c'est ce qui permet de voir des
    /// joueurs qui n'ont pas fait le même choix que vous. Chaque entrée activée
    /// apprend que votre personnage est en ligne et qui se tient autour de vous,
    /// donc la liste reste courte par défaut.
    /// </remarks>
    public List<RendezvousEntry> Rendezvous { get; set; } = [];

    /// <summary>
    /// Passer par le cercle ouvert pour les pairs déjà épinglés.
    /// </summary>
    /// <remarks>
    /// Activé par défaut : il ne voit jamais passer une clé, et il répartit la
    /// charge et le relais. Désactivé, tout passe par les services de la liste.
    /// </remarks>
    public bool OpenCircle { get; set; } = true;

    /// <summary>Les services activés, ceux que le moteur emploiera.</summary>
    public IReadOnlyList<RendezvousEntry> ActiveRendezvous =>
        Rendezvous.Where(entry => entry.Enabled).ToList();

    /// <summary>
    /// Construit la liste depuis l'ancien réglage, la première fois.
    /// </summary>
    /// <remarks>
    /// <c>RendezvousHost</c> et <c>RendezvousPort</c> restent lus pour cela, et
    /// pour cela seulement : ils ne sont plus la source de vérité.
    /// </remarks>
    public void MigrateIfNeeded()
    {
        var migrated = RendezvousList.RenameRetiredDefault(
            RendezvousList.Migrate(RendezvousHost, RendezvousPort, Rendezvous));

        // Une seule fois : un joueur qui retire ensuite le secours ne doit pas
        // le voir revenir au chargement suivant.
        var firstOfV3 = Version < 3;

        if (firstOfV3)
            migrated = RendezvousList.NameDefaults(RendezvousList.AddBackupDefault(migrated));

        var renamed = RendezvousHost == RendezvousList.RetiredDefaultHost;

        if (renamed)
            RendezvousHost = RendezvousList.DefaultHost;

        if (ReferenceEquals(migrated, Rendezvous) && renamed is false && firstOfV3 is false)
            return;

        Rendezvous = [.. migrated];
        Version = 3;
        Save();
    }

    /// <summary>
    /// Se signaler aux autres joueurs.
    /// </summary>
    /// <remarks>
    /// Activé par défaut : une fonction désactivée par défaut n'existe pas, et
    /// sans elle personne ne se trouve. Le prix est que l'opérateur du
    /// rendez-vous peut savoir quels personnages sont en ligne, parce qu'une
    /// adresse de boîte dérive du nom. Être découvrable par un inconnu implique
    /// de l'être par le serveur.
    /// </remarks>
    public bool Discoverable { get; set; } = true;

    /// <summary>Répertoire du cache. Vide pour le défaut sous LOCALAPPDATA.</summary>
    public string CacheDirectory { get; set; } = "";

    /// <summary>Taille maximale du cache.</summary>
    /// <remarks>
    /// 50 Go : une apparence pèse environ 800 Mo, soit une soixantaine
    /// d'apparences. Une configuration qui avait enregistré l'ancien défaut de
    /// 20 Go le garde.
    /// </remarks>
    public long CacheQuotaBytes { get; set; } = 50L * 1024 * 1024 * 1024;

    /// <summary>La présentation a été fermée une fois.</summary>
    public bool OnboardingSeen { get; set; }

    /// <summary>Le cache a déjà créé son dossier : sa disparition bloque le plugin.</summary>
    public bool CacheEstablished { get; set; }

    /// <summary>L'ancien dossier après un changement, à proposer à la suppression.</summary>
    public string PreviousCacheDirectory { get; set; } = "";

    /// <summary>Un badge aux pieds des pairs dont l'apparence arrive ou se fait attendre.</summary>
    public bool ShowTransferBadges { get; set; } = true;

    /// <summary>Un glyphe coloré à droite du nom des joueurs qui utilisent Linkpearl.</summary>
    public bool ShowNameplateGlyphs { get; set; } = true;

    /// <summary>
    /// Brider l'envoi pour préserver le ping.
    /// </summary>
    /// <remarks>
    /// Désactivé par défaut : les joueurs visés font du jeu de rôle, pas du
    /// donjon, et quelques millisecondes de ping leur coûtent moins qu'une tenue
    /// qui met des minutes à arriver.
    /// </remarks>
    public bool LimitUpload { get; set; }

    /// <summary>Plafond d'émission, en octets par seconde.</summary>
    public long UploadCeilingBytesPerSecond { get; set; } = 8 * 1024 * 1024;

    /// <summary>Animations, VFX et sons reçus de tous les pairs, avant le réglage de chacun.</summary>
    /// <remarks>
    /// Tout par défaut : une idle ou une pose assise moddée est ce que les
    /// joueurs visés veulent voir. Couper se fait d'un clic dans la barre du
    /// haut, sans passer par les réglages.
    /// </remarks>
    public bool ReceiveAnimations { get; set; } = true;

    public bool ReceiveVfx { get; set; } = true;

    public bool ReceiveSounds { get; set; } = true;

    /// <summary>Le rappel de sauvegarder a été fait, ou une sauvegarde faite.</summary>
    /// <remarks>
    /// Une seule fois : un rappel qui revient à chaque connexion s'apprend à
    /// ignorer, et ne sert alors plus à rien.
    /// </remarks>
    public bool BackupReminded { get; set; }

    /// <summary>Une sauvegarde a été faite, ou restaurée : le rappel de la page Pairs se tait.</summary>
    /// <remarks>
    /// Pour tout le poste et non par personnage : une sauvegarde les emporte
    /// tous. Un personnage créé après elle n'y figure pas, et n'est pas rappelé.
    /// </remarks>
    public bool BackedUp { get; set; }

    /// <summary>L'utilisateur a demandé à ne plus voir le rappel de la page Pairs.</summary>
    public bool BackupNudgeDismissed { get; set; }

    /// <summary>
    /// Vrai une fois l'avertissement de Public lu.
    /// </summary>
    /// <remarks>La spec veut qu'il s'affiche une fois : la première activation, pas chaque bascule.</remarks>
    public bool PublicWarningSeen { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
