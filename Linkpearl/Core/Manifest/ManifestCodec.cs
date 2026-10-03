using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Manifest;

/// <summary>
/// Encodage canonique et compression du manifeste.
/// </summary>
/// <remarks>
/// L'encodage est écrit à la main avec <see cref="Utf8JsonWriter"/> plutôt que
/// sérialisé par réflexion : l'ordre des propriétés fait partie du contrat,
/// puisque c'est lui qui rend le hash du manifeste reproductible d'une machine
/// à l'autre. La lecture passe par <see cref="JsonDocument"/>, qui n'utilise pas
/// non plus la réflexion et n'instancie rien que nous n'ayons validé.
///
/// Noms de champs courts : le manifeste d'un personnage lourd porte plusieurs
/// centaines d'entrées, et ces octets se paient à chaque rencontre.
/// </remarks>
public static class ManifestCodec
{
    public static byte[] Encode(CharacterManifest manifest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", manifest.Version);

            writer.WriteStartArray("r");
            foreach (var replacement in manifest.Replacements)
            {
                writer.WriteStartObject();
                writer.WriteString("h", replacement.Hash.ToHex());
                writer.WriteNumber("s", replacement.Size);
                writer.WriteStartArray("p");
                foreach (var path in replacement.GamePaths)
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteString("m", manifest.MetaManipulations);

            if (manifest.GlamourerState is null)
                writer.WriteNull("g");
            else
                writer.WriteString("g", manifest.GlamourerState);

            // Une clé par extra présent, et aucune pour les absents : des extras
            // vides et des extras absents donnent le même encodage, donc la
            // même empreinte, et ne font pas réannoncer pour rien.
            var extras = manifest.ExtrasOrNone;
            WriteExtra(writer, "xc", extras.CustomizePlus);
            WriteExtra(writer, "xh", extras.Heels);
            WriteExtra(writer, "xt", extras.Honorific);
            WriteExtra(writer, "xm", extras.Moodles);
            WriteExtra(writer, "xp", extras.PetNicknames);

            // Absente quand il n'y a aucun échange, pour la même raison que les
            // extras : un manifeste d'avant garde son empreinte.
            if (manifest.SwapsOrNone.Count > 0)
            {
                writer.WriteStartArray("w");
                foreach (var swap in manifest.SwapsOrNone)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(swap.GamePath);
                    writer.WriteStringValue(swap.TargetGamePath);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteExtra(Utf8JsonWriter writer, string key, string? value)
    {
        if (value is not null)
            writer.WriteString(key, value);
    }

    private static string? ReadExtra(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var element) is false)
            return null;

        return element.ValueKind is JsonValueKind.String
            ? element.GetString()
            : throw new JsonException($"extra {key} non textuel");
    }

    private static bool TryReadSwaps(JsonElement root, Quotas quotas, out List<FileSwap>? swaps, out string? rejection)
    {
        swaps = null;
        rejection = null;

        if (root.TryGetProperty("w", out var element) is false)
            return true;

        if (element.ValueKind is not JsonValueKind.Array)
        {
            rejection = "manifeste : liste d'échanges malformée";
            return false;
        }

        if (element.GetArrayLength() > quotas.MaxGamePaths)
        {
            rejection = $"manifeste : plafond de chemins de jeu dépassé (plafond {quotas.MaxGamePaths})";
            return false;
        }

        swaps = new List<FileSwap>(element.GetArrayLength());

        foreach (var pair in element.EnumerateArray())
        {
            if (pair.ValueKind is not JsonValueKind.Array
                || pair.GetArrayLength() != 2
                || pair[0].ValueKind is not JsonValueKind.String
                || pair[1].ValueKind is not JsonValueKind.String)
            {
                rejection = "manifeste : échange malformé";
                return false;
            }

            swaps.Add(new FileSwap(pair[0].GetString()!, pair[1].GetString()!));
        }

        return true;
    }

    /// <summary>
    /// Identité du manifeste. C'est elle qu'on compare pour savoir si quoi que
    /// ce soit a changé, et elle seule qu'on annonce à un pair.
    /// </summary>
    public static BlobHash HashOf(CharacterManifest manifest)
        => BlobHash.OfContent(Encode(manifest));

    public static byte[] Compress(CharacterManifest manifest)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(Encode(manifest));

        return output.ToArray();
    }

    public static bool TryDecompress(
        ReadOnlySpan<byte> compressed, Quotas quotas, out CharacterManifest? manifest, out string? rejection)
    {
        manifest = null;

        if (compressed.Length > quotas.MaxManifestCompressedBytes)
        {
            rejection = $"manifeste compressé trop gros ({compressed.Length} octets, plafond {quotas.MaxManifestCompressedBytes})";
            return false;
        }

        byte[]? plain = null;

        try
        {
            if (TryInflate(compressed, quotas.MaxManifestDecompressedBytes, out plain, out var length, out rejection) is false)
                return false;

            return TryParse(plain.AsMemory(0, length), quotas, out manifest, out rejection);
        }
        finally
        {
            if (plain is not null)
                ArrayPool<byte>.Shared.Return(plain);
        }
    }

    /// <summary>Premier tampon de détente : un manifeste courant y tient sans le faire grandir.</summary>
    private const int FirstInflateBuffer = 256 * 1024;

    /// <summary>
    /// Détend le flux en s'arrêtant au plafond, sans jamais faire confiance à la
    /// taille annoncée par l'émetteur.
    /// </summary>
    /// <param name="plain">Tampon emprunté à l'<see cref="ArrayPool{T}"/>, à rendre par l'appelant.</param>
    /// <remarks>
    /// Le décodeur travaille sur des spans : ni copie de l'entrée, ni
    /// MemoryStream qui double sa capacité jusqu'au plafond. Le tampon de
    /// sortie vient du pool et ne grandit qu'en doublant, jusqu'au plafond :
    /// un manifeste de plusieurs Mo ne laisse pas un tableau neuf sur le LOH à
    /// chaque réception.
    /// </remarks>
    private static bool TryInflate(
        ReadOnlySpan<byte> compressed, int cap, out byte[]? plain, out int length, out string? rejection)
    {
        using var decoder = new BrotliDecoder();

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(cap, FirstInflateBuffer));
        var written = 0;
        var handedOver = false;

        try
        {
            while (true)
            {
                // Jamais au-delà du plafond, même si le pool rend plus grand.
                var room = buffer.AsSpan(written, Math.Min(buffer.Length, cap) - written);
                var status = decoder.Decompress(compressed, room, out var consumed, out var produced);

                compressed = compressed[consumed..];
                written += produced;

                switch (status)
                {
                    case OperationStatus.Done:
                        plain = buffer;
                        length = written;
                        rejection = null;
                        handedOver = true;
                        return true;

                    case OperationStatus.DestinationTooSmall when written >= cap:
                        rejection = $"plafond de décompression dépassé ({cap} octets)";
                        break;

                    case OperationStatus.DestinationTooSmall:
                        var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, cap));
                        buffer.AsSpan(0, written).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                        continue;

                    default:
                        // Données invalides ou flux tronqué. L'entrée vient d'un
                        // pair : toute défaillance du décodeur devient un refus,
                        // jamais une exception qui remonterait dans la boucle de
                        // synchronisation.
                        rejection = "flux compressé illisible";
                        break;
                }

                plain = null;
                length = 0;
                return false;
            }
        }
        finally
        {
            if (handedOver is false)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParse(ReadOnlyMemory<byte> plain, Quotas quotas, out CharacterManifest? manifest, out string? rejection)
    {
        manifest = null;

        try
        {
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                rejection = "manifeste : racine non objet";
                return false;
            }

            if (root.TryGetProperty("v", out var versionElement) is false
                || versionElement.TryGetUInt16(out var version) is false)
            {
                rejection = "manifeste : version absente ou illisible";
                return false;
            }

            root.TryGetProperty("r", out var replacementsElement);

            if (replacementsElement.ValueKind is not JsonValueKind.Array)
            {
                rejection = "manifeste : liste de remplacements absente";
                return false;
            }

            if (replacementsElement.GetArrayLength() > quotas.MaxReplacements)
            {
                rejection = $"manifeste : plafond de remplacements dépassé (plafond {quotas.MaxReplacements})";
                return false;
            }

            var replacements = new List<FileReplacement>(replacementsElement.GetArrayLength());
            var totalPaths = 0;

            foreach (var element in replacementsElement.EnumerateArray())
            {
                if (element.ValueKind is not JsonValueKind.Object
                    || element.TryGetProperty("h", out var hashElement) is false
                    || hashElement.ValueKind is not JsonValueKind.String
                    || BlobHash.TryParseHex(hashElement.GetString(), out var hash) is false)
                {
                    rejection = "manifeste : empreinte absente ou malformée";
                    return false;
                }

                if (element.TryGetProperty("s", out var sizeElement) is false
                    || sizeElement.TryGetInt64(out var size) is false
                    || size < 0)
                {
                    rejection = "manifeste : taille absente ou négative";
                    return false;
                }

                if (element.TryGetProperty("p", out var pathsElement) is false
                    || pathsElement.ValueKind is not JsonValueKind.Array
                    || pathsElement.GetArrayLength() == 0)
                {
                    rejection = "manifeste : entrée sans chemin de jeu";
                    return false;
                }

                totalPaths += pathsElement.GetArrayLength();
                if (totalPaths > quotas.MaxGamePaths)
                {
                    rejection = $"manifeste : plafond de chemins de jeu dépassé (plafond {quotas.MaxGamePaths})";
                    return false;
                }

                var paths = new List<string>(pathsElement.GetArrayLength());
                foreach (var pathElement in pathsElement.EnumerateArray())
                {
                    if (pathElement.ValueKind is not JsonValueKind.String)
                    {
                        rejection = "manifeste : chemin de jeu non textuel";
                        return false;
                    }

                    paths.Add(pathElement.GetString()!);
                }

                replacements.Add(new FileReplacement(paths, hash, size));
            }

            if (root.TryGetProperty("m", out var metaElement) is false
                || metaElement.ValueKind is not JsonValueKind.String)
            {
                rejection = "manifeste : manipulations méta absentes";
                return false;
            }

            string? glamourer = null;
            if (root.TryGetProperty("g", out var glamourerElement))
            {
                glamourer = glamourerElement.ValueKind switch
                {
                    JsonValueKind.String => glamourerElement.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new JsonException("état Glamourer non textuel"),
                };
            }

            var extras = new CharacterExtras(
                ReadExtra(root, "xc"), ReadExtra(root, "xh"), ReadExtra(root, "xt"),
                ReadExtra(root, "xm"), ReadExtra(root, "xp"));

            if (TryReadSwaps(root, quotas, out var swaps, out rejection) is false)
                return false;

            manifest = new CharacterManifest(
                version, replacements, metaElement.GetString()!, glamourer,
                extras.IsEmpty ? null : extras, swaps);
            rejection = null;
            return true;
        }
        catch (JsonException e)
        {
            rejection = $"manifeste illisible : {e.Message}";
            return false;
        }
    }
}
