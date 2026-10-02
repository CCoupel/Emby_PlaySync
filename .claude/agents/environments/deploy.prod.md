# Deploy PROD — adaptations projet Emby_PlaySync

> Compagnon de `deploy.prod.template.md` : **remplace** la procedure Helm generique. Meme mecanique que
> `deploy.qualif.md` (copie du DLL dans `/config/plugins`, restart, verification de taille) mais :
> cible `deployment/emby` (namespace `media`) et artefact = asset de la GitHub Release (BORE : jamais
> le DLL local de QUALIF, la CI a reconstruit depuis le tag).

## PRE-DEPLOYMENT — Sauvegarde de `library.db`

**Avant tout déploiement**, sauvegarder la base de données Emby de PROD pour la cohérence et le rollback :

```bash
# 1. Scale à 0 et attendre la terminaison complète du pod
kubectl scale deployment/emby -n media --replicas=0
sleep 5
kubectl wait --for=delete pod -l app=emby -n media --timeout=60s 2>/dev/null || true

# 2. Copie à froid (base complètement stoppée)
POD=$(kubectl get pods -n media --no-headers | grep -E '^emby-' | grep -v emby2 | head -1)
[ -n "$POD" ] && kubectl exec -n media "$POD" -- ls /config/library.db >/dev/null 2>&1 || {
  echo "ERREUR : pod PROD non accessible après scale 0 — avorter et investiguer"
  exit 1
}

# Créer le répertoire de backup
mkdir -p backups/prod
BACKUP_DIR="backups/prod/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"

# Copier library.db, .db-wal, .db-shm (WAL mode)
MSYS_NO_PATHCONV=1 kubectl cp media/$POD:/config/library.db "$BACKUP_DIR/library.db" 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp media/$POD:/config/library.db-wal "$BACKUP_DIR/library.db-wal" 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp media/$POD:/config/library.db-shm "$BACKUP_DIR/library.db-shm" 2>/dev/null || true

echo "Backup PROD : $BACKUP_DIR"
```

**Important** : ne jamais copier `library.db` pendant qu'Emby est en fonctionnement — seule une copie à froid (après scale 0) garantit la cohérence. `kubectl rollout restart` ne suffit pas : il faut scale à 0, attendre la terminaison, puis sauvegarder.

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
POD=$(kubectl get pods -n media --no-headers | grep -E '^emby-' | grep -v emby2 | awk '{print $1}')
echo "Pod PROD cible : $POD"   # controle visuel : ne doit pas contenir "emby2"
MSYS_NO_PATHCONV=1 kubectl exec -n media "$POD" -- cp /config/plugins/EmbySharedPlaylist.dll /config/plugins/EmbySharedPlaylist.dll.prev 2>/dev/null || true
MSYS_NO_PATHCONV=1 kubectl cp "$ARTIFACT" media/$POD:/config/plugins/EmbySharedPlaylist.dll
kubectl rollout restart deployment/emby -n media

# 3. Rollout + verification dans le NOUVEAU pod
kubectl rollout status deployment/emby -n media --timeout=120s
ROLLOUT_STATUS=$?
NEW_POD=$(kubectl get pods -n media --no-headers | grep -E '^emby-' | grep -v emby2 | awk '{print $1}')
ACTUAL_SIZE=$(MSYS_NO_PATHCONV=1 kubectl exec -n media "$NEW_POD" -- stat -c %s /config/plugins/EmbySharedPlaylist.dll)
[ "$ACTUAL_SIZE" = "$EXPECTED_SIZE" ] || ROLLOUT_STATUS=1
```

Variables : `KUBECONFIG` (defaut `private/kubeconfig.yml`), `KUBE_CONTEXT` optionnel. `gh` authentifie.

## Echec / Rollback

`kubectl rollout undo` est inoperant (DLL sur volume). Restaurer `EmbySharedPlaylist.dll.prev` puis
`kubectl rollout restart deployment/emby -n media` (commandes de `deploy.qualif.md`, cible `emby`).
Le tag/la Release restent tels quels sauf decision de `main` (voir Rollback de `publish.prod.md`).
