namespace Linkpearl.Core.Safety;

/// <summary>
/// Plafonds appliqués à toute donnée venant d'un pair.
/// </summary>
/// <remarks>
/// Un dépassement coupe la session plutôt que de tronquer : une donnée tronquée
/// donne un personnage incohérent et masque une tentative d'abus.
/// </remarks>
public sealed record Quotas
{
    public static Quotas Default { get; } = new();

    /// <summary>Nombre de remplacements de fichiers dans un manifeste.</summary>
    public int MaxReplacements { get; init; } = 2_000;

    /// <summary>Nombre total de chemins de jeu, plusieurs pouvant viser un même contenu.</summary>
    public int MaxGamePaths { get; init; } = 8_000;

    /// <summary>Longueur d'un chemin de jeu, en caractères.</summary>
    public int MaxGamePathLength { get; init; } = 256;

    /// <summary>Profondeur d'un chemin de jeu, en segments.</summary>
    public int MaxGamePathDepth { get; init; } = 16;

    /// <summary>Taille d'un blob transféré.</summary>
    public long MaxBlobBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>
    /// Somme des tailles des blobs distincts d'un manifeste.
    /// </summary>
    /// <remarks>
    /// Une apparence moyenne pèse environ 800 Mo, selon l'utilisateur (voir
    /// docs/reprise.md), et celle qui sert aux mesures du faux pair 405 Mo.
    /// Cinq fois la moyenne laisse passer les tenues les plus lourdes ;
    /// au-delà, c'est un pair qui veut remplir notre disque. Le moteur y
    /// ajoute un plafond relatif au quota du cache, que le validateur ne
    /// connaît pas.
    /// </remarks>
    public long MaxManifestTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Taille d'un manifeste tel qu'il arrive sur le réseau.</summary>
    public int MaxManifestCompressedBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Taille d'un manifeste une fois détendu.
    /// </summary>
    /// <remarks>
    /// Un pair peut envoyer un mégaoctet qui se détend en plusieurs gigaoctets.
    /// La lecture s'arrête ici plutôt que de remplir la mémoire du processus du jeu.
    ///
    /// Le plus gros manifeste honnête que les autres plafonds laissent passer
    /// pèse 4,7 Mo encodé : 2 000 entrées et 6 000 échanges de deux chemins,
    /// tous de 256 caractères, la méta, l'état Glamourer et chaque extra à son
    /// plafond (mesuré par
    /// <c>Le_plus_gros_manifeste_honnete_tient_sous_le_plafond_de_detente</c>).
    /// Un manifeste réel en est très loin : quelques centaines de Kio. Seul un
    /// manifeste hostile, qui ferait échapper chaque caractère de ses chaînes,
    /// dépasse ces 5 Mio, et il serait refusé de toute façon.
    /// </remarks>
    public int MaxManifestDecompressedBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Chaîne de manipulations méta de Penumbra, opaque pour nous.</summary>
    /// <remarks>
    /// Gardé tel quel faute de mesure : aucune capture réelle n'a encore été
    /// relevée, et un plafond trop bas ferait disparaître un pair entier. La
    /// détente, elle, est bornée par <see cref="MaxMetaManipulationsDecompressedBytes"/>.
    /// </remarks>
    public int MaxMetaManipulationChars { get; init; } = 512 * 1024;

    /// <summary>
    /// Manipulations méta une fois détendues.
    /// </summary>
    /// <remarks>
    /// Penumbra les détend sans plafond, sur le thread du jeu, à chaque
    /// AddTemporaryMod. Une manipulation tient en une vingtaine d'octets dans
    /// le format binaire (version 1), en une centaine dans l'ancien format
    /// JSON (version 0) : 4 Mio laissent la place à des dizaines de milliers,
    /// quand un personnage en porte quelques centaines.
    /// </remarks>
    public int MaxMetaManipulationsDecompressedBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Chaîne d'état de Glamourer, opaque pour nous.</summary>
    public int MaxGlamourerStateChars { get; init; } = 64 * 1024;

    /// <summary>
    /// État Glamourer une fois détendu.
    /// </summary>
    /// <remarks>
    /// Un design JSON : équipement, apparence, et les teintures avancées, qui
    /// en sont la plus grosse part avec quelques centaines d'octets par ligne
    /// de table de couleurs. 2 Mio, c'est des milliers de lignes de plus que ce
    /// qu'un personnage peut porter.
    /// </remarks>
    public int MaxGlamourerStateDecompressedBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Profil Customize+, JSON des os : de 1 à 15 Kio relevés, marge large.</summary>
    public int MaxCustomizePlusChars { get; init; } = 64 * 1024;

    /// <summary>Configuration SimpleHeels nettoyée : moins de 1 Kio relevé.</summary>
    public int MaxHeelsChars { get; init; } = 16 * 1024;

    /// <summary>Titre Honorific : moins de 250 octets relevés.</summary>
    public int MaxHonorificChars { get; init; } = 4 * 1024;

    /// <summary>Honorific refuse lui-même d'afficher plus de 32 caractères.</summary>
    public int MaxHonorificTitleLength { get; init; } = 32;

    /// <summary>Moodles en base64 : de 0,5 à 3 Kio relevés.</summary>
    public int MaxMoodlesChars { get; init; } = 32 * 1024;

    /// <summary>PetNicknames en base64 : de 0,5 à 3 Kio relevés.</summary>
    public int MaxPetNicknamesChars { get; init; } = 16 * 1024;

    /// <summary>Profondeur maximale des extras JSON.</summary>
    public int MaxExtrasJsonDepth { get; init; } = 8;
}
