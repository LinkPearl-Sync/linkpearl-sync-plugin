namespace Linkpearl.Core.Protocol;

/// <summary>
/// Types de message du protocole.
/// </summary>
/// <remarks>
/// Des constantes et non une énumération : cet octet vient du réseau, et une
/// énumération C# accepte sans broncher n'importe quelle valeur entière, ce qui
/// donnerait une fausse impression de validation. Ici, tout ce qui n'est pas
/// reconnu est traité explicitement.
/// </remarks>
public static class MessageKind
{
    public const byte Hello = 0x01;
    public const byte ManifestOffer = 0x02;
    public const byte ManifestRequest = 0x03;
    public const byte ManifestData = 0x04;
    public const byte BlobWant = 0x05;
    public const byte BlobStart = 0x07;
    public const byte BlobChunk = 0x08;
    public const byte BlobEnd = 0x09;
    public const byte Presence = 0x0B;
    public const byte Ping = 0x0C;
    public const byte Pong = 0x0D;
    public const byte Bye = 0x0E;

    /// <summary>
    /// L'expéditeur nous a retirés de son carnet.
    /// </summary>
    /// <remarks>
    /// Sans charge utile : la session chiffrée dit déjà de qui il vient, et
    /// seul ce pair-là peut l'envoyer. Un client qui ne le connaît pas l'ignore.
    /// </remarks>
    public const byte Unpair = 0x0F;

    /// <summary>
    /// La politique signée d'un groupe : identifiant (16) puis politique encodée.
    /// </summary>
    /// <remarks>
    /// Envoyée à l'ouverture de chaque session de groupe et à chaque nouvelle
    /// version. Le receveur garde la plus récente et renvoie la sienne si elle
    /// l'est davantage. Un client qui ne le connaît pas l'ignore.
    /// </remarks>
    public const byte GroupPolicy = 0x10;

    /// <summary>
    /// L'expéditeur vient de nous mettre en pause, et ferme la session.
    /// </summary>
    /// <remarks>
    /// Sans charge utile, comme <see cref="Unpair"/>. Envoyé seulement sur une
    /// session déjà ouverte : une pause ne fait jamais chercher le pair pour la
    /// lui dire. Un client qui ne le connaît pas l'ignore.
    /// </remarks>
    public const byte Pause = 0x11;
}
