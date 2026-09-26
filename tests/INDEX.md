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
| tests/integration/05-reentrancy-probe.sh | integration | plugin | sonde U11 de ré-entrance (#52) : P1–P6, échos, latence p50/p95/max, logs Emby, décision immédiat/repli | feature | critical |
| tests/integration/probe-lib.sh | integration (support) | plugin | sonde U11 (#52) : parsing des événements Probe, statistiques, analyse des logs, décision | feature | |
| tests/integration/test-probe-offline.sh | unit (hors ligne) | plugin | sonde U11 (#52) : fonctions pures de probe-lib.sh | feature | smoke |
| tests/integration/test-probe-flow-offline.sh | integration (hors ligne, faux Emby) | plugin | sonde U11 (#52) : flux complet de 05-reentrancy-probe.sh contre fake_emby.py, 4 situations | feature | smoke |
| tests/integration/fake_emby.py | integration (support) | plugin | faux serveur Emby local pour test-probe-flow-offline.sh | feature | |
| tests/integration/20-etiquettes-retrait.sh | integration | plugin | v0.2.0 : scénarios I0–I17 (I5 : tous les doublons retirés sur une transition) (étiquettes remove-si-lu/propager-lu, retrait à la transition, grâce, première détection, concurrence, ré-entrance, latence) ; option --restart pour I10 | feature | smoke, critical |
| tests/integration/int-lib.sh | integration (support) | plugin | v0.2.0 : helpers Diagnostics/tâche planifiée/étiquettes/lecture simulée pour 20-etiquettes-retrait.sh | feature | |
| tests/integration/test-int-flow-offline.sh | integration (hors ligne, faux moteur) | plugin | v0.2.0 : flux de 20-etiquettes-retrait.sh contre fake_emby2.py, 5 situations dont moteur défaillant | feature | smoke |
| tests/integration/fake_emby2.py | integration (support) | plugin | faux Emby + faux moteur v0.2.0 (spécification exécutable minimale) | feature | |
| tests/integration/MANUAL.md | manuel | plugin | v0.2.0 : procédure utilisateur (remplacement NON->OUI, retrait, relecture, décoche/recoche, message d'aide, TV/mobile, file de lecture) | feature | |
| tests/EmbySharedPlaylist.Tests/PlayedTransitionTrackerSpecTests.cs | unit | plugin | v0.2.0 #12/#16 : spécification de la transition non lu -> lu (Q1, TogglePlayed, Progress/Finished, casse, indépendance, capacité, concurrence) | feature | |
| tests/EmbySharedPlaylist.Tests/ReadRemovalEngineSpecTests.cs | unit | plugin | v0.2.0 #12/#16 : spécification du retrait du média lu, chaîne tracker+moteur (legacy x4 + propager-lu, Q1, doublons, membres Q3, non partagée, verrou, écho, ne lève jamais, suspension) | feature | critical |
