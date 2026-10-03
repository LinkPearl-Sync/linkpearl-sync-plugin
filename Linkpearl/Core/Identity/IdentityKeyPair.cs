using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;

namespace Linkpearl.Core.Identity;

/// <summary>
/// L'identité de cette installation.
/// </summary>
/// <remarks>
/// Générée au premier lancement et conservée telle quelle : la perdre, c'est
/// perdre tous les pairages, puisque c'est elle que les autres ont épinglée. Un
/// export et un import sont donc indispensables, sans quoi réinstaller Windows
/// obligerait à tout refaire.
/// </remarks>
public sealed class IdentityKeyPair : IDisposable
{
    private IdentityKeyPair(ECDsa key)
    {
        Key = key;
        PublicKey = CryptoPrimitives.ExportPublicPoint(key);
        Id = PeerId.Of(PublicKey);
    }

    public ECDsa Key { get; }

    public byte[] PublicKey { get; }

    public PeerId Id { get; }

    /// <summary>Charge l'identité existante, ou en crée une au premier lancement.</summary>
    public static IdentityKeyPair LoadOrCreate(IIdentityStore store)
    {
        var stored = store.Load();

        // Identité illisible, ou d'une autre courbe : on en crée une neuve
        // plutôt que d'empêcher le plugin de démarrer. Les pairages seront à
        // refaire, et l'interface doit le dire.
        if (stored is not null && TryImport(stored, out var existing, out _))
            return new IdentityKeyPair(existing);

        var created = CryptoPrimitives.GenerateIdentity();
        store.Save(created.ExportPkcs8PrivateKey());
        return new IdentityKeyPair(created);
    }

    /// <summary>
    /// Importe une clé privée d'identité, à condition qu'elle soit sur P-256.
    /// </summary>
    /// <remarks>
    /// <c>ImportPkcs8PrivateKey</c> accepte toute courbe, P-384 comprise. Une
    /// telle clé passait l'import, puis faisait lever une exception non
    /// attrapée à l'export du point public, de taille fixe : le personnage ne
    /// se chargeait plus. Une sauvegarde forgée ou corrompue suffisait.
    /// </remarks>
    public static bool TryImport(
        ReadOnlySpan<byte> pkcs8, [NotNullWhen(true)] out ECDsa? key, [NotNullWhen(false)] out string? rejection)
    {
        key = null;
        var candidate = ECDsa.Create();

        try
        {
            candidate.ImportPkcs8PrivateKey(pkcs8, out _);

            var parameters = candidate.ExportParameters(true);

            if (IsP256(parameters.Curve) is false
                || parameters.D is not { Length: PrivateScalarLength }
                || parameters.Q.X is not { Length: PrivateScalarLength }
                || parameters.Q.Y is not { Length: PrivateScalarLength })
            {
                rejection = "clé hors de la courbe P-256";
                candidate.Dispose();
                return false;
            }

            key = candidate;
            rejection = null;
            return true;
        }
        catch (CryptographicException e)
        {
            rejection = $"clé illisible : {e.Message}";
            candidate.Dispose();
            return false;
        }
    }

    /// <summary>Un scalaire privé P-256 : 256 bits, comme chaque coordonnée du point public.</summary>
    private const int PrivateScalarLength = 32;

    /// <summary>
    /// Vrai pour P-256 et elle seule, quel que soit le nom que lui donne le système.
    /// </summary>
    /// <remarks>
    /// Par l'OID d'abord. Mais CNG, sous Windows, rend parfois une courbe
    /// nommée sans valeur d'OID, seulement un nom convivial : on accepte alors
    /// les noms sous lesquels il désigne P-256, et rien d'autre. Une courbe de
    /// 256 bits qui ne serait pas P-256, brainpool ou secp256k1, signerait des
    /// handshakes que personne ne vérifierait.
    /// </remarks>
    private static bool IsP256(ECCurve curve)
    {
        if (curve.IsNamed is false || curve.Oid is not { } oid)
            return false;

        if (oid.Value is { } value)
            return value == ECCurve.NamedCurves.nistP256.Oid.Value;

        return oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secP256r1";
    }

    public PairingCode NewInvitation(string rendezvousHost) => PairingCode.Create(Id, rendezvousHost);

    public void Dispose() => Key.Dispose();
}
