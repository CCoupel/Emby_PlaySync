# Deploy PROD — adaptations projet Emby_PlaySync

> Compagnon de `deploy.prod.template.md` : **remplace** la procedure Helm generique. Meme mecanique que
> `deploy.qualif.md` (copie du DLL dans `/config/plugins`, restart, verification de taille) mais :
> cible `deployment/emby` (namespace `media`) et artefact = asset de la GitHub Release (BORE : jamais
> le DLL local de QUALIF, la CI a reconstruit depuis le tag).

## PRE-DEPLOYMENT — Sauvegarde de `library.db`

**Avant tout déploiement**, sauvegarder la base de données Emby de PROD pour la cohérence et le rollback :

**Chemin réel** : `/config/data/library.db` (et `.wal`, `.shm`) — **non** `/config/library.db` ni `/config/plugins/`

```bash
# 1. Scale à 0 et attendre la terminaison complète du pod
kubectl scale deployment/emby -n media --replicas=0
sleep 5
kubectl wait --for=delete pod -l app=emby -n media --timeout=60s 2>/dev/null || true

# 2. Copie à froid via pod busybox temporaire monté sur PVC emby-config-claim
#    (impossible d'utiliser kubectl exec après scale 0 — le pod n'existe plus)
BACKUP_DIR="backups/prod/pre-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"

# Lancer un pod busybox temporaire (point de montage emby-config-claim)
BUSYBOX_POD="backup-busybox-$$-$(date +%s)"
kubectl run "$BUSYBOX_POD" -n media --image=busybox:latest --restart=Never \
  -v emby-config=/config --overrides='{"spec":{"containers":[{"name":"busybox","volumeMounts":[{"name":"emby-config","mountPath":"/config"}]}],"volumes":[{"name":"emby-config","persistentVolumeClaim":{"claimName":"emby-config-claim"}}]}}' \
  -- sleep 300 &

# Attendre que le pod soit ready
sleep 5
kubectl wait --for=condition=Ready pod -l run="$BUSYBOX_POD" -n media --timeout=30s || {
  echo "ERREUR : pod busybox non ready — avorter"
  kubectl delete pod "$BUSYBOX_POD" -n media 2>/dev/null || true
  exit 1
}

# Copier library.db via kubectl cp depuis le pod busybox
MSYS_NO_PATHCONV=1 kubectl cp "media/$BUSYBOX_POD:/config/data/library.db" "$BACKUP_DIR/library.db" 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp "media/$BUSYBOX_POD:/config/data/library.db-wal" "$BACKUP_DIR/library.db-wal" 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp "media/$BUSYBOX_POD:/config/data/library.db-shm" "$BACKUP_DIR/library.db-shm" 2>/dev/null || true

# Supprimer le pod busybox
kubectl delete pod "$BUSYBOX_POD" -n media 2>/dev/null || true

echo "Backup PROD : $BACKUP_DIR"
```

**Important** :
- Copie **à froid uniquement** (scale 0 + terminaison complète) → cohérence SQLite
- **Jamais** `kubectl rollout restart` — utiliser scale 0/wait/scale 1
- **sqlite3 absent du pod** : integrity_check non réalisable à chaud. Reprise manuelle en cas de KO base
- Chemin correct : `/config/data/library.db{,.wal,.shm}` (WAL mode)

## GARDE-FOU

`CLAUDE.md` : « Never deploy to `emby` (production) — only `emby2` ». Cette procedure ne s'execute que si
l'ordre de l'utilisateur, dans le message courant, demande explicitement le deploiement PROD **et** la version.
Sinon : STOP, remonter a `main` sans rien executer. Ne jamais l'enchainer automatiquement apres PUBLISH PROD.

```bash
# 1. Verification + recuperation de l'artefact publie (Release GitHub du tag)
export KUBECONFIG="${KUBECONFIG:-private/kubeconfig.yml}"
[ -n "$KUBE_CONTEXT" ] && kubectl config use-context "$KUBE_CONTEXT"
mkdir -p build/prod_v$VERSION
gh release download "v$VERSION" -R CCoupel/Emby_PlaySync -p EmbySharedPlaylist.dll -D "build/prod_v$VERSION" --clobber
ARTIFACT="build/prod_v$VERSION/EmbySharedPlaylist.dll"
EXPECTED_SIZE=$(stat -c %s "$ARTIFACT")

# 2. Install (memes regles critiques que deploy.qualif.md : MSYS_NO_PATHCONV, chemin relatif,
#    copie AVANT restart, destination /config/plugins/EmbySharedPlaylist.dll a la racine)
#    IMPORTANT : ne pas utiliser 'kubectl rollout restart' (peut laisser SQLite incohérent)
#    Utiliser scale 0/wait/scale 1 pour un arrêt propre de PROD
POD=$(kubectl get pods -n media --no-headers | grep -E '^emby-' | grep -v emby2 | awk '{print $1}')
echo "Pod PROD cible : $POD"   # controle visuel : ne doit pas contenir "emby2"
MSYS_NO_PATHCONV=1 kubectl exec -n media "$POD" -- cp /config/plugins/EmbySharedPlaylist.dll /config/plugins/EmbySharedPlaylist.dll.prev 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp "$ARTIFACT" media/$POD:/config/plugins/EmbySharedPlaylist.dll

# 3. Restart propre : scale 0/wait/scale 1 (jamais rollout restart)
kubectl scale deployment/emby -n media --replicas=0
sleep 5
kubectl wait --for=delete pod -l app=emby -n media --timeout=60s 2>/dev/null || true
kubectl scale deployment/emby -n media --replicas=1
kubectl rollout status deployment/emby -n media --timeout=120s
ROLLOUT_STATUS=$?

# 4. Verification dans le NOUVEAU pod
NEW_POD=$(kubectl get pods -n media --no-headers | grep -E '^emby-' | grep -v emby2 | awk '{print $1}')
ACTUAL_SIZE=$(MSYS_NO_PATHCONV=1 kubectl exec -n media "$NEW_POD" -- stat -c %s /config/plugins/EmbySharedPlaylist.dll)
[ "$ACTUAL_SIZE" = "$EXPECTED_SIZE" ] || ROLLOUT_STATUS=1
```

Variables : `KUBECONFIG` (defaut `private/kubeconfig.yml`), `KUBE_CONTEXT` optionnel. `gh` authentifie.

## Echec / Rollback

`kubectl rollout undo` est inoperant (DLL sur volume). Restaurer `EmbySharedPlaylist.dll.prev` puis
un redemarrage propre `scale --replicas=0` / attente de la suppression du pod / `scale --replicas=1`
(commandes de `deploy.qualif.md`, cible `emby`) — jamais `rollout restart` (voir ci-dessus).
Le tag/la Release restent tels quels sauf decision de `main` (voir Rollback de `publish.prod.md`).
