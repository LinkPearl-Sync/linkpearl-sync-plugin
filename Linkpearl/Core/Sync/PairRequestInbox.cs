using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;

namespace Linkpearl.Core.Sync;

/// <summary>Ce qu'est devenue une demande de pairage reçue.</summary>
public enum InboxOutcome
{
    /// <summary>Nouvelle, montrée à l'utilisateur.</summary>
    Added,

    /// <summary>Remplace la demande en attente du même expéditeur.</summary>
    Replaced,

    /// <summary>Trop de demandes en attente : jetée.</summary>
    Full,
}

/// <summary>
/// Les demandes de pairage reçues, en attente de la décision de l'utilisateur.
/// </summary>
/// <remarks>
/// Déposer dans une boîte ne coûte rien, et chaque dépôt fabriquait une carte
/// à l'écran : une rafale de demandes sous des noms inventés couvrait l'écran
/// de fenêtres, et la mémoire des aléas déjà vus grandissait sans fin. D'où les
/// bornes : une seule demande par expéditeur (la dernière remplace, celui qui
/// redemande a peut-être perdu sa première), <see cref="Capacity"/> au plus,
/// chacune oubliée après <see cref="Lifetime"/>, et une mémoire des aléas vus
/// bornée elle aussi.
///
/// Un expéditeur, c'est à la fois une clé et un personnage annoncé : une
/// nouvelle demande de l'un ou de l'autre remplace l'ancienne, sans quoi un
/// même nom pourrait se montrer sous autant de clés qu'il en tire.
///
/// Générique sur la demande, que l'adaptateur garde à lui : c'est lui qui
/// relie un nom en clair à une empreinte. Sûr entre fils : les dépôts arrivent
/// sur les fils d'écoute, et l'interface lit depuis le sien.
/// </remarks>
public sealed class PairRequestInbox<T>(IClock clock) where T : class
{
    /// <summary>
    /// Plafond des demandes en attente. Vingt cartes, c'est déjà plus que ce
    /// qu'un joueur traite d'un coup ; au-delà, c'est une rafale.
    /// </summary>
    public const int Capacity = 20;

    /// <summary>
    /// Une demande n'attend pas plus de dix minutes : elle se fait en face à
    /// face, et celui qui a demandé est parti depuis longtemps.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Mémoire des aléas déjà vus. Assez pour dédoublonner une même demande
    /// arrivée par chacun de nos services, pas assez pour qu'une rafale
    /// fasse grandir la mémoire sans fin.
    /// </summary>
    public const int SeenCapacity = 512;

    private sealed record Entry(PeerId Id, PlayerFingerprint Sender, DateTimeOffset At, T Request);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, DateTimeOffset> _seen = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Purge();
                return _entries.Count;
            }
        }
    }

    /// <summary>Range une demande reçue de la clé <paramref name="id"/>, au nom de <paramref name="sender"/>.</summary>
    /// <remarks>
    /// Sans dédoublonnage : c'est <see cref="Witness"/> qui le fait, avant,
    /// pour que l'appelant n'engage ses vérifications coûteuses (listes de
    /// bannissement) qu'une fois par demande.
    /// </remarks>
    public InboxOutcome Offer(PeerId id, PlayerFingerprint sender, T request)
    {
        lock (_gate)
        {
            Purge();

            var replaced = _entries.RemoveAll(entry => entry.Id == id || entry.Sender == sender) > 0;

            // Les demandes déjà montrées restent : en évincer une au profit de
            // la dernière venue laisserait une rafale effacer celle de la
            // personne qu'on a devant soi.
            if (replaced is false && _entries.Count >= Capacity)
                return InboxOutcome.Full;

            _entries.Add(new Entry(id, sender, clock.UtcNow, request));
            return replaced ? InboxOutcome.Replaced : InboxOutcome.Added;
        }
    }

    /// <summary>
    /// Vrai la première fois que ce couple clé et aléa se présente, faux pour ses échos.
    /// </summary>
    /// <remarks>
    /// Un expéditeur qui dépose sur plusieurs services ne doit produire
    /// qu'une seule invite, ou une seule acceptation à trancher : la même
    /// trame nous arrive par autant de chemins que nous partageons de services
    /// avec lui. La mémoire est bornée et oublie au bout de <see cref="Lifetime"/>.
    /// </remarks>
    public bool Witness(PeerId id, ReadOnlySpan<byte> nonce)
    {
        var key = $"{id.ToHex()}:{Convert.ToHexStringLower(nonce)}";

        lock (_gate)
        {
            PurgeSeen();

            if (_seen.ContainsKey(key))
                return false;

            Remember(key);
            return true;
        }
    }

    public IReadOnlyList<T> Peek()
    {
        lock (_gate)
        {
            Purge();
            return [.. _entries.Select(entry => entry.Request)];
        }
    }

    /// <summary>Retire une demande à laquelle l'utilisateur vient de répondre.</summary>
    public bool Remove(T request)
    {
        lock (_gate)
            return _entries.RemoveAll(entry => ReferenceEquals(entry.Request, request)) > 0;
    }

    /// <summary>Oublie tout, au changement de personnage.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _seen.Clear();
        }
    }

    /// <summary>Sous le verrou.</summary>
    private void Remember(string key)
    {
        // Les plus anciens d'abord : ce sont ceux dont un écho tardif est le
        // moins probable.
        if (_seen.Count >= SeenCapacity)
        {
            foreach (var oldest in _seen.OrderBy(seen => seen.Value).Take(_seen.Count - SeenCapacity + 1).ToList())
                _seen.Remove(oldest.Key);
        }

        _seen[key] = clock.UtcNow;
    }

    /// <summary>Sous le verrou. Appelé à chaque image par l'interface : les seules demandes.</summary>
    private void Purge()
    {
        var now = clock.UtcNow;
        _entries.RemoveAll(entry => now - entry.At >= Lifetime);
    }

    /// <summary>Sous le verrou. Au dépôt seulement, qui est le seul à faire grandir la mémoire.</summary>
    private void PurgeSeen()
    {
        var now = clock.UtcNow;

        foreach (var stale in _seen.Where(seen => now - seen.Value >= Lifetime).Select(seen => seen.Key).ToList())
            _seen.Remove(stale);
    }
}
