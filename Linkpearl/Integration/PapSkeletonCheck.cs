using System.Buffers;
using System.Buffers.Binary;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using Linkpearl.Core.Safety;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace Linkpearl.Integration;

/// <summary>
/// Dit si une animation anime des os que notre squelette n'a pas.
/// </summary>
/// <remarks>
/// Une animation liée à un os au-delà du squelette qui la joue fait écrire le
/// moteur du jeu hors de ses tableaux : c'est le plantage que les clients
/// comparables se sont vus infliger par des animations de pairs. L'émetteur
/// vérifie ses propres fichiers, sans risque puisque son jeu les charge déjà.
/// Le receveur vérifie aussi ceux qu'il reçoit, avant de les poser
/// (<c>RemoteApplicator</c>, <c>ReceivedAnimationScreen</c>) : un émetteur
/// modifié se passe du premier contrôle.
///
/// La lecture Havok passe par le chargeur du jeu, qui alloue dans la mémoire
/// Havok du fil courant : elle ne s'appelle que depuis le thread du framework.
/// </remarks>
internal static unsafe class PapSkeletonCheck
{
    private const int HeaderLength = 26;

    /// <summary>
    /// Lit la section Havok d'un <c>.pap</c> dans un tampon loué, hors du thread du jeu.
    /// </summary>
    /// <remarks>
    /// Loué et non alloué : une animation pèse jusqu'à plusieurs mégaoctets, et
    /// un tableau neuf de cette taille finirait sur le tas des gros objets.
    /// Rendre le tampon avec <see cref="ArrayPool{T}.Shared"/> une fois lu.
    /// </remarks>
    public static bool TryReadHavok(string localPath, string gamePath, out byte[] buffer, out int length)
    {
        buffer = [];
        length = 0;

        using var stream = File.OpenRead(localPath);

        if (TransientFileCheck.IsWellFormed(gamePath, stream, out _) is false)
            return false;

        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);

        var havok = BinaryPrimitives.ReadInt32LittleEndian(header[18..]);
        var footer = BinaryPrimitives.ReadInt32LittleEndian(header[22..]);

        length = footer - havok;
        buffer = ArrayPool<byte>.Shared.Rent(length);
        stream.Position = havok;
        stream.ReadExactly(buffer.AsSpan(0, length));

        return true;
    }

    /// <summary>Le nombre d'os du plus grand squelette partiel du personnage, ou null s'il n'est pas dessiné.</summary>
    public static int? BoneCount(IGameObject character)
    {
        var gameObject = (GameObject*)character.Address;

        if (gameObject == null || gameObject->DrawObject == null
            || gameObject->DrawObject->Object.GetObjectType() is not SceneObjectType.CharacterBase)
            return null;

        var skeleton = ((CharacterBase*)gameObject->DrawObject)->Skeleton;

        if (skeleton == null)
            return null;

        var most = 0;

        for (var i = 0; i < skeleton->PartialSkeletonCount; i++)
        {
            var handle = skeleton->PartialSkeletons[i].SkeletonResourceHandle;

            if (handle != null)
                most = Math.Max(most, (int)handle->BoneCount);
        }

        return most > 0 ? most : null;
    }

    /// <summary>Le plus grand indice d'os animé par cette section Havok, ou null si elle ne se charge pas.</summary>
    public static int? MaxAnimatedBone(ReadOnlySpan<byte> havok)
    {
        var registry = hkBuiltinTypeRegistry.Instance();

        var options = stackalloc hkSerializeUtil.LoadOptions[1];
        options->TypeInfoRegistry = registry->GetTypeInfoRegistry();
        options->ClassNameRegistry = registry->GetClassNameRegistry();
        options->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
        {
            Storage = (int)hkSerializeUtil.LoadOptionBits.Default,
        };

        fixed (byte* data = havok)
        {
            var resource = hkSerializeUtil.LoadFromBuffer(data, havok.Length, null, options);

            if (resource == null)
                return null;

            try
            {
                var root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer"u8, options->TypeInfoRegistry);

                if (root == null)
                    return null;

                var container = (hkaAnimationContainer*)root->findObjectByType("hkaAnimationContainer", null);

                if (container == null)
                    return null;

                var most = -1;

                for (var i = 0; i < container->Bindings.Length; i++)
                {
                    var binding = container->Bindings.Data[i].ptr;

                    if (binding == null)
                        continue;

                    var tracks = binding->TransformTrackToBoneIndices;

                    for (var j = 0; j < tracks.Length; j++)
                        most = Math.Max(most, tracks.Data[j]);
                }

                return most;
            }
            finally
            {
                resource->RemoveReference();
            }
        }
    }
}
