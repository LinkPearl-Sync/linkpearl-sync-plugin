using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

public class ReflectRequestTests
{
    [Fact]
    public void La_demande_de_reflexion_pese_plus_que_toute_reponse()
    {
        // Sinon, une source usurpée ferait du service un amplificateur. La
        // réponse la plus longue : type, longueur, IPv6 (16), port (2).
        const int LargestReply = 1 + 1 + 16 + 2;

        var request = RendezvousClient.ReflectRequest();

        Assert.True(request.Length >= LargestReply);
        Assert.Equal(RendezvousKind.Reflect, request[0]);
        Assert.All(request[1..], b => Assert.Equal(0, b));
    }
}
