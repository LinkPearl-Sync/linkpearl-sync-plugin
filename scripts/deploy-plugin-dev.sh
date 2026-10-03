#!/usr/bin/env bash
# Compile le plugin en Debug et le copie vers le dossier de dev plugin Windows,
# pour tester depuis WSL sans passer par une release.
#
# Ne publie rien : pas de tag, pas de push, aucun effet hors de la machine.
#
# Usage : ./scripts/deploy-plugin-dev.sh
#         LINKPEARL_DEV_PLUGIN_DIR=/autre/chemin ./scripts/deploy-plugin-dev.sh
#
# Côté Dalamud, une seule configuration à faire une fois :
#   /xlsettings > Experimental > Dev Plugin Locations > ajouter
#   D:\XIVDev\Linkpearl\LinkpearlSync.dll (la DLL elle-même : depuis le
#   renommage, un emplacement qui visait Linkpearl.dll ne trouve plus rien)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/Linkpearl/Linkpearl.csproj"
BUILD_DIR="$REPO_ROOT/Linkpearl/bin/Debug"
DEST="${LINKPEARL_DEV_PLUGIN_DIR:-/mnt/d/XIVDev/Linkpearl}"

# Le SDK Dalamud cherche ses assemblies dans ~/.xlcore sous Linux, alors que le
# Dalamud à jour est celui de XIVLauncher côté Windows. On les aligne avant de
# compiler, sinon on construit contre une API plus ancienne que celle qui
# chargera le plugin.
WINDOWS_HOOKS="$(ls -d /mnt/c/Users/*/AppData/Roaming/XIVLauncher/addon/Hooks/dev 2>/dev/null | head -1 || true)"
LINUX_HOOKS="$HOME/.xlcore/dalamud/Hooks/dev"

if [ -n "$WINDOWS_HOOKS" ] && [ -d "$WINDOWS_HOOKS" ]; then
  echo "==> Alignement des assemblies Dalamud depuis $WINDOWS_HOOKS"
  mkdir -p "$LINUX_HOOKS"
  rsync -a --delete "$WINDOWS_HOOKS/" "$LINUX_HOOKS/"
fi

echo "==> Compilation Debug"
dotnet build "$PROJECT" -c Debug --nologo

if [ ! -f "$BUILD_DIR/LinkpearlSync.dll" ]; then
  echo "Erreur : $BUILD_DIR/LinkpearlSync.dll est introuvable." >&2
  exit 1
fi

# On copie tout le répertoire de sortie, et surtout pas une liste écrite en
# dur : le SDK Dalamud en a déjà retiré les assemblies qu'il fournit lui-même,
# donc ce qui reste est exactement ce dont le plugin a besoin. Une liste en dur
# oublie les dépendances transitives, et le plugin échoue au chargement sur un
# « Could not load file or assembly » (c'est arrivé avec Luna.dll, dépendance
# de Penumbra.Api).
#
# --delete pour qu'une DLL d'une compilation précédente ne survive pas à la
# suppression de sa dépendance.
#
# --inplace est ce qui fait marcher le rechargement automatique de Dalamud.
# Sans lui, rsync écrit dans un fichier temporaire puis le renomme par-dessus
# la cible : le surveillant de fichiers de Dalamud reçoit une création, là où il
# attend une écriture sur la DLL, et ne recharge pas. Le réglage « Automatic
# reloading » a beau être actif, il ne voit rien passer.
echo "==> Copie vers $DEST"
mkdir -p "$DEST"
rsync -a --delete --inplace "$BUILD_DIR/" "$DEST/"

# Dernière écriture sur la DLL elle-même, après tout le reste : c'est elle que
# Dalamud surveille, et elle doit être le dernier événement qu'il reçoive, sans
# quoi il rechargerait pendant que les dépendances changent encore.
touch "$DEST/LinkpearlSync.dll"

echo "==> Déployé :"
ls -la "$DEST"
echo
echo "Dans le jeu : /xlplugins > Dev Tools > recharger le plugin."
