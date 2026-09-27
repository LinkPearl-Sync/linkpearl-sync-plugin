using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

/// <summary>
/// Le fil de rendez-vous, figé octet par octet.
/// </summary>
/// <remarks>
/// Le plugin et le serveur vivent dans deux dépôts, et chacun porte sa copie
/// des sources du format. Deux copies divergent toujours un jour, et la panne
/// qui s'ensuit est de celles qui coûtent une soirée : deux joueurs qui ne se
/// voient plus, sans message d'erreur qui dise pourquoi.
///
/// Ce fichier de vecteurs est identique dans les deux dépôts, et ce test est
/// identique lui aussi. Le premier côté qui touche à un octet du format casse
/// son propre test, avant d'avoir livré quoi que ce soit.
///
/// Ce qu'il atteste et rien de plus : la cohérence de format entre les deux
/// copies. Les vecteurs ont été produits par l'implémentation qu'ils testent,
/// donc ils n'attrapent pas une erreur de conception, seulement une dérive.
/// </remarks>
public class RendezvousVectorTests
{
    private static byte[] Repeat(byte seed, int length) => Enumerable.Repeat(seed, length).ToArray();

    private static byte[] TicketA => Repeat(0x11, RendezvousTicket.SizeInBytes);

    private static byte[] TicketB => Repeat(0x22, RendezvousTicket.SizeInBytes);

    private static byte[] Candidates => [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];

    private static byte[] AddressA => Repeat(0xA1, RendezvousWire.MailboxAddressSize);

    private static byte[] AddressB => Repeat(0xB2, RendezvousWire.MailboxAddressSize);

    private static byte[] Payload => Encoding.UTF8.GetBytes("charge utile scellee");

    private static readonly (string Name, Func<byte[]> Build)[] Frames =
    [
        ("annonce", () => RendezvousWire.Announce(new Announcement([TicketA, TicketB], Candidates))),
        ("apparie", () => RendezvousWire.Matched(Candidates)),
        ("relais-ouverture", () => RendezvousWire.RelayOpen(TicketA)),
        ("relais-pret", () => RendezvousWire.Simple(RendezvousKind.RelayReady)),
        ("relais-donnees", () => RendezvousWire.RelayData(Payload)),
        ("erreur", () => RendezvousWire.Error("jeton inconnu")),
        ("ticket-enregistrement", () => RendezvousWire.TicketRegister(TicketA, Payload)),
        ("ticket-retrait", () => RendezvousWire.TicketRedeem(TicketA)),
        ("ticket-charge", () => RendezvousWire.TicketPayload(Payload)),
        ("boite-ouverture", () => RendezvousWire.MailboxOpen([AddressA, AddressB])),
        ("boite-interrogation", () => RendezvousWire.MailboxQuery([AddressA, AddressB])),
        ("boite-presence", () => RendezvousWire.MailboxPresence([true, false, true])),
        ("boite-depot", () => RendezvousWire.MailboxDeposit(AddressA, Payload)),
        ("boite-remise", () => RendezvousWire.MailboxDelivery(Payload)),
        ("annuaire-demande", () => RendezvousWire.Simple(RendezvousKind.DirectoryQuery)),
        ("annuaire", () => RendezvousWire.Directory(
            [new DirectoryEntry("rdv.ami.ch", "Chez l'amie"),
             new DirectoryEntry("rdv.exemple.ch:443", "Service commun")])),
        ("annuaire-candidature", () => RendezvousWire.DirectorySubmit("rdv.nouveau.ch", "Chez le nouveau")),
        ("bannissement-demande", () => RendezvousWire.BanListQuery(1)),
        ("bannissement-page", () => RendezvousWire.BanListData(0, 2, "{\"version\":1}")),
        ("consensus-demande", () => RendezvousWire.ConsensusQuery(1)),
        ("consensus-page", () => RendezvousWire.ConsensusPage(0, 2, new byte[] { 0xAB, 0xCD })),
        ("consensus-document", () => ServiceConsensus.Assemble(
            new ServiceConsensus(7, 1_790_000_000, 1_790_604_800,
                [new ConsensusEntry("rdv.ami.ch:47900", "Ami", Repeat(0x0F, 8))]),
            [new ConsensusSignature(Repeat(0x5A, 8), Repeat(0x5B, 64))])),
        ("etat-reseau-demande", () => RendezvousWire.NetworkStatusQuery(1)),
        ("etat-reseau-page", () => RendezvousWire.NetworkStatusPage(0, 2, new byte[] { 0xAB, 0xCD })),
        ("consensus-v2-demande", () => RendezvousWire.ConsensusV2Query(1)),
        ("consensus-v2-page", () => RendezvousWire.ConsensusV2Page(0, 2, new byte[] { 0xAB, 0xCD })),
        ("consensus-v2-document", () => ServiceConsensus.AssembleV2(
            new ServiceConsensus(7, 1_790_000_000, 1_790_604_800,
                [new ConsensusEntry("rdv.ami.ch:47900", "Ami", Repeat(0x0F, 8), "EU"),
                 new ConsensusEntry("rdv.loin.ch:47900", "Loin", Repeat(0x1E, 8))]),
            [new ConsensusSignature(Repeat(0x5A, 8), Repeat(0x5B, 64))])),
    ];

    /// <summary>Les vecteurs, retrouvés dans les sources du dépôt.</summary>
    /// <remarks>
    /// Le fichier ne vit pas au même endroit dans les deux dépôts, et il n'est
    /// pas copié à côté de l'assembly. Partir du chemin de ce fichier-ci,
    /// remonter jusqu'à la racine, puis chercher par nom : c'est ce qui permet
    /// à ce test de rester identique des deux côtés, ce qui est précisément ce
    /// qu'il sert à garantir.
    /// </remarks>
    private static JsonElement Document([CallerFilePath] string here = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(here)!);

        while (directory is not null && directory.EnumerateFiles("*.slnx").Any() is false)
            directory = directory.Parent;

        Assert.True(directory is not null, $"racine du dépôt introuvable depuis {here}");

        var found = directory!
            .EnumerateFiles("rendezvous-vectors.json", SearchOption.AllDirectories)
            .Where(file => file.FullName.Contains("/bin/", StringComparison.Ordinal) is false)
            .Where(file => file.FullName.Contains("/obj/", StringComparison.Ordinal) is false)
            .ToList();

        Assert.True(found.Count == 1, $"un seul fichier de vecteurs attendu, {found.Count} trouvé(s)");

        return JsonDocument.Parse(File.ReadAllText(found[0].FullName)).RootElement.Clone();
    }

    [Fact]
    public void Le_fichier_de_vecteurs_les_porte_tous()
    {
        // Garde-fou du garde-fou : un fichier tronqué ferait passer au vert un
        // test qui ne vérifierait plus rien.
        var names = Document().GetProperty("trames").EnumerateArray()
            .Select(entry => entry.GetProperty("nom").GetString())
            .ToList();

        Assert.Equal(Frames.Select(frame => frame.Name), names);
    }

    [Fact]
    public void Chaque_trame_se_reproduit_a_l_octet_pres()
    {
        var expected = Document().GetProperty("trames").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("nom").GetString()!, entry => entry.GetProperty("hex").GetString()!);

        foreach (var (name, build) in Frames)
            Assert.Equal(expected[name], Convert.ToHexStringLower(build()));
    }

    [Theory]
    [InlineData("tailleTicket", RendezvousTicket.SizeInBytes)]
    [InlineData("tailleAdresseBoite", RendezvousWire.MailboxAddressSize)]
    [InlineData("tailleTicketInvitation", RendezvousWire.InvitationTicketSize)]
    [InlineData("longueurTrameMax", RendezvousWire.MaxFrameLength)]
    [InlineData("ticketsParAnnonceMax", RendezvousWire.MaxTicketsPerAnnouncement)]
    [InlineData("candidatsScellesMax", RendezvousWire.MaxSealedCandidatesLength)]
    [InlineData("adressesInterrogeesMax", RendezvousWire.MaxQueriedAddresses)]
    [InlineData("depotMax", RendezvousWire.MaxDepositLength)]
    [InlineData("chargeTicketMax", RendezvousWire.MaxTicketPayloadLength)]
    [InlineData("entreesAnnuaireMax", RendezvousWire.MaxDirectoryEntries)]
    [InlineData("libelleAnnuaireMax", RendezvousWire.MaxDirectoryLabelLength)]
    [InlineData("adresseAnnuaireMax", RendezvousWire.MaxDirectoryAddressLength)]
    [InlineData("entreesParPageBannissement", RendezvousWire.BanListPageEntries)]
    [InlineData("pagesBannissementMax", RendezvousWire.MaxBanListPages)]
    [InlineData("octetsParPageConsensus", RendezvousWire.ConsensusPageBytes)]
    [InlineData("pagesConsensusMax", RendezvousWire.MaxConsensusPages)]
    [InlineData("entreesConsensusMax", ServiceConsensus.MaxEntries)]
    [InlineData("tailleRegion", ServiceConsensus.RegionSize)]
    public void Les_plafonds_sont_les_memes_des_deux_cotes(string name, int expected)
    {
        // Un plafond qui diverge ne casse pas le format : il fait refuser chez
        // l'un ce que l'autre accepte, ce qui est plus difficile à diagnostiquer
        // qu'une trame illisible.
        Assert.Equal(expected, Document().GetProperty("constantes").GetProperty(name).GetInt32());
    }

    [Fact]
    public void La_fenetre_du_jeton_est_la_meme_des_deux_cotes()
    {
        Assert.Equal(
            (int)RendezvousTicket.Window.TotalMinutes,
            Document().GetProperty("constantes").GetProperty("fenetreMinutes").GetInt32());
    }
}
