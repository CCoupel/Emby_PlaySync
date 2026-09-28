# Publish QUALIF — adaptations projet Emby_PlaySync

> Compagnon de `publish.qualif.template.md` (mode `promote`) : **prevaut** sur le template. Meme principe
> (zero rebuild), artefact adapte au plugin.

```bash
# 1. Verification
CANDIDATE="$BUILD_DIR/EmbySharedPlaylist-$VERSION.dll"
test -f "$CANDIDATE" || { echo "Aucun candidat — executer /build d'abord"; exit 1; }

# 2. Promotion : copie du DLL tel quel dans build/qualif_vX.Y.Z/ (racine du repo, sans `a` dans le dossier)
TARGET_DIR="$REPO_ROOT/build/qualif_v$DIR_VERSION"
mkdir -p "$TARGET_DIR"
cp "$CANDIDATE" "$TARGET_DIR/EmbySharedPlaylist-$VERSION.dll"
stat -c %s "$TARGET_DIR/EmbySharedPlaylist-$VERSION.dll"   # taille de reference pour DEPLOY

# 3. Notification
echo "Publication QUALIF terminee - $VERSION -> $TARGET_DIR/EmbySharedPlaylist-$VERSION.dll"
```

Pas de registre, pas de CI, aucune variable requise. Echec / rollback : identiques au template
(`rm -rf "$TARGET_DIR"` puis `/publish qualif`).
