using System.Reflection;
using Xunit;

namespace Linkpearl.Core.Tests;

/// <summary>Protège les textes qui donnent au joueur le modèle mental de Linkpearl.</summary>
public class UiCopyTests
{
    private static string Source(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.Contains(".UiSources.", StringComparison.Ordinal)
                             && resource.EndsWith(suffix, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void La_presentation_definit_un_pair_et_explique_le_relais()
    {
        var source = Source("Onboarding.OnboardingWindow.cs");

        Assert.Contains("Un pair est un joueur ajouté à vos contacts Linkpearl", source);
        Assert.Contains("un service Linkpearl les relaie sans pouvoir les lire", source);
        Assert.DoesNotContain("sert uniquement à trouver", source);
    }

    [Fact]
    public void Les_demandes_expliquent_quand_accepter()
    {
        var source = Source("Pages.RequestsPage.cs");

        Assert.Contains("N'accepter que si vous reconnaissez ce personnage", source);
        Assert.Contains("ne peut pas confirmer", source);
    }

    [Fact]
    public void Les_etats_de_pair_decrivent_un_resultat_visible()
    {
        var source = Source("Pages.PairsPage.cs");

        Assert.Contains("apparence appliquée", source);
        Assert.Contains("prêt quand le joueur sera visible", source);
        Assert.Contains("mis en pause par ce joueur", source);
        Assert.DoesNotContain("apparence posée", source);
        Assert.DoesNotContain("pause du pair", source);
    }

    [Fact]
    public void Le_mode_public_annonce_les_joueurs_inconnus()
    {
        var source = Source("Pages.GroupsPage.cs");

        Assert.Contains("Mode public", source);
        Assert.Contains("même si vous ne les connaissez pas", source);
        Assert.DoesNotContain("Activer Public", source);
    }

    [Fact]
    public void Le_cache_nomme_sa_taille_maximale()
        => Assert.Contains("Taille maximale du cache", Source("CacheChooser.cs"));

    [Fact]
    public void La_barre_decrit_la_disponibilite_du_service()
    {
        var source = Source("Shell.StatusBar.cs");

        Assert.Contains("Service disponible", source);
        Assert.Contains("Service indisponible", source);
        Assert.DoesNotContain("? \"En ligne\" : \"Hors ligne\"", source);
    }
}
