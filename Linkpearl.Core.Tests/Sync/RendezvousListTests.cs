using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// La migration d'un réglage unique vers une liste. Elle vit dans le noyau et
/// non dans Configuration, qui dépend de Dalamud et ne se testerait pas sous
/// Linux.
/// </summary>
public class RendezvousListTests
{
    [Fact]
    public void Un_reglage_d_avant_la_federation_devient_une_liste_d_une_entree()
    {
        var list = RendezvousList.Migrate("83.228.242.221", 47900, null);

        var entry = Assert.Single(list);
        Assert.Equal("83.228.242.221", entry.Address.Host);
        Assert.Equal(47900, entry.Address.Port);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void Un_port_non_standard_est_conserve()
    {
        var entry = Assert.Single(RendezvousList.Migrate("rdv.exemple.ch", 443, null));

        Assert.Equal(443, entry.Address.Port);
    }

    [Fact]
    public void Une_liste_deja_migree_n_est_pas_retouchee()
    {
        // La migration ne doit jamais écraser un choix fait depuis.
        var current = new[] { new RendezvousEntry(new RendezvousAddress("rdv.ami.ch", 443), "Amie", true) };

        Assert.Same(current, RendezvousList.Migrate("83.228.242.221", 47900, current));
    }

    [Fact]
    public void Un_reglage_vide_ne_fabrique_pas_d_entree()
    {
        // Mieux vaut une liste vide, que l'interface signale, qu'une entrée
        // pointant nulle part dont l'utilisateur ne comprendrait pas l'échec.
        Assert.Empty(RendezvousList.Migrate("", 47900, null));
        Assert.Empty(RendezvousList.Migrate(null, 47900, []));
    }

    [Fact]
    public void Un_reglage_illisible_ne_fabrique_pas_d_entree()
    {
        Assert.Empty(RendezvousList.Migrate("rdv exemple.ch", 47900, null));
    }
    [Fact]
    public void L_ancienne_ip_du_service_par_defaut_devient_son_nom()
    {
        var current = new[]
        {
            new RendezvousEntry(new RendezvousAddress(RendezvousList.RetiredDefaultHost, 47900), "", false),
            new RendezvousEntry(new RendezvousAddress("rdv.ami.ch", 443), "Amie", true),
        };

        var renamed = RendezvousList.RenameRetiredDefault(current);

        // Seul le nom change : l'interrupteur et les autres entrées restent.
        Assert.Equal(new RendezvousAddress(RendezvousList.DefaultHost, 47900), renamed[0].Address);
        Assert.False(renamed[0].Enabled);
        Assert.Equal(current[1], renamed[1]);
    }

    [Fact]
    public void Une_liste_sans_l_ancienne_ip_n_est_pas_retouchee()
    {
        var current = new[] { new RendezvousEntry(new RendezvousAddress("rdv.ami.ch", 443), "Amie", true) };

        Assert.Same(current, RendezvousList.RenameRetiredDefault(current));
    }

    private static RendezvousEntry Entry(string host, int port = 47900, string label = "", bool enabled = true)
        => new(new RendezvousAddress(host, port), label, enabled);

    [Fact]
    public void La_liste_d_origine_recoit_le_service_de_secours()
    {
        var current = new[] { Entry(RendezvousList.DefaultHost) };

        var completed = RendezvousList.AddBackupDefault(current);

        Assert.Equal([current[0], Entry(RendezvousList.BackupHost)], completed);
    }

    [Fact]
    public void Une_liste_composee_a_la_main_ne_recoit_rien()
    {
        // Le secours ne s'ajoute qu'à la liste que le plugin a posée lui-même :
        // un choix fait depuis, même proche, n'est pas le nôtre à compléter.
        IReadOnlyList<RendezvousEntry>[] composed =
        [
            [],
            [Entry(RendezvousList.DefaultHost, enabled: false)],
            [Entry(RendezvousList.DefaultHost, label: "Principal")],
            [Entry(RendezvousList.DefaultHost, port: 443)],
            [Entry("rdv.ami.ch", 443)],
            [Entry(RendezvousList.DefaultHost), Entry("rdv.ami.ch", 443)],
        ];

        foreach (var current in composed)
            Assert.Same(current, RendezvousList.AddBackupDefault(current));
    }

    [Fact]
    public void Une_installation_neuve_recoit_les_deux_services_nommes()
    {
        var fresh = RendezvousList.NameDefaults(RendezvousList.AddBackupDefault(
            RendezvousList.Migrate(RendezvousList.DefaultHost, 47900, null)));

        Assert.Equal(
            [
                Entry(RendezvousList.DefaultHost, label: RendezvousList.DefaultLabel),
                Entry(RendezvousList.BackupHost, label: RendezvousList.BackupLabel),
            ],
            fresh);
    }

    [Fact]
    public void Les_services_par_defaut_sans_libelle_sont_nommes_dans_toute_liste()
    {
        var current = new[]
        {
            Entry("rdv.ami.ch", 443),
            Entry(RendezvousList.BackupHost, enabled: false),
            Entry(RendezvousList.DefaultHost),
        };

        var named = RendezvousList.NameDefaults(current);

        // L'ordre, l'interrupteur et les autres services ne bougent pas.
        Assert.Equal(
            [
                current[0],
                Entry(RendezvousList.BackupHost, label: RendezvousList.BackupLabel, enabled: false),
                Entry(RendezvousList.DefaultHost, label: RendezvousList.DefaultLabel),
            ],
            named);
    }

    [Fact]
    public void Un_libelle_saisi_n_est_pas_remplace()
    {
        IReadOnlyList<RendezvousEntry>[] untouched =
        [
            [Entry(RendezvousList.DefaultHost, label: "Principal")],
            [Entry(RendezvousList.DefaultHost, port: 443)],
            [Entry("rdv.ami.ch", 443)],
        ];

        foreach (var current in untouched)
            Assert.Same(current, RendezvousList.NameDefaults(current));
    }
}
