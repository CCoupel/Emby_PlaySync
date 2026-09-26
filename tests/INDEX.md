# Index des tests

> Tests de specification ecrits par le test-writer ; statuts tenus par le CDP.
> Convention : `context/COMMON.md` section 15. Statuts : `feature` | `regression` | `quarantaine`.
> Tags : `smoke`, `critical`, `slow`.

| Fichier | Niveau | Composant | Feature | Statut | Tags |
|---------|--------|-----------|---------|--------|------|
| tests/spike/lib.sh | integration (support) | plugin | spike-partage-natif #1 (fonctions communes, cible QUALIF vérifiée) | feature | |
| tests/spike/00-setup-users.sh | integration | plugin | spike-partage-natif #1 (comptes test_u1/u2/u3, comptes réels inchangés) | feature | |
| tests/spike/10-run-spike.sh | integration | plugin | spike-partage-natif #1 (U1–U6, U10 avancement #44 + règles propager-lu ; smoke = GET Spike/Shares) | feature | smoke |
| tests/spike/90-cleanup.sh | integration | plugin | spike-partage-natif #1 (nettoyage, comptes protégés vérifiés) | feature | |
| tests/spike/MANUAL.md | manuel | plugin | spike-partage-natif #1 (TV/mobile, lecture réelle, étiquettes, avancement #44) | feature | |
| tests/spike/test-lib-offline.sh | unit (hors ligne) | plugin | spike-partage-natif #1 (lib.sh : échappement config curl, en-tête de login intact ; serveur local, sans emby2) | regression | smoke |
