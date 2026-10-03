using System.Buffers.Binary;

namespace Linkpearl.Core.Crypto;

/// <summary>
/// Le canal de données : chiffrement authentifié au niveau du message.
/// </summary>
/// <remarks>
/// Au niveau du message applicatif et non du datagramme. Chiffrer chaque
/// datagramme s'appliquerait aussi aux paquets de connexion et à la découverte
/// de MTU du transport, ce qui rendrait le handshake circulaire, et l'expansion
/// se paierait sur chaque fragment. Sur un bloc de 16 Kio, l'en-tête et
/// l'étiquette coûtent 26 octets, soit 0,16 %.
///
/// Conséquence assumée : les en-têtes du transport restent en clair. Ils ne
/// révèlent rien qu'un observateur ne déduirait des tailles et des temps.
/// </remarks>
public sealed class SecureChannel
{
    /// <summary>Type(1) || canal(1) || compteur(8), le tout en donnée associée.</summary>
    public const int HeaderLength = 10;

    /// <summary>Le compteur réserve son octet de poids fort à l'index de canal.</summary>
    public const byte MaxChannels = 64;

    private const int NoncePrefixLength = 4;
    private const ulong MaxSequence = (1UL << 56) - 1;

    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly byte[] _noncePrefix;
    private readonly ulong[] _sent = new ulong[MaxChannels];
    private readonly ulong[] _received = new ulong[MaxChannels];

    public SecureChannel(byte[] sendKey, byte[] receiveKey, ReadOnlySpan<byte> sessionId)
    {
        _sendKey = sendKey;
        _receiveKey = receiveKey;
        _noncePrefix = sessionId[..NoncePrefixLength].ToArray();
    }

    public byte[] Seal(byte channel, byte kind, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, MaxChannels);

        // Incrément atomique : plusieurs blobs sont servis de front, et deux
        // scellements qui liraient le même compteur produiraient deux fois le
        // même nonce. En AES-GCM, un nonce réutilisé sous la même clé ne fuit
        // pas seulement les deux messages, il livre de quoi forger.
        // L'atomicité protège le nonce, pas l'ordre : le receveur refuse un
        // compteur inférieur au dernier vu, donc l'appelant doit remettre les
        // trames d'un canal au lien dans l'ordre où elles ont été scellées.
        // PeerSession le fait sous un verrou par canal.
        var sequence = System.Threading.Interlocked.Increment(ref _sent[channel]);

        if (sequence > MaxSequence)
            throw new InvalidOperationException("compteur de canal épuisé : la session doit être renégociée");

        // L'index de canal occupe l'octet de poids fort du compteur : deux
        // canaux ne peuvent donc pas produire le même nonce, par construction
        // et non par convention.
        var counter = ((ulong)channel << 56) | sequence;

        var frame = new byte[HeaderLength + payload.Length + CryptoPrimitives.TagLength];
        frame[0] = kind;
        frame[1] = channel;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), counter);

        Span<byte> nonce = stackalloc byte[CryptoPrimitives.NonceLength];
        WriteNonce(nonce, counter);

        CryptoPrimitives.Seal(_sendKey, nonce, payload, frame.AsSpan(0, HeaderLength))
            .CopyTo(frame.AsSpan(HeaderLength));

        return frame;
    }

    public bool TryOpen(
        ReadOnlySpan<byte> frame, out byte kind, out byte channel, out byte[] payload, out string? rejection)
    {
        kind = 0;
        channel = 0;
        payload = [];

        if (frame.Length < HeaderLength + CryptoPrimitives.TagLength)
        {
            rejection = $"trame trop courte ({frame.Length} octets)";
            return false;
        }

        kind = frame[0];
        channel = frame[1];
        var counter = BinaryPrimitives.ReadUInt64BigEndian(frame[2..]);

        if (channel >= MaxChannels)
        {
            rejection = $"canal hors bornes ({channel})";
            return false;
        }

        // Le canal annoncé dans l'en-tête doit être celui que porte le compteur,
        // sinon deux en-têtes différents désigneraient le même nonce.
        if ((byte)(counter >> 56) != channel)
        {
            rejection = "canal incohérent avec le compteur";
            return false;
        }

        if (counter <= _received[channel])
        {
            rejection = $"rejeu ou déclassement sur le canal {channel}";
            return false;
        }

        Span<byte> nonce = stackalloc byte[CryptoPrimitives.NonceLength];
        WriteNonce(nonce, counter);

        if (CryptoPrimitives.TryOpen(
                _receiveKey, nonce, frame[HeaderLength..], frame[..HeaderLength], out payload) is false)
        {
            rejection = "trame illisible : étiquette invalide, ou clé de session différente";
            return false;
        }

        // Le compteur n'avance qu'une fois la trame authentifiée : sans cela, un
        // paquet forgé au compteur élevé ferait rejeter les trames légitimes qui
        // suivent.
        _received[channel] = counter;
        rejection = null;
        return true;
    }

    /// <summary>Le nonce d'une trame, exposé pour que les tests vérifient qu'il ne se répète jamais.</summary>
    public static byte[] NonceOf(ReadOnlySpan<byte> frame)
        => frame.Slice(2, 8).ToArray();

    private void WriteNonce(Span<byte> nonce, ulong counter)
    {
        _noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[NoncePrefixLength..], counter);
    }
}
