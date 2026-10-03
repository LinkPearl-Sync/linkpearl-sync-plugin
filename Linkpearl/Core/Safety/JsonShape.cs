using System.Text;
using System.Text.Json;

namespace Linkpearl.Core.Safety;

/// <summary>Vérifie qu'une chaîne venue d'un pair est un objet JSON de profondeur bornée.</summary>
/// <remarks>
/// La profondeur est bornée par le lecteur lui-même : un JSON imbriqué sur
/// des milliers de niveaux épuiserait la pile d'un parseur récursif, chez
/// nous ou chez le plugin à qui on le passe.
/// </remarks>
public static class JsonShape
{
    public static bool IsObject(string json, int maxDepth)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = maxDepth });

            if (reader.Read() is false || reader.TokenType is not JsonTokenType.StartObject)
                return false;

            reader.Skip();
            return reader.Read() is false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Vrai si aucun objet, à aucune profondeur, ne porte deux fois la même clé
    /// à la casse près.
    /// </summary>
    /// <remarks>
    /// SimpleHeels et Honorific lisent leur JSON avec Newtonsoft, qui associe
    /// une clé à un champ sans tenir compte de la casse et garde la dernière
    /// valeur vue. Avec <c>{"Title":"court","title":"…"}</c>, nos contrôles
    /// liraient la première et le plugin poserait la seconde. Interdire les
    /// doublons rend la lecture des deux côtés identique, et un émetteur
    /// honnête n'en produit jamais : c'est Newtonsoft qui les a sérialisés.
    /// </remarks>
    public static bool HasDistinctKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in element.EnumerateObject())
                {
                    if (seen.Add(property.Name) is false || HasDistinctKeys(property.Value) is false)
                        return false;
                }

                return true;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasDistinctKeys(item) is false)
                        return false;
                }

                return true;

            default:
                return true;
        }
    }

    /// <summary>La propriété de ce nom, à la casse près, comme Newtonsoft la trouverait.</summary>
    /// <remarks>À n'appeler qu'après <see cref="HasDistinctKeys"/> : il n'y en a alors qu'une.</remarks>
    public static bool TryGetPropertyIgnoringCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
