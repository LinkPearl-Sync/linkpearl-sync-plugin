using System.Globalization;
using System.Text;

namespace Linkpearl.Ui;

/// <summary>
/// Rend affichable un texte saisi par un joueur.
/// </summary>
/// <remarks>
/// Les noms donnés aux pairs sont libres, et les joueurs emploient volontiers
/// des caractères décoratifs qu'aucune police de texte ne couvre : pleine
/// chasse, alphabet mathématique, émojis. Sans traitement, ils s'affichent en
/// points d'interrogation, et l'utilisateur croit à un bug alors qu'il a lui-même
/// tapé le nom.
///
/// La normalisation de compatibilité Unicode (NFKC) ramène les deux premières
/// familles à leur équivalent latin. Les émojis, eux, sont retirés : il n'existe
/// pas de repli raisonnable pour eux dans une police vectorielle monochrome.
/// </remarks>
internal static class Glyphs
{
    private static readonly Dictionary<string, string> Cache = [];

    /// <summary>Au-delà, le cache est vidé : il n'évite qu'un recalcul par image.</summary>
    private const int CacheLimit = 256;

    /// <summary>
    /// Symboles absents d'Inter mais assez courants pour mériter un équivalent
    /// plutôt qu'une suppression.
    /// </summary>
    /// <remarks>
    /// Les joueurs substituent volontiers une lettre par un symbole qui lui
    /// ressemble. « ＦＡＣＴ⚙ＲＹ » attend un O, pas un trou.
    /// </remarks>
    private static readonly Dictionary<char, string> Substitutes = new()
    {
        ['⚙'] = "O",
        ['☼'] = "O",
        ['✿'] = "o",
        ['❀'] = "o",
        ['★'] = "*",
        ['☆'] = "*",
        ['✦'] = "◆",
        ['✧'] = "◇",
        ['➡'] = "→",
        ['✨'] = "",
        ['☠'] = "",
        ['❤'] = "",
        ['　'] = " ",
    };

    /// <summary>
    /// Rend un texte affichable. Le résultat est mémorisé : la méthode est
    /// appelée à chaque image, pour chaque ligne de liste.
    /// </summary>
    /// <remarks>
    /// Appelée aussi hors du thread du jeu, pour les messages du chat qui
    /// citent un nom venu du réseau : le cache est donc sous verrou, qu'une
    /// écriture concurrente corromprait sans rien lever.
    /// </remarks>
    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var cached))
                return cached;
        }

        var result = Convert(text);

        lock (Gate)
        {
            if (Cache.Count >= CacheLimit)
                Cache.Clear();

            Cache[text] = result;
        }

        return result;
    }

    private static readonly Lock Gate = new();

    /// <summary>
    /// Comme <see cref="Safe"/>, pour un texte posé dans le libellé d'un widget ImGui.
    /// </summary>
    /// <remarks>
    /// ImGui cesse d'afficher un libellé au premier « ## » : un nom venu du
    /// réseau qui en porte un serait tronqué, quel que soit l'identifiant
    /// placé après « ### ». Une espace entre les deux dièses suffit à le
    /// neutraliser sans rendre le nom méconnaissable.
    /// </remarks>
    public static string Label(string? text) => Safe(text).Replace("##", "# #", StringComparison.Ordinal);

    private static string Convert(string text)
    {
        var builder = new StringBuilder(text.Length);

        // NFKC d'abord : il ramène la pleine chasse et les alphabets
        // mathématiques au latin, ce qui règle la majorité des cas d'un coup.
        // Un texte qui ne se normalise pas (substitut orphelin) passe tel quel
        // au tri qui suit, qui écarte justement les substituts.
        string normalized;

        try
        {
            normalized = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            normalized = text;
        }

        foreach (var ch in normalized)
        {
            if (Substitutes.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
                continue;
            }

            if (IsRenderable(ch))
                builder.Append(ch);
        }

        var result = builder.ToString().Trim();

        // Tout retirer donnerait une ligne vide, où l'utilisateur ne
        // reconnaîtrait plus l'entrée qu'il a créée. Mieux vaut le texte brut,
        // mais jamais ses caractères de contrôle ou de mise en forme : un nom
        // venu du réseau n'en garde aucun, même dans ce repli.
        return result.Length == 0 ? new string([.. text.Where(IsInert)]).Trim() : result;
    }

    /// <summary>
    /// Faux pour un caractère de contrôle (Cc) ou de mise en forme (Cf).
    /// </summary>
    /// <remarks>
    /// Les premiers coupent une ligne ou ouvrent une séquence d'échappement du
    /// chat, les seconds sont invisibles ou retournent l'affichage (U+202E) :
    /// un nom qui en porte peut se faire passer pour un autre à l'écran.
    /// </remarks>
    private static bool IsInert(char ch)
        => char.IsControl(ch) is false
        && CharUnicodeInfo.GetUnicodeCategory(ch) is not UnicodeCategory.Format;

    /// <summary>
    /// Vrai pour ce qu'Inter et FontAwesome savent dessiner.
    /// </summary>
    /// <remarks>
    /// Les substituts de paire (U+D800 à U+DFFF) sont écartés : ils codent les
    /// émojis et les alphabets décoratifs hors du plan de base, et la moitié
    /// d'une paire n'a aucun sens isolée.
    /// </remarks>
    private static bool IsRenderable(char ch)
        => char.IsSurrogate(ch) is false
        && IsInert(ch)
        && (char.IsLetterOrDigit(ch)
         || char.IsPunctuation(ch)
         || char.IsSymbol(ch) is false && char.IsWhiteSpace(ch)
         || ch < 0x2000);
}
