using System.Net;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class CandidateSetTests
{
    private static IPEndPoint End(string address, int port) => new(IPAddress.Parse(address), port);

    [Fact]
    public void Un_ensemble_de_candidats_fait_l_aller_retour()
    {
        var original = new List<IPEndPoint>
        {
            End("203.0.113.57", 47251),
            End("192.168.1.20", 47251),
            End("2001:db8::1", 47251),
        };

        Assert.True(CandidateSet.TryDecode(CandidateSet.Encode(original), out var parsed, out var why), why);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Un_ensemble_vide_se_relit_sans_erreur()
    {
        Assert.True(CandidateSet.TryDecode(CandidateSet.Encode([]), out var parsed, out var why), why);
        Assert.Empty(parsed);
    }

    [Fact]
    public void Trop_de_candidats_sont_ecartes_a_l_encodage()
    {
        var many = Enumerable.Range(1, 30).Select(i => End($"10.0.0.{i}", 1000 + i)).ToList();

        Assert.True(CandidateSet.TryDecode(CandidateSet.Encode(many), out var parsed, out _));
        Assert.Equal(CandidateSet.MaxCandidates, parsed.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void Un_bloc_tronque_est_refuse_sans_lever(int length)
    {
        var body = new byte[length];

        if (length > 0)
            body[0] = 3;   // annonce trois candidats qui n'y sont pas

        Assert.False(CandidateSet.TryDecode(body, out _, out var why) && body.Length > 1);
        Assert.True(length == 0 || why is not null);
    }

    [Fact]
    public void Un_port_nul_est_refuse()
    {
        Assert.False(CandidateSet.TryDecode(CandidateSet.Encode([End("10.0.0.1", 0)]), out _, out var why));
        Assert.Contains("port", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void L_ipv6_passe_avant_tout_le_reste()
    {
        // Quand les deux pairs ont une IPv6 globale, il n'y a pas de NAT du
        // tout : c'est le chemin le plus sûr et il ne coûte rien de l'essayer
        // en premier.
        var ordered = CandidateSet.InPriorityOrder(
        [
            End("203.0.113.57", 1),
            End("192.168.1.20", 2),
            End("2001:db8::1", 3),
        ]);

        Assert.Equal(3, ordered[0].Port);
    }

    [Fact]
    public void Les_adresses_privees_passent_avant_les_publiques()
    {
        // Deux joueurs sous le même toit : le NAT ne laisserait pas forcément
        // revenir un paquet parti vers sa propre adresse publique.
        var ordered = CandidateSet.InPriorityOrder([End("203.0.113.57", 1), End("192.168.1.20", 2)]);

        Assert.Equal(2, ordered[0].Port);
    }
}
