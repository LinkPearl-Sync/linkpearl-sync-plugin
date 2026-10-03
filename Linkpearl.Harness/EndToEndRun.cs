using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using LiteNetLib;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transfer;
using Linkpearl.Core.Transport;

namespace Linkpearl.Harness;

internal sealed class RealClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record EndToEndSettings(
    string SourceCacheRoot, string ManifestPath, int DataChannels,
    int LatencyMs, double LossPercent, bool RateLimited, int BlockSize,
    bool SynthesizeFromCache);

/// <summary>
/// Fait tourner la chaîne complète entre deux exemplaires du noyau.
/// </summary>
/// <remarks>
/// Pairage, handshake authentifié, canal chiffré, annonce et transfert du
/// manifeste, plan de ce qui manque, transfert des blobs, vérification. Tout
/// sauf le rendez-vous et la traversée de NAT, qui ne s'éprouvent pas en local.
///
/// Le but n'est pas la démonstration : c'est le seul endroit où les briques se
/// rencontrent, et donc le seul endroit où un désaccord entre elles peut
/// apparaître avant le jeu.
/// </remarks>
public static class EndToEndRun
{
    private const byte ControlChannel = 0;

    public static async Task ExecuteAsync(EndToEndSettings settings, CancellationToken ct)
    {
        var clock = new RealClock();

        // Les deux identités, et le carnet : chacun n'accepte que l'autre.
        using var aliceIdentity = CryptoPrimitives.GenerateIdentity();
        using var bobIdentity = CryptoPrimitives.GenerateIdentity();
        var alicePublic = CryptoPrimitives.ExportPublicPoint(aliceIdentity);
        var bobPublic = CryptoPrimitives.ExportPublicPoint(bobIdentity);

        var source = new FileSystemBlobStore(
            settings.SourceCacheRoot, new CacheSettings(), clock, _ => long.MaxValue);

        var destinationRoot = Path.Combine(Path.GetTempPath(), "linkpearl-e2e-" + Guid.NewGuid().ToString("N"));
        var destination = new FileSystemBlobStore(
            destinationRoot, new CacheSettings(), clock, _ => long.MaxValue);

        Console.WriteLine($"Cache source     : {settings.SourceCacheRoot}");
        Console.WriteLine($"  {source.Count} blobs, {source.TotalBytes / 1024.0 / 1024.0:F1} Mo");
        Console.WriteLine($"Cache destination : {destinationRoot} (vide)");
        Console.WriteLine();

        CharacterManifest manifest;
        int manifestSize;

        if (settings.SynthesizeFromCache)
        {
            manifest = SynthesizeFromCache(settings.SourceCacheRoot);
            manifestSize = ManifestCodec.Compress(manifest).Length;
            Console.WriteLine("Manifeste SYNTHÉTIQUE, reconstruit depuis les blobs du cache source.");
        }
        else
        {
            var manifestBytes = await File.ReadAllBytesAsync(settings.ManifestPath, ct).ConfigureAwait(false);

            if (ManifestCodec.TryDecompress(manifestBytes, Quotas.Default, out var decoded, out var why) is false)
                throw new InvalidOperationException($"manifeste illisible : {why}");

            manifest = decoded!;
            manifestSize = manifestBytes.Length;
        }

        Console.WriteLine($"Manifeste : {manifest.Replacements.Count} entrées, "
                        + $"{manifest.Replacements.Sum(r => r.GamePaths.Count)} chemins de jeu, "
                        + $"{manifestSize} octets compressés.");

        if (manifest.Replacements.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Le manifeste est vide : la capture a probablement été faite avec les mods");
            Console.WriteLine("désactivés. Relancer /lpearl capture mods activés, ou utiliser --from-cache.");
            return;
        }

        var (link, proxy) = OpenLink(settings);

        using (proxy)
        {
            await using var session = link;

            Console.WriteLine();
            Console.Write("Handshake... ");
            var watch = Stopwatch.StartNew();

            var (aliceKeys, bobKeys) = await HandshakeAsync(
                session, aliceIdentity, bobIdentity, alicePublic, bobPublic, clock, ct).ConfigureAwait(false);

            Console.WriteLine($"établi en {watch.ElapsedMilliseconds} ms.");
            Console.WriteLine($"  Chaîne d'authentification : {string.Join(" ", ShortAuthString.Of(aliceKeys.SessionId))}");
            Console.WriteLine($"  Identique des deux côtés  : "
                            + $"{aliceKeys.SessionId.AsSpan().SequenceEqual(bobKeys.SessionId)}");

            var aliceChannel = new SecureChannel(aliceKeys.SendKey, aliceKeys.ReceiveKey, aliceKeys.SessionId);
            var bobChannel = new SecureChannel(bobKeys.SendKey, bobKeys.ReceiveKey, bobKeys.SessionId);

            var plan = BlobRequestPlanner.Plan(manifest, destination);

            Console.WriteLine();
            Console.WriteLine($"Plan : {plan.Missing.Count} blobs à transférer, "
                            + $"{plan.MissingBytes / 1024.0 / 1024.0:F1} Mo. "
                            + $"Déjà en cache : {plan.CachedBytes / 1024.0 / 1024.0:F1} Mo.");

            await TransferAsync(session, aliceChannel, bobChannel, source, destination, plan, settings, clock, ct)
                .ConfigureAwait(false);

            Verify(manifest, destination);
        }

        Directory.Delete(destinationRoot, recursive: true);
    }

    /// <summary>
    /// Reconstruit un manifeste depuis les blobs présents dans un cache.
    /// </summary>
    /// <remarks>
    /// Réservé au banc : les chemins de jeu sont inventés. Cela permet
    /// d'éprouver le transfert sur un volume réaliste sans dépendre d'une
    /// capture fraîche.
    /// </remarks>
    /// <summary>Un manifeste bâti sur les blobs déjà en cache, quand aucune capture n'existe.</summary>
    internal static CharacterManifest SynthesizeFromCache(string root)
    {
        var replacements = Directory
            .EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetFileName(path), new FileInfo(path).Length))
            .Where(entry => BlobHash.TryParseHex(entry.Item1, out _))
            .Select((entry, index) =>
            {
                BlobHash.TryParseHex(entry.Item1, out var hash);
                return new FileReplacement([$"chara/equipment/e{index:D4}/model/c0101e{index:D4}_top.mdl"], hash, entry.Length);
            })
            .OrderBy(r => r.Hash.ToHex(), StringComparer.Ordinal)
            .ToArray();

        return new CharacterManifest(CharacterManifest.CurrentVersion, replacements, "bWV0YQ==", null);
    }

    private static (LoopbackLink Link, ImpairmentProxy? Proxy) OpenLink(EndToEndSettings settings)
    {
        var link = new LoopbackLink(settings.DataChannels + 1);
        ImpairmentProxy? proxy = null;

        if (settings.LatencyMs > 0 || settings.LossPercent > 0)
        {
            proxy = new ImpairmentProxy(
                new IPEndPoint(IPAddress.Loopback, link.ServerPort),
                settings.LatencyMs, settings.LatencyMs / 10, settings.LossPercent, seed: 1);
        }

        link.Connect(proxy?.Port ?? link.ServerPort);
        return (link, proxy);
    }

    private static async Task<(SessionKeys Alice, SessionKeys Bob)> HandshakeAsync(
        LoopbackLink link, ECDsa aliceIdentity, ECDsa bobIdentity,
        byte[] alicePublic, byte[] bobPublic, IClock clock, CancellationToken ct)
    {
        var alice = new HandshakeInitiator(aliceIdentity, clock);
        var bob = new HandshakeResponder(bobIdentity, clock);

        link.SendFromClient(ControlChannel, alice.CreateMessage1());
        var message1 = await link.ReceiveOnServerAsync(ct).ConfigureAwait(false);

        if (bob.TryHandleMessage1(message1.Payload, out var message2, out var why1) is false)
            throw new InvalidOperationException($"message 1 refusé : {why1}");

        link.SendFromServer(ControlChannel, message2!);
        var received2 = await link.ReceiveOnClientAsync(ct).ConfigureAwait(false);

        if (alice.TryHandleMessage2(received2.Payload, key => key.AsSpan().SequenceEqual(bobPublic),
                                    out var message3, out var aliceKeys, out var why2) is false)
            throw new InvalidOperationException($"message 2 refusé : {why2}");

        link.SendFromClient(ControlChannel, message3!);
        var received3 = await link.ReceiveOnServerAsync(ct).ConfigureAwait(false);

        if (bob.TryHandleMessage3(received3.Payload, key => key.AsSpan().SequenceEqual(alicePublic),
                                  out var bobKeys, out var why3) is false)
            throw new InvalidOperationException($"message 3 refusé : {why3}");

        return (aliceKeys!, bobKeys!);
    }

    private static async Task TransferAsync(
        LoopbackLink link, SecureChannel aliceChannel, SecureChannel bobChannel,
        IBlobStore source, IBlobStore destination, TransferPlan plan,
        EndToEndSettings settings, IClock clock, CancellationToken ct)
    {
        var requested = plan.Missing.ToDictionary(m => m.Hash, m => m.Size);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, requested);

        var sender = new BlobSender(source, settings.BlockSize);
        var channels = new ChannelPlan(settings.DataChannels);
        var limiter = new RateLimiter(clock, new RateLimiterSettings());

        var watch = Stopwatch.StartNew();
        var transferred = 0L;
        var completed = 0;
        var lastReport = 0L;

        Console.WriteLine();

        // Le récepteur tourne en parallèle de l'émission. En pas-à-pas, chaque
        // trame coûtait un aller-retour complet et le débit mesuré n'aurait
        // rien voulu dire.
        var consumer = Task.Run(async () =>
        {
            while (completed < plan.Missing.Count && ct.IsCancellationRequested is false)
            {
                var incoming = await link.ReceiveOnServerAsync(ct).ConfigureAwait(false);

                if (bobChannel.TryOpen(incoming.Payload, out var kind, out var channel, out var payload, out var rejection) is false)
                    throw new InvalidOperationException($"trame refusée : {rejection}");

                var outcome = await receiver.HandleAsync(channel, kind, payload, ct).ConfigureAwait(false);

                if (outcome.Accepted is false)
                    throw new InvalidOperationException($"réception refusée : {outcome.Rejection}");

                if (kind == MessageKind.BlobChunk)
                    Interlocked.Add(ref transferred, payload.Length - 4);

                if (outcome.BlobCompleted || outcome.AlreadyPresent)
                    Interlocked.Increment(ref completed);
            }
        }, ct);

        foreach (var request in plan.Missing)
        {
            // Un blob entier sur un seul canal : LiteNetLib ne garantit l'ordre
            // qu'à l'intérieur d'un canal, donc une clôture envoyée ailleurs que
            // ses blocs pourrait les précéder.
            var channel = channels.Next((int)request.Size);

            await foreach (var frame in sender.FramesFor(request.Hash, ct).ConfigureAwait(false))
            {
                if (settings.RateLimited)
                {
                    while (limiter.TryConsume(frame.Payload.Length) is false)
                    {
                        await Task.Delay(2, ct).ConfigureAwait(false);
                        limiter.Observe(link.PacketLossPercent, link.RoundTripMs);
                    }
                }

                link.SendFromClient(channel, aliceChannel.Seal(channel, frame.Kind, frame.Payload));
            }

            channels.Completed(channel, (int)request.Size);

            var seen = Interlocked.Read(ref transferred);
            if (seen - lastReport > 64 * 1024 * 1024)
            {
                lastReport = seen;
                Console.WriteLine($"  {seen / 1024.0 / 1024.0,7:F0} Mo reçus, {completed,3} blobs, "
                                + $"{seen / 1024.0 / 1024.0 / watch.Elapsed.TotalSeconds,6:F2} Mo/s");
            }
        }

        await consumer.ConfigureAwait(false);
        watch.Stop();

        Console.WriteLine();
        Console.WriteLine($"Transfert : {transferred / 1024.0 / 1024.0:F1} Mo en {watch.Elapsed.TotalSeconds:F1} s, "
                        + $"{transferred / 1024.0 / 1024.0 / watch.Elapsed.TotalSeconds:F2} Mo/s, {completed} blobs.");
    }

    private static void Verify(CharacterManifest manifest, IBlobStore destination)
    {
        var missing = manifest.Replacements
            .Where(r => destination.TryGetSize(r.Hash, out _) is false)
            .ToList();

        Console.WriteLine();

        if (missing.Count > 0)
        {
            Console.WriteLine($"ÉCHEC : {missing.Count} blobs absents du cache destination.");
            return;
        }

        Console.WriteLine($"VÉRIFIÉ : les {manifest.Replacements.Count} blobs du manifeste sont présents,");
        Console.WriteLine("et chacun a été publié parce que son empreinte recalculée correspondait.");
    }
}
