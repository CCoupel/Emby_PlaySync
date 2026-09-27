# Index des tests

> Tests de specification ecrits par le test-writer ; statuts tenus par le CDP.
> Convention : `context/COMMON.md` section 15. Statuts : `feature` | `regression` | `quarantaine`.
> Tags : `smoke`, `critical`, `slow`.

| Fichier | Niveau | Composant | Feature | Statut | Tags |
|---------|--------|-----------|---------|--------|------|
| tests/integration/lib.sh | integration (support) | plugin | fonctions communes (repris de tests/spike/lib.sh v0.1.0, supprimé avec #15), cible QUALIF vérifiée | feature | |
| tests/integration/00-setup-users.sh | integration | plugin | comptes test_u1/u2/u3, comptes réels inchangés (repris de tests/spike/00-setup-users.sh, supprimé avec #15) | feature | |
| tests/integration/90-cleanup.sh | integration | plugin | nettoyage playlists SPIKE*/comptes test_*, comptes protégés vérifiés (repris de tests/spike/90-cleanup.sh, supprimé avec #15) | feature | |
| tests/integration/test-lib-offline.sh | unit (hors ligne) | plugin | lib.sh : échappement config curl, en-tête de login intact, compare_protected, need_int, normalisation Diagnostics/* ; serveur local, sans emby2 (repris de tests/spike/test-lib-offline.sh) | regression | smoke |
| tests/integration/20-etiquettes-retrait.sh | integration | plugin | v0.2.0 : scénarios I0–I17 (I5 : tous les doublons retirés sur une transition) (étiquettes remove-si-lu/propager-lu, retrait à la transition, grâce, première détection, concurrence, ré-entrance, latence) ; option --restart pour I10 | feature | smoke, critical |
| tests/integration/int-lib.sh | integration (support) | plugin | v0.2.0 : helpers Diagnostics/tâche planifiée/étiquettes/lecture simulée pour 20-etiquettes-retrait.sh | feature | |
| tests/integration/test-int-flow-offline.sh | integration (hors ligne, faux moteur) | plugin | v0.2.0 : flux de 20-etiquettes-retrait.sh contre fake_emby2.py, 5 situations dont moteur défaillant | feature | smoke |
| tests/integration/fake_emby2.py | integration (support) | plugin | faux Emby + faux moteur v0.2.0/v0.3.0 (retrait, propagation, R7/R8, message d'aide #51 : spécification exécutable minimale) | feature | |
| tests/integration/21-propagation.sh | integration | plugin | v0.3.0 : scénarios I18–I26 (matrice 2×2, R6/R7/R8, S6a-c non-transitivité, S7 anti-écho, #51 message d'aide, régression I0–I17) | feature | smoke, critical |
| tests/integration/test-propagation-flow-offline.sh | integration (hors ligne, faux moteur) | plugin | v0.3.0 : flux de 21-propagation.sh contre fake_emby2.py, 5 situations (moteur sans retrait, sans propagation, régression, compte R8 créé/nettoyé dans le run) | feature | smoke |
| tests/integration/MANUAL.md | manuel | plugin | v0.2.0/v0.3.0 : procédure utilisateur (remplacement NON->OUI, retrait, propagation du lu, relecture, décoche/recoche, message d'aide #51, TV/mobile, file de lecture) | feature | |
| tests/EmbySharedPlaylist.Tests/PlayedTransitionTrackerSpecTests.cs | unit | plugin | v0.2.0 #12/#16 : spécification de la transition non lu -> lu (Q1, TogglePlayed, Progress/Finished, casse, indépendance, capacité, concurrence) | feature | |
| tests/EmbySharedPlaylist.Tests/ReadRemovalEngineSpecTests.cs | unit | plugin | v0.2.0 #12/#16 : spécification du retrait du média lu, chaîne tracker+moteur (legacy x4 + propager-lu, Q1, doublons, membres Q3, non partagée, verrou, écho, ne lève jamais) | feature | critical |
| tests/EmbySharedPlaylist.Tests/EngineBudgetAndCountersSpecTests.cs | unit | plugin | v0.2.0 #12/#14/#16 : budget global du gestionnaire (budget-exceeded, reprise idempotente) ; compteurs de Skipped agrégés (SkippedCounts) et « un écho par écriture » | feature | |
| tests/EmbySharedPlaylist.Tests/PropagationChainSpecTests.cs | unit | plugin | v0.3.0 #20/#21 : propagation à travers la CHAÎNE RÉELLE (PlaybackEventProcessor+WriteTracker+Engine) ; absence de transitivité S6a (écho consommé, contre-épreuve par appel direct au moteur), R7/R8/Q1 via la même chaîne | feature | critical |
