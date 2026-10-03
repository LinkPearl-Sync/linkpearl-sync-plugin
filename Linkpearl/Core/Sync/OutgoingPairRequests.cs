using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;

namespace Linkpearl.Core.Sync;

/// <summary>Ce qu'il faut faire d'une acceptation reçue en réponse à l'une de nos demandes.</summary>
public abstract record AcceptanceVerdict
{
    private AcceptanceVerdict()
    {
    }

    /// <summary>La première acceptation, d'un expéditeur visible : le pair entre au carnet.</summary>
    public sealed record Concluded(byte[] PairingMaterial) : AcceptanceVerdict;

    /// <summary>
    /// Une seconde acceptation du même aléa, sous une autre clé : quelqu'un a
    /// lu la demande et répondu à la place de l'autre, ou avant lui.
    /// </summary>
    /// <param name="First">Le pair ajouté par la première, à retirer.</param>
    public sealed record Conflict(PeerId First) : AcceptanceVerdict;

    /// <summary>L'expéditeur annoncé n'est pas devant nous : rien n'est conclu, la demande reste ouverte.</summary>
    public sealed record NotVisible : AcceptanceVerdict;

    /// <summary>Déjà vue sous cette clé, ou déjà tranchée : rien à faire.</summary>
    public sealed record Repeated : AcceptanceVerdict;

    /// <summary>Aucune demande de notre part ne porte cet aléa : redemander.</summary>
    public sealed record Unknown : AcceptanceVerdict;
}

/// <summary>
/// Nos demandes de pairage en attente, et celles qui viennent d'être acceptées.
/// </summary>
/// <remarks>
/// Une acceptation n'est pas une preuve. Elle arrive par la boîte aux lettres
/// du service, et quiconque a lu notre demande en connaît l'aléa : jusqu'à la
/// réclamation exclusive des boîtes, n'importe quel client pouvait ouvrir celle
/// de la cible, et un service ancien ou malveillant le peut toujours. Conclure
/// sur la première acceptation, c'était épingler le nom de la cible sur la clé
/// du plus rapide.
///
/// D'où deux règles. L'expéditeur annoncé doit être devant nous au moment où
/// l'on tranche, comme le veut le pairage en face à face. Et l'aléa reste
/// surveillé <see cref="Watch"/> après la première acceptation : une seconde,
/// sous une autre clé, trahit qu'il y a eu deux répondants, et rien ne dit
/// lequel est le bon. Les deux sont alors écartés, et il faut redemander.
///
/// Sûr entre fils : les acceptations arrivent sur les fils d'écoute, et
/// l'interface demande depuis le sien.
/// </remarks>
public sealed class OutgoingPairRequests(IClock clock) : IDisposable
{
    /// <summary>
    /// Combien de temps une acceptation conclue reste contestable.
    /// </summary>
    /// <remarks>
    /// Dix minutes : bien plus que le temps qu'il faut au vrai destinataire
    /// pour cliquer après un intrus, et assez court pour que la surveillance
    /// ne s'accumule pas.
    /// </remarks>
    public static readonly TimeSpan Watch = TimeSpan.FromMinutes(10);

    private sealed class Pending(byte[] nonce, ECDiffieHellman ephemeral)
    {
        public byte[] Nonce { get; } = nonce;

        /// <summary>La moitié privée de l'accord, oubliée dès la première conclusion.</summary>
        public ECDiffieHellman? Ephemeral { get; set; } = ephemeral;

        /// <summary>Le pair de la première acceptation, et quand elle a été conclue.</summary>
        public PeerId? ConcludedWith { get; set; }

        public DateTimeOffset ConcludedAt { get; set; }

        /// <summary>Une seconde clé est venue : plus rien ne se conclut sur cet aléa.</summary>
        public bool Annulled { get; set; }
    }

    private readonly Dictionary<PlayerFingerprint, Pending> _byTarget = [];
    private readonly Lock _gate = new();

    /// <summary>Retient une demande envoyée. Une nouvelle demande à la même personne remplace l'ancienne.</summary>
    /// <remarks>
    /// L'ancienne ne peut plus conclure, y compris pendant sa surveillance :
    /// redemander, c'est repartir d'un aléa neuf.
    /// </remarks>
    public void Add(PlayerFingerprint target, byte[] nonce, ECDiffieHellman ephemeral)
    {
        lock (_gate)
        {
            if (_byTarget.Remove(target, out var previous))
                previous.Ephemeral?.Dispose();

            _byTarget[target] = new Pending(nonce, ephemeral);
        }
    }

    /// <summary>Vrai si une demande à cette personne attend encore sa réponse.</summary>
    public bool IsPending(PlayerFingerprint target)
    {
        lock (_gate)
            return _byTarget.TryGetValue(target, out var pending) && pending.ConcludedWith is null;
    }

    /// <summary>Vrai si cet aléa est celui d'une demande en attente ou sous surveillance.</summary>
    /// <remarks>Le tri du fil d'écoute : tout le reste est jeté sans attendre.</remarks>
    public bool Concerns(PlayerFingerprint sender, ReadOnlySpan<byte> nonce)
    {
        lock (_gate)
        {
            Purge();
            return _byTarget.TryGetValue(sender, out var pending) && pending.Nonce.AsSpan().SequenceEqual(nonce);
        }
    }

    /// <summary>
    /// Tranche une acceptation reçue de <paramref name="sender"/>, sous la clé <paramref name="from"/>.
    /// </summary>
    /// <param name="senderVisible">L'expéditeur annoncé, nom et monde, est-il devant nous à cet instant ?</param>
    public AcceptanceVerdict Settle(
        PlayerFingerprint sender, PeerId from, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> theirEphemeral,
        bool senderVisible)
    {
        lock (_gate)
        {
            Purge();

            if (_byTarget.TryGetValue(sender, out var pending) is false
                || pending.Nonce.AsSpan().SequenceEqual(nonce) is false)
                return new AcceptanceVerdict.Unknown();

            if (pending.Annulled)
                return new AcceptanceVerdict.Repeated();

            // Déjà conclue : la même clé n'est qu'un écho par un autre service,
            // une autre clé est un second répondant. Peu importe ici que
            // l'expéditeur soit visible : la seconde réponse prouve à elle
            // seule que l'aléa a fui.
            if (pending.ConcludedWith is { } first)
            {
                if (first == from)
                    return new AcceptanceVerdict.Repeated();

                pending.Annulled = true;
                return new AcceptanceVerdict.Conflict(first);
            }

            // La demande reste ouverte : le vrai destinataire, lui, est devant
            // nous, et sa réponse peut encore conclure.
            if (senderVisible is false)
                return new AcceptanceVerdict.NotVisible();

            using var ours = pending.Ephemeral!;
            pending.Ephemeral = null;
            pending.ConcludedWith = from;
            pending.ConcludedAt = clock.UtcNow;

            return new AcceptanceVerdict.Concluded(PairRequestMessage.AgreeOnPairing(ours, theirEphemeral, nonce));
        }
    }

    /// <summary>Oublie tout, au changement de personnage.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var pending in _byTarget.Values)
                pending.Ephemeral?.Dispose();

            _byTarget.Clear();
        }
    }

    /// <summary>Lâche les demandes dont la surveillance est échue. Sous le verrou.</summary>
    /// <remarks>
    /// Une demande sans réponse n'expire pas : un refus ne revient jamais, et
    /// l'interface laisse renvoyer. Elle ne coûte qu'un éphémère par personne
    /// sollicitée.
    /// </remarks>
    private void Purge()
    {
        var now = clock.UtcNow;

        foreach (var (target, pending) in _byTarget.ToList())
        {
            if (pending.ConcludedWith is not null && now - pending.ConcludedAt >= Watch)
                _byTarget.Remove(target);
        }
    }

    public void Dispose() => Clear();
}
