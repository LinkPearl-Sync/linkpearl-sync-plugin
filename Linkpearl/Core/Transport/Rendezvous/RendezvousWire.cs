using System.Buffers.Binary;

namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>Types de trame du rendez-vous.</summary>
public static class RendezvousKind
{
    public const byte Announce = 0x01;
    public const byte Matched = 0x02;
    public const byte RelayOpen = 0x03;
    public const byte RelayReady = 0x04;
    public const byte RelayData = 0x05;
    public const byte Error = 0x06;
    public const byte Reflect = 0x07;
    public const byte Reflected = 0x08;
    public const byte TicketRegister = 0x09;
    public const byte TicketAccepted = 0x0A;
    public const byte TicketRedeem = 0x0B;
    public const byte TicketPayload = 0x0C;

    /// <summary>Déclare sa présence et ouvre la remise des demandes.</summary>
    public const byte MailboxOpen = 0x0D;

    /// <summary>Demande lesquelles de ces adresses sont présentes.</summary>
    public const byte MailboxQuery = 0x0E;

    /// <summary>Réponse : un bit par adresse interrogée.</summary>
    public const byte MailboxPresence = 0x0F;

    /// <summary>Dépose une demande dans la boîte de quelqu'un.</summary>
    public const byte MailboxDeposit = 0x10;

    /// <summary>Remise poussée au destinataire.</summary>
    public const byte MailboxDelivery = 0x11;

    /// <summary>Un client demande à un service la liste de ceux qu'il connaît.</summary>
    public const byte DirectoryQuery = 0x12;

    /// <summary>La réponse, une liste d'adresses et de libellés.</summary>
    public const byte DirectoryList = 0x13;

    /// <summary>
    /// Un service se présente à un annuaire.
    /// </summary>
    /// <remarks>
    /// La seule trame qu'un service envoie à un autre, dans un seul sens, sans
    /// rien attendre en retour. Elle ne lui accorde aucune confiance : elle
    /// dépose une candidature dans une file que l'opérateur lira.
    /// </remarks>
    public const byte DirectorySubmit = 0x14;

    /// <summary>Demande une page de la liste de bannissement du service.</summary>
    public const byte BanListQuery = 0x15;

    /// <summary>Une page : son numéro, le nombre de pages, puis le JSON d'une <c>BanList</c>.</summary>
    public const byte BanListData = 0x16;

    /// <summary>Demande une page de la liste signée du cercle ouvert.</summary>
    public const byte ConsensusQuery = 0x17;

    /// <summary>Une page : son numéro, le nombre de pages, puis une tranche du document signé.</summary>
    public const byte ConsensusPage = 0x18;

    /// <summary>Demande une page de l'état public du réseau, que seule une autorité tient.</summary>
    public const byte NetworkStatusQuery = 0x19;

    /// <summary>Une page : son numéro, le nombre de pages, puis une tranche du JSON d'état.</summary>
    public const byte NetworkStatusPage = 0x1A;

    /// <summary>Demande une page de la liste signée v2, celle qui porte les régions.</summary>
    /// <remarks>
    /// Une requête à part plutôt qu'un drapeau : une autorité d'avant répond
    /// « trame inattendue », et le client sait alors redemander la v1.
    /// </remarks>
    public const byte ConsensusV2Query = 0x1B;

    /// <summary>Une page de la liste signée v2.</summary>
    public const byte ConsensusV2Page = 0x1C;
}

/// <summary>Ce qu'un client annonce au rendez-vous.</summary>
/// <remarks>
/// Les jetons sont opaques, et le bloc de candidats est scellé sous une clé
/// dérivée du secret de paire : le serveur ne peut lire ni l'un ni l'autre. Il
/// ne voit que des octets et l'adresse d'où ils viennent.
/// </remarks>
public sealed record Announcement(IReadOnlyList<byte[]> Tickets, byte[] SealedCandidates);

/// <summary>Un service de rendez-vous tel qu'un annuaire le présente.</summary>
/// <remarks>
/// Le libellé vient du réseau : il est borné ici, et normalisé par l'interface
/// avant affichage, comme les noms de pairs.
/// </remarks>
public sealed record DirectoryEntry(string Address, string Label);

/// <summary>
/// Format de trame du rendez-vous, partagé par le client et le serveur.
/// </summary>
/// <remarks>
/// Partagé, et non dupliqué de part et d'autre : deux encodeurs qui divergent
/// donnent un échec silencieux qu'aucun des deux côtés ne peut diagnostiquer.
/// </remarks>
public static class RendezvousWire
{
    public const int MaxFrameLength = 64 * 1024;
    public const int MaxTicketsPerAnnouncement = 8;
    public const int MaxSealedCandidatesLength = 4096;

    /// <summary>
    /// Le texte d'erreur d'un service dont le relais est coupé.
    /// </summary>
    /// <remarks>
    /// Seul cas où le service dit ne pas relayer du tout, et le client en fait
    /// un refus retenu une journée. Ici, dans le fichier que le service recopie,
    /// pour que les deux côtés lisent la même chaîne : un littéral de chaque
    /// côté ferait, au premier écart, passer chaque refus pour un silence.
    /// </remarks>
    public const string RelayDisabledReason = "relais coupé sur ce service";

    public static byte[] Announce(Announcement announcement)
    {
        var body = new List<byte> { RendezvousKind.Announce, (byte)announcement.Tickets.Count };

        foreach (var ticket in announcement.Tickets)
            body.AddRange(ticket);

        body.Add((byte)(announcement.SealedCandidates.Length >> 8));
        body.Add((byte)announcement.SealedCandidates.Length);
        body.AddRange(announcement.SealedCandidates);

        return body.ToArray();
    }

    public static bool TryReadAnnounce(ReadOnlySpan<byte> frame, out Announcement? announcement, out string? rejection)
    {
        announcement = null;

        if (frame.Length < 2 || frame[0] != RendezvousKind.Announce)
        {
            rejection = "annonce malformée";
            return false;
        }

        var count = frame[1];

        if (count is 0 || count > MaxTicketsPerAnnouncement)
        {
            rejection = $"nombre de jetons hors bornes ({count}, plafond {MaxTicketsPerAnnouncement})";
            return false;
        }

        var offset = 2 + (count * RendezvousTicket.SizeInBytes);

        if (frame.Length < offset + 2)
        {
            rejection = "annonce tronquée";
            return false;
        }

        var tickets = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
            tickets.Add(frame.Slice(2 + (i * RendezvousTicket.SizeInBytes), RendezvousTicket.SizeInBytes).ToArray());

        var sealedLength = (frame[offset] << 8) | frame[offset + 1];
        offset += 2;

        if (sealedLength > MaxSealedCandidatesLength || frame.Length < offset + sealedLength)
        {
            rejection = "bloc de candidats hors bornes ou tronqué";
            return false;
        }

        announcement = new Announcement(tickets, frame.Slice(offset, sealedLength).ToArray());
        rejection = null;
        return true;
    }

    public static byte[] Matched(ReadOnlySpan<byte> sealedCandidates)
    {
        var frame = new byte[1 + sealedCandidates.Length];
        frame[0] = RendezvousKind.Matched;
        sealedCandidates.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] RelayOpen(ReadOnlySpan<byte> ticket)
    {
        var frame = new byte[1 + RendezvousTicket.SizeInBytes];
        frame[0] = RendezvousKind.RelayOpen;
        ticket[..RendezvousTicket.SizeInBytes].CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] Simple(byte kind) => [kind];

    public static byte[] RelayData(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.RelayData;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] Error(string reason)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(reason);
        var frame = new byte[1 + text.Length];
        frame[0] = RendezvousKind.Error;
        text.CopyTo(frame.AsSpan(1));
        return frame;
    }

    /// <summary>
    /// Ce qu'un ticket d'invitation fait déposer au rendez-vous.
    /// </summary>
    /// <remarks>
    /// <b>Le serveur peut lire cette charge, et donc la remplacer.</b> C'est la
    /// contrepartie assumée d'un ticket de douze caractères : celui qui le
    /// retire n'a encore aucune clé pour ouvrir quoi que ce soit. La
    /// substitution se détecte après coup, en comparant les six mots que le
    /// handshake produit des deux côtés.
    /// </remarks>
    public const int MaxTicketPayloadLength = 256;

    public static byte[] TicketRegister(ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + InvitationTicketSize + payload.Length];
        frame[0] = RendezvousKind.TicketRegister;
        ticket[..InvitationTicketSize].CopyTo(frame.AsSpan(1));
        payload.CopyTo(frame.AsSpan(1 + InvitationTicketSize));
        return frame;
    }

    public static byte[] TicketRedeem(ReadOnlySpan<byte> ticket)
    {
        var frame = new byte[1 + InvitationTicketSize];
        frame[0] = RendezvousKind.TicketRedeem;
        ticket[..InvitationTicketSize].CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] TicketPayload(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.TicketPayload;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    /// <summary>Taille d'un ticket sur le fil.</summary>
    public const int InvitationTicketSize = 6;

    /// <summary>Taille d'une adresse de boîte sur le fil.</summary>
    public const int MailboxAddressSize = 6;

    /// <summary>
    /// Adresses interrogeables en une fois.
    /// </summary>
    /// <remarks>
    /// Une zone bondée compte quelques dizaines de joueurs visibles. Le plafond
    /// évite qu'un client transforme le rendez-vous en service d'énumération.
    /// </remarks>
    public const int MaxQueriedAddresses = 64;

    public const int MaxDepositLength = 512;

    public static byte[] MailboxOpen(IReadOnlyList<byte[]> addresses)
    {
        var frame = new byte[2 + (addresses.Count * MailboxAddressSize)];
        frame[0] = RendezvousKind.MailboxOpen;
        frame[1] = (byte)addresses.Count;

        for (var i = 0; i < addresses.Count; i++)
            addresses[i].CopyTo(frame.AsSpan(2 + (i * MailboxAddressSize)));

        return frame;
    }

    public static byte[] MailboxQuery(IReadOnlyList<byte[]> addresses)
    {
        var frame = new byte[2 + (addresses.Count * MailboxAddressSize)];
        frame[0] = RendezvousKind.MailboxQuery;
        frame[1] = (byte)addresses.Count;

        for (var i = 0; i < addresses.Count; i++)
            addresses[i].CopyTo(frame.AsSpan(2 + (i * MailboxAddressSize)));

        return frame;
    }

    /// <summary>Lit une liste d'adresses, quel que soit le type de trame.</summary>
    public static bool TryReadAddresses(ReadOnlySpan<byte> frame, out List<byte[]> addresses, out string? rejection)
    {
        addresses = [];

        if (frame.Length < 2)
        {
            rejection = "trame trop courte";
            return false;
        }

        var count = frame[1];

        if (count is 0 || count > MaxQueriedAddresses)
        {
            rejection = $"nombre d'adresses hors bornes ({count}, plafond {MaxQueriedAddresses})";
            return false;
        }

        if (frame.Length < 2 + (count * MailboxAddressSize))
        {
            rejection = "trame tronquée";
            return false;
        }

        for (var i = 0; i < count; i++)
            addresses.Add(frame.Slice(2 + (i * MailboxAddressSize), MailboxAddressSize).ToArray());

        rejection = null;
        return true;
    }

    /// <summary>Un bit par adresse interrogée, dans l'ordre de la question.</summary>
    public static byte[] MailboxPresence(IReadOnlyList<bool> present)
    {
        var frame = new byte[2 + ((present.Count + 7) / 8)];
        frame[0] = RendezvousKind.MailboxPresence;
        frame[1] = (byte)present.Count;

        for (var i = 0; i < present.Count; i++)
        {
            if (present[i])
                frame[2 + (i / 8)] |= (byte)(1 << (i % 8));
        }

        return frame;
    }

    public static bool TryReadPresence(ReadOnlySpan<byte> frame, out bool[] present)
    {
        present = [];

        if (frame.Length < 2)
            return false;

        var count = frame[1];

        if (frame.Length < 2 + ((count + 7) / 8))
            return false;

        present = new bool[count];

        for (var i = 0; i < count; i++)
            present[i] = (frame[2 + (i / 8)] & (1 << (i % 8))) != 0;

        return true;
    }

    public static byte[] MailboxDeposit(ReadOnlySpan<byte> address, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + MailboxAddressSize + payload.Length];
        frame[0] = RendezvousKind.MailboxDeposit;
        address[..MailboxAddressSize].CopyTo(frame.AsSpan(1));
        payload.CopyTo(frame.AsSpan(1 + MailboxAddressSize));
        return frame;
    }

    public static byte[] MailboxDelivery(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[1 + payload.Length];
        frame[0] = RendezvousKind.MailboxDelivery;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public const int MaxDirectoryEntries = 64;
    public const int MaxDirectoryLabelLength = 64;
    public const int MaxDirectoryAddressLength = 255;

    /// <summary>
    /// La liste des services qu'un service connaît.
    /// </summary>
    /// <remarks>
    /// Elle vient de sa configuration, écrite par son opérateur, jamais d'un
    /// échange entre services : il publie ce qu'il connaît, il n'interroge
    /// personne.
    /// </remarks>
    public static byte[] Directory(IReadOnlyList<DirectoryEntry> entries)
        => WriteEntries(RendezvousKind.DirectoryList, entries);

    public static byte[] DirectorySubmit(string address, string label)
        => WriteEntries(RendezvousKind.DirectorySubmit, [new DirectoryEntry(address, label)]);

    private static byte[] WriteEntries(byte kind, IReadOnlyList<DirectoryEntry> entries)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entries.Count, MaxDirectoryEntries);

        var bytes = new List<byte> { kind, (byte)entries.Count };

        foreach (var entry in entries)
        {
            var address = System.Text.Encoding.UTF8.GetBytes(entry.Address);
            var label = System.Text.Encoding.UTF8.GetBytes(entry.Label);

            if (address.Length is 0 or > MaxDirectoryAddressLength)
                throw new ArgumentException($"adresse hors bornes : {address.Length} octets", nameof(entries));

            if (label.Length > MaxDirectoryLabelLength)
                throw new ArgumentException($"libellé hors bornes : {label.Length} octets", nameof(entries));

            bytes.Add((byte)address.Length);
            bytes.AddRange(address);
            bytes.Add((byte)label.Length);
            bytes.AddRange(label);
        }

        return [.. bytes];
    }

    /// <summary>Lit une liste d'entrées, qu'elle vienne d'un annuaire ou d'une candidature.</summary>
    public static bool TryReadDirectory(
        ReadOnlySpan<byte> frame, out List<DirectoryEntry> entries, out string? rejection)
    {
        entries = [];

        if (frame.Length < 2)
        {
            rejection = "trame trop courte";
            return false;
        }

        var count = frame[1];

        if (count > MaxDirectoryEntries)
        {
            rejection = $"nombre d'entrées hors bornes ({count}, plafond {MaxDirectoryEntries})";
            return false;
        }

        var offset = 2;

        for (var i = 0; i < count; i++)
        {
            if (TryReadText(frame, ref offset, MaxDirectoryAddressLength, "adresse", out var address, out rejection) is false)
                return false;

            if (TryReadText(frame, ref offset, MaxDirectoryLabelLength, "libellé", out var label, out rejection) is false)
                return false;

            entries.Add(new DirectoryEntry(address, label));
        }

        rejection = null;
        return true;
    }

    private static bool TryReadText(
        ReadOnlySpan<byte> frame, ref int offset, int max, string what, out string value, out string? rejection)
    {
        value = string.Empty;

        if (offset >= frame.Length)
        {
            rejection = "trame tronquée";
            return false;
        }

        var length = frame[offset++];

        if (length > max)
        {
            rejection = $"{what} hors bornes ({length} octets, plafond {max})";
            return false;
        }

        if (offset + length > frame.Length)
        {
            rejection = "trame tronquée";
            return false;
        }

        value = System.Text.Encoding.UTF8.GetString(frame.Slice(offset, length));
        offset += length;
        rejection = null;
        return true;
    }

    /// <summary>
    /// Entrées de la liste de bannissement par page.
    /// </summary>
    /// <remarks>
    /// Une entrée pèse jusqu'à 860 octets en JSON indenté : un motif de 120
    /// caractères que l'encodeur par défaut échappe chacun en six. Soixante-
    /// quatre tiennent sous une trame, et soixante-quatre pages couvrent les
    /// 4 096 entrées qu'une liste peut compter. Tronquer à une trame ferait un
    /// banni que plus personne ne voit, sans que rien le dise.
    /// </remarks>
    public const int BanListPageEntries = 64;

    public const int MaxBanListPages = 64;

    public static byte[] BanListQuery(int page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, MaxBanListPages);

        return [RendezvousKind.BanListQuery, (byte)(page >> 8), (byte)page];
    }

    public static bool TryReadBanListQuery(ReadOnlySpan<byte> frame, out int page)
    {
        page = 0;

        if (frame.Length != 3 || frame[0] != RendezvousKind.BanListQuery)
            return false;

        page = (frame[1] << 8) | frame[2];
        return page < MaxBanListPages;
    }

    public static byte[] BanListData(int page, int pages, string json)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pages, MaxBanListPages);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, pages);

        var text = System.Text.Encoding.UTF8.GetBytes(json);

        if (5 + text.Length > MaxFrameLength)
            throw new ArgumentException($"page de {text.Length} octets, au-delà d'une trame", nameof(json));

        var frame = new byte[5 + text.Length];
        frame[0] = RendezvousKind.BanListData;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)page);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)pages);
        text.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static bool TryReadBanListData(
        ReadOnlySpan<byte> frame, out int page, out int pages, out string json, out string? rejection)
    {
        page = 0;
        pages = 0;
        json = string.Empty;

        if (frame.Length < 6 || frame[0] != RendezvousKind.BanListData)
        {
            rejection = "page de liste malformée";
            return false;
        }

        page = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]);
        pages = BinaryPrimitives.ReadUInt16BigEndian(frame[3..]);

        if (pages is < 1 or > MaxBanListPages || page >= pages)
        {
            rejection = $"page {page} sur {pages}, hors bornes (plafond {MaxBanListPages})";
            return false;
        }

        json = System.Text.Encoding.UTF8.GetString(frame[5..]);
        rejection = null;
        return true;
    }

    /// <summary>Octets du document signé portés par une page.</summary>
    /// <remarks>
    /// Une tranche brute et non des entrées : le document ne se vérifie
    /// qu'entier, donc le découper selon son contenu n'apporterait rien.
    /// </remarks>
    public const int ConsensusPageBytes = 32 * 1024;

    /// <summary>Plafond de pages, donc du document : 512 Kio.</summary>
    public const int MaxConsensusPages = 16;

    public static byte[] ConsensusQuery(int page) => PageQuery(RendezvousKind.ConsensusQuery, page);

    public static bool TryReadConsensusQuery(ReadOnlySpan<byte> frame, out int page)
        => TryReadPageQuery(RendezvousKind.ConsensusQuery, frame, out page);

    public static byte[] ConsensusPage(int page, int pages, ReadOnlySpan<byte> chunk)
        => ChunkPage(RendezvousKind.ConsensusPage, page, pages, chunk);

    public static bool TryReadConsensusPage(
        ReadOnlySpan<byte> frame, out int page, out int pages, out byte[] chunk, out string? rejection)
        => TryReadChunkPage(RendezvousKind.ConsensusPage, "page de liste signée malformée", frame, out page, out pages, out chunk, out rejection);

    public static byte[] ConsensusV2Query(int page) => PageQuery(RendezvousKind.ConsensusV2Query, page);

    public static bool TryReadConsensusV2Query(ReadOnlySpan<byte> frame, out int page)
        => TryReadPageQuery(RendezvousKind.ConsensusV2Query, frame, out page);

    public static byte[] ConsensusV2Page(int page, int pages, ReadOnlySpan<byte> chunk)
        => ChunkPage(RendezvousKind.ConsensusV2Page, page, pages, chunk);

    public static bool TryReadConsensusV2Page(
        ReadOnlySpan<byte> frame, out int page, out int pages, out byte[] chunk, out string? rejection)
        => TryReadChunkPage(RendezvousKind.ConsensusV2Page, "page de liste signée v2 malformée", frame, out page, out pages, out chunk, out rejection);

    /// <summary>
    /// Une page de l'état public du réseau, découpé comme la liste signée.
    /// </summary>
    /// <remarks>
    /// Mêmes tranches et même plafond : ce n'est qu'un autre document servi par
    /// l'autorité, lu par le site du projet pour sa page publique.
    /// </remarks>
    public static byte[] NetworkStatusQuery(int page) => PageQuery(RendezvousKind.NetworkStatusQuery, page);

    public static bool TryReadNetworkStatusQuery(ReadOnlySpan<byte> frame, out int page)
        => TryReadPageQuery(RendezvousKind.NetworkStatusQuery, frame, out page);

    public static byte[] NetworkStatusPage(int page, int pages, ReadOnlySpan<byte> chunk)
        => ChunkPage(RendezvousKind.NetworkStatusPage, page, pages, chunk);

    public static bool TryReadNetworkStatusPage(
        ReadOnlySpan<byte> frame, out int page, out int pages, out byte[] chunk, out string? rejection)
        => TryReadChunkPage(RendezvousKind.NetworkStatusPage, "page d'état du réseau malformée", frame, out page, out pages, out chunk, out rejection);

    private static byte[] PageQuery(byte kind, int page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, MaxConsensusPages);

        return [kind, (byte)(page >> 8), (byte)page];
    }

    private static bool TryReadPageQuery(byte kind, ReadOnlySpan<byte> frame, out int page)
    {
        page = 0;

        if (frame.Length != 3 || frame[0] != kind)
            return false;

        page = (frame[1] << 8) | frame[2];
        return page < MaxConsensusPages;
    }

    private static byte[] ChunkPage(byte kind, int page, int pages, ReadOnlySpan<byte> chunk)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pages, MaxConsensusPages);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, pages);

        if (chunk.Length is 0 || chunk.Length > ConsensusPageBytes)
            throw new ArgumentException($"tranche de {chunk.Length} octets, hors de 1 à {ConsensusPageBytes}", nameof(chunk));

        var frame = new byte[5 + chunk.Length];
        frame[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)page);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)pages);
        chunk.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static bool TryReadChunkPage(
        byte kind, string malformed, ReadOnlySpan<byte> frame,
        out int page, out int pages, out byte[] chunk, out string? rejection)
    {
        page = 0;
        pages = 0;
        chunk = [];

        if (frame.Length < 6 || frame.Length > 5 + ConsensusPageBytes || frame[0] != kind)
        {
            rejection = malformed;
            return false;
        }

        page = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]);
        pages = BinaryPrimitives.ReadUInt16BigEndian(frame[3..]);

        if (pages is < 1 or > MaxConsensusPages || page >= pages)
        {
            rejection = $"page {page} sur {pages}, hors bornes (plafond {MaxConsensusPages})";
            return false;
        }

        chunk = frame[5..].ToArray();
        rejection = null;
        return true;
    }

    /// <summary>Préfixe de longueur, pour délimiter les trames sur un flux.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        var framed = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
        body.CopyTo(framed.AsSpan(sizeof(int)));
        return framed;
    }
}
