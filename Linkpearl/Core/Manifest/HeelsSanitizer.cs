using System.Text.Json;
using System.Text.Json.Nodes;
using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Manifest;

/// <summary>
/// Retire de la configuration SimpleHeels ce qui ne sert pas au décalage et
/// en dit trop.
/// </summary>
/// <remarks>
/// Relevé dans SimpleHeels 162466c (IpcCharacterConfig) : les positions d'emote
/// et de familier sont des coordonnées absolues dans le monde, les étiquettes
/// sont des chaînes libres écrites par d'autres plugins, <c>E</c> révèle que le
/// plugin Echo est installé. Aucun n'est nécessaire au décalage.
/// </remarks>
public static class HeelsSanitizer
{
    public static IReadOnlyList<string> Removed { get; } =
        ["EmotePosition", "MinionPosition", "Tags", "E", "PluginVersion"];

    private const int MaxDepth = 8;

    public static string? Sanitize(string json)
    {
        if (JsonShape.IsObject(json, MaxDepth) is false)
            return null;

        try
        {
            // Insensible à la casse, comme SimpleHeels lit ce JSON et comme le
            // receveur le contrôle : une variante de casse partirait sinon et
            // ferait refuser l'apparence entière chez chaque pair.
            var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };

            if (JsonNode.Parse(json, options) is not JsonObject root)
                return null;

            foreach (var name in Removed)
                root.Remove(name);

            return root.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Une clé en double à la casse près : le receveur la refuserait.
            return null;
        }
    }
}
