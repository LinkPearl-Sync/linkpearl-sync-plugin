using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Integration;

/// <summary>
/// Identité et carnet de pairs du personnage connecté.
/// </summary>
public sealed class PairingService : IDisposable
{
    private readonly PairBook _book;
    private readonly Configuration _configuration;
    private readonly IPluginLog _log;

    private IdentityKeyPair? _identity;
    private PairBookStore? _bookStore;

    public PairingService(Configuration configuration, SystemClock clock, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
        _book = new PairBook(clock);
    }

    /// <summary>Ce qu'on répond tant qu'aucun personnage n'est connecté.</summary>
    private const string NoCharacter =
        "connexion à un personnage requise : chaque personnage possède sa propre identité Linkpearl.";

    /// <summary>Vrai quand une identité de personnage est chargée.</summary>
    public bool IsBound => _identity is not null;

    /// <summary>
    /// Reprend l'identité et le carnet d'un personnage.
    /// </summary>
    /// <remarks>
    /// Par personnage et non par installation : deux personnages sur une même
    /// machine sont deux pairs distincts, que personne ne peut relier l'un à
    /// l'autre. C'est cohérent avec le reste, où la boîte aux lettres et la
    /// liste de bannissement portent déjà sur un personnage, et c'est ce qui
    /// permet à deux clients de la même machine de se pairer pour de vrai.
    /// </remarks>
    public void Bind(string root)
    {
        Unbind();

        _identity = IdentityKeyPair.LoadOrCreate(new DpapiIdentityStore(Path.Combine(root, "identity.key")));

        _bookStore = new PairBookStore(Path.Combine(root, "pairs.json"));
        _bookStore.Load(_book);

        if (_book.ForgetStaleRevocations() > 0)
            _bookStore.Save(_book);
    }

    /// <summary>Repose tout à la déconnexion.</summary>
    /// <remarks>
    /// Le carnet est vidé, sans quoi le personnage suivant réécrirait dans son
    /// propre fichier les pairs de celui d'avant.
    /// </remarks>
    public void Unbind()
    {
        _identity?.Dispose();
        _identity = null;
        _bookStore = null;

        _book.Clear();
    }

    /// <summary>Nos propres lieux de rendez-vous, ceux qui sont activés.</summary>
    /// <remarks>
    /// Vide quand l'utilisateur les a tous retirés : les appelants doivent le
    /// dire plutôt que de tomber sur un index hors bornes.
    /// </remarks>
    private IReadOnlyList<RendezvousAddress> Here() =>
        [.. _configuration.ActiveRendezvous.Select(entry => entry.Address)];

    /// <summary>Ce qu'on répond quand l'utilisateur n'a plus aucun service actif.</summary>
    /// <remarks>
    /// Le cas est atteignable en deux clics dans les réglages, et sans message
    /// il se manifesterait par un index hors bornes que personne ne saurait
    /// relier à la case qu'il vient de décocher.
    /// </remarks>
    private const string NoService =
        "aucun service Linkpearl actif. En ajouter un dans les réglages.";

    public PeerId? Id => _identity?.Id;

    public IdentityKeyPair? Identity => _identity;

    /// <summary>Ajoute un pair depuis une demande acceptée dans l'interface.</summary>
    /// <remarks>
    /// Un pair déjà au carnet est remplacé, secret compris. S'il nous redemande,
    /// c'est qu'il nous a perdus (retrait, réinstallation) et qu'il repart d'un
    /// nouveau secret : garder l'ancien laisserait deux carnets qui ne se
    /// rejoignent plus jamais, chacun croyant l'autre simplement hors ligne.
    /// </remarks>
    public string AddFromRequest(IncomingRequest request)
    {
        if (_identity is null || _bookStore is null)
            return NoCharacter;

        // Sans accord, pas de secret : une demande non encore acceptée n'a pas
        // sa place au carnet, et l'y mettre avec l'aléa seul referait l'erreur
        // de la version 1, un secret que le rendez-vous connaît.
        if (request.PairingMaterial is not { } material)
            return "pairage incomplet avec ce joueur.";

        var again = _book.Find(request.Id) is { Trust: not PairTrust.Revoked };

        _book.Add(request.Id, request.PublicKey, material, _identity.Id,
                  request.CharacterName, Here());

        // Épinglé dès maintenant et non à la première session : jusque-là, rien
        // ne reliait le pair au joueur qu'on a devant soi, et le menu comme la
        // page « Autour de vous » continuaient de proposer un pairage déjà fait.
        // C'est aussi le personnage auquel on vient de dire oui : un pair qui en
        // annoncerait un autre sera contesté plutôt qu'épinglé à la place.
        _book.PinFingerprint(request.Id, PlayerFingerprint.Of(
            DalamudObjectSource.Normalize(request.CharacterName), request.WorldId));
        _bookStore.Save(_book);

        return again
            ? $"Nouveau pairage avec {request.CharacterName}."
            : $"{request.CharacterName} ajouté au carnet.";
    }

    /// <summary>Enregistre un carnet que le moteur vient de modifier.</summary>
    public void Save() => _bookStore?.Save(_book);

    public PairBook Book => _book;

    /// <summary>Met un pair en pause, ou l'en sort.</summary>
    /// <remarks>
    /// En pause, le moteur ferme la session et retire ce qu'il avait posé :
    /// c'est ce qui fait de la pause un bouton dont on voit l'effet.
    /// </remarks>
    public string SetPaused(PeerId id, bool paused)
    {
        if (_bookStore is null)
            return NoCharacter;

        if (_book.Find(id) is not { } record)
            return "pair inconnu.";

        _book.SetPaused(id, paused);
        _bookStore.Save(_book);
        return paused ? $"{record.DisplayName} est en pause." : $"{record.DisplayName} repris.";
    }

    /// <summary>Change les animations, VFX et sons acceptés de ce pair.</summary>
    public string SetReceive(PeerId id, TransientCategories receive)
    {
        if (_bookStore is null)
            return NoCharacter;

        if (_book.Find(id) is null)
            return "pair inconnu.";

        _book.SetReceive(id, receive);
        _bookStore.Save(_book);
        return string.Empty;
    }

    /// <summary>Retire un pair du carnet.</summary>
    /// <remarks>
    /// Retiré de la liste tout de suite, mais gardé en retrait tant qu'il n'a
    /// pas été prévenu : le moteur le joint une dernière fois pour le lui dire.
    /// Un pair dont on n'a jamais appris la clé n'a jamais pu se joindre, et
    /// part pour de bon.
    /// </remarks>
    public string Remove(PeerId id)
    {
        if (_bookStore is null)
            return NoCharacter;

        if (_book.Find(id) is not { } record)
            return "pair inconnu.";

        if (record.PublicKey is null)
            _book.Remove(id);
        else
            _book.Revoke(id);

        _bookStore.Save(_book);
        return $"{record.DisplayName} retiré du carnet.";
    }

    public void Dispose() => Unbind();
}
