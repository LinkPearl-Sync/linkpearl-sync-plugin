using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;

namespace Linkpearl.Core.Safety;

/// <summary>
/// Contrôle une chaîne « base64 d'un flux gzip » sans faire confiance à ce
/// qu'elle annonce.
/// </summary>
/// <remarks>
/// C'est la forme des deux chaînes opaques d'un manifeste. Penumbra produit ses
/// manipulations méta par <c>GetPlayerMetaManipulations</c> : un octet de
/// version puis les manipulations, le tout passé dans un <c>GZipStream</c> et
/// encodé en base64 (<c>MetaApi.CompressMetaManipulations</c>, version 0 en
/// JSON et 1 en binaire). Glamourer produit son état par
/// <c>GetStateBase64</c> de la même façon (<c>DesignConverter.ShareBase64</c>,
/// via <c>ToCompressedBase64</c>). Les deux plugins les détendent en entier,
/// sans plafond, sur le thread du jeu : 512 Kio de base64, soit 384 Kio de
/// gzip, peuvent cacher 400 Mo. On détend donc ici d'abord, par blocs
/// empruntés, et l'on s'arrête au plafond.
///
/// Rien n'oblige un client honnête à produire autre chose que du gzip : nos
/// émetteurs ne font que relayer ces deux appels. Tout autre format est
/// refusé, même si l'un des plugins saurait le lire.
/// </remarks>
public static class GzipBase64
{
    private const int Block = 64 * 1024;

    /// <summary>Vrai si la chaîne est du base64 d'un flux gzip complet qui se détend sous le plafond.</summary>
    /// <param name="value">Vide accepté : c'est « rien à poser », pas un flux.</param>
    public static bool IsBounded(string value, int maxDecompressedBytes, out string? rejection)
    {
        rejection = null;

        if (value.Length == 0)
            return true;

        // Sans tableau : un refus ne coûte rien de plus que la lecture.
        if (Base64.IsValid(value, out var decodedLength) is false)
        {
            rejection = "base64 invalide";
            return false;
        }

        var compressed = ArrayPool<byte>.Shared.Rent(Math.Max(decodedLength, 1));
        var buffer = ArrayPool<byte>.Shared.Rent(Block);

        try
        {
            if (Convert.TryFromBase64String(value, compressed, out var written) is false)
            {
                rejection = "base64 invalide";
                return false;
            }

            // L'en-tête gzip : deux octets magiques, puis la méthode deflate.
            if (written < 3 || compressed[0] != 0x1F || compressed[1] != 0x8B || compressed[2] != 0x08)
            {
                rejection = "format inattendu (gzip attendu)";
                return false;
            }

            using var input = new MemoryStream(compressed, 0, written, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);

            var total = 0L;
            int read;

            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;

                if (total > maxDecompressedBytes)
                {
                    rejection = $"se détend au-delà de {maxDecompressedBytes} octets";
                    return false;
                }
            }

            // Ni l'octet de version des deux plugins, ni rien après : un flux
            // vide n'est pas ce qu'ils produisent.
            if (total == 0)
            {
                rejection = "flux gzip vide";
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException)
        {
            // L'entrée vient d'un pair : un flux corrompu ou tronqué est un
            // refus, jamais une exception qui remonterait dans la session.
            rejection = "flux gzip illisible";
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }
}
