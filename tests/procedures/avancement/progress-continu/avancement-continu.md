# Procédure de Test — Avancement synchronisé en continu et fin de lecture (bugfix #58, D23)

**Version** : 1.2.1
**Date** : 2026-09-30
**Testeur** : QA

Corrige : « lecture continue sans pause + `remove-si-lu`=OUI + `propager-lu`=OUI → le membre est marqué lu mais garde une
position périmée ». Spécification : `docs/chronogrammes.md` (R10, D23, S9g) ; conception : `docs/mockup/v1.2.1/conception/position-sync__progress-continu.md` ;
contrats : `contracts/http-endpoints.md` (`PositionPropagation`, `trigger=`, `Diagnostics/State.PositionProgress`), `contracts/CHANGELOG.md` [20260930].
Automatisation équivalente : `tests/integration/22-avancement.sh` I35, I41–I44 (API synthétique) — **cette procédure est la vérification
en conditions réelles** (client Emby Web) que l'API synthétique ne remplace pas (CA1, CA4 ; spike U15 : `PlayedToCompletion`).

## Prérequis

- [ ] Environnement : **QUALIF** (`emby2`), plugin v1.2.1.x déployé ; jamais PROD
- [ ] Données : comptes `test_u1` (propriétaire), `test_u2` (Write) — `tests/integration/00-setup-users.sh` exécuté ; un média **court**
      de test (≈ 2–3 min, pour atteindre la fin sans attendre) présent dans la bibliothèque, non lu et à la position 0 pour les deux comptes
- [ ] Deux navigateurs (ou un profil privé) : session `test_u1` et session `test_u2` (client Emby Web)
- [ ] Accès : DevTools/`/SharedPlaylist/Diagnostics/State` et `/Journal` (clé API admin) pour relever les compteurs et le journal
- [ ] Préparer, en tant que `test_u1`, une playlist « SPIKE-PROC-58 » contenant le média, partagée avec `test_u2` (Write) ; la page PlaySync
      doit afficher les trois interrupteurs. Relever `Diagnostics/State.PositionProgress` (valeurs de départ : P0/T0/L0)
- [ ] Vider le journal : `GET /SharedPlaylist/Diagnostics/Journal?clear=true`

## Scénarios

### Scénario 1 — Lecture continue sans pause : le membre suit l'avancement (CA1, CA3, CA8)

**Objectif** : vérifier que `test_u1` (membre) voit la position de `test_u2` avancer pendant la lecture, sans que `test_u2` ne mette en pause ni n'arrête.
Réglages : « Propager l'avancement » = OUI ; « Propager le lu » = NON ; « Retirer si lu » = NON.

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | La description de « Propager l'avancement » (page PlaySync FR) est lue | « … pendant la lecture (environ toutes les 10 s), à la pause et à l'arrêt, même pour un média déjà vu. Ne marque jamais un média comme « lu ». » (EN : « during playback (about every 10 s), on pause and on stop … ») | | |
| 2 | `test_u2` lance le média et le lit **sans toucher aux commandes** pendant ≥ 60 s | La lecture est fluide (aucune saccade due au plugin) | | |
| 3 | Après ~35 s puis ~45 s de lecture, `test_u1` consulte la fiche du média (rafraîchir la page) | La position affichée (« Reprendre à … ») de `test_u1` suit celle de `test_u2` avec **au plus ~20 s de retard**, et **avance** entre les deux relevés | | |
| 4 | Relever `Diagnostics/State.PositionProgress` | `Propagated` a augmenté d'au moins 3 depuis P0 ; `Throttled` a augmenté (Emby rapporte plus souvent que toutes les 10 s) ; `LockBusy` peu ou pas | | |
| 5 | Lire `Diagnostics/Journal?kind=PositionPropagation` | **Aucune** entrée pour les Progress périodiques (pas de bruit dans le journal) | | |

**Verdict** : [ ] PASS  [ ] FAIL

---

### Scénario 2 — Pause immédiate et heartbeat en pause (CA2, CA8)

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | Dans la lecture du scénario 1, `test_u2` met en **pause** (≥ 30 s de lecture) | `test_u1` voit la position de la pause **tout de suite** (sans attendre 10 s) | | |
| 2 | Journal `PositionPropagation` | Une entrée dont le `Detail` **se termine par** `trigger=pause` | | |
| 3 | `test_u2` reste en pause ≥ 30 s (le client envoie des heartbeats) puis consulter `test_u1` | La position de `test_u1` **ne change pas** pendant la pause ; aucune nouvelle entrée de journal | | |
| 4 | `test_u2` reprend puis **arrête** en cours de média (pas à la fin) | Position d'arrêt propagée ; entrée `trigger=stop` ; `test_u1` non marqué lu | | |

**Verdict** : [ ] PASS  [ ] FAIL

---

### Scénario 3 — LE CAS DU BUG : lecture continue jusqu'au bout avec « Propager le lu » et « Retirer si lu » (CA4, S9g)

**Objectif** : à la fin naturelle du média, `test_u1` est **lu ET sans point de reprise**. Réglages : « Propager l'avancement » = OUI ; « Propager le lu » = OUI ; « Retirer si lu » = OUI.
Réinitialiser d'abord le média (non lu, position 0) pour les deux comptes et le rajouter à la playlist s'il en a été retiré.

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | `test_u2` lit le média **du début à la fin, sans pause** (laisser le générique/écran de fin) | Pendant la lecture, `test_u1` suit la position (comme scénario 1) | | |
| 2 | À la fin : consulter la fiche du média chez `test_u1` | Le média est **marqué lu** chez `test_u1` **et** ne propose **aucun « Reprendre »** (position 0) | | |
| 3 | Consulter la playlist « SPIKE-PROC-58 » | Le média en a été **retiré** (remove-si-lu) | | |
| 4 | Journal `PositionPropagation` | Une entrée dont le `Detail` se termine par `trigger=completion` (malgré le retrait du média avant l'arrêt) | | |
| 5 | Journal : `Removal` et `Propagation` (flux du lu) | Présents et inchangés (aucune régression du flux du lu) | | |
| 6 | **Spike U15** : relever dans les logs Emby/plugin (niveau Debug) l'ordre `UserDataSaved PlaybackFinished` puis `PlaybackStopped` et la valeur de `PlayedToCompletion` | Ordre conforme au plan §2 ; `PlayedToCompletion=true`. **Si false** : noter le client et signaler (le repli sur l'état UserData du déclencheur est prévu) | | |

**Verdict** : [ ] PASS  [ ] FAIL

---

### Scénario 4 — Fin de lecture sans « Propager le lu » (CA5, S9f inchangé)

Réglages : « Propager l'avancement » = OUI ; « Propager le lu » = NON. Réinitialiser le média.

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | `test_u2` lit jusqu'au bout | `test_u1` suit pendant la lecture | | |
| 2 | Fin de lecture, fiche du média chez `test_u1` | **Non marqué lu par le plugin** ; position = position d'arrêt (« Reprendre à ~99 % ») — observer et noter si Emby pose lui-même « lu » (spike U14b, observation, non bloquant) | | |
| 3 | Journal | Entrée `PositionPropagation` avec `trigger=completion` | | |

**Verdict** : [ ] PASS  [ ] FAIL

---

### Scénario 5 — Aucun écho parasite (CA7, S6/S7)

Prérequis : `test_u1` est aussi membre d'une **seconde** playlist « SPIKE-PROC-58-B » (avec `test_u3`) contenant le même média, avec « Retirer si lu » = OUI et « Propager le lu » = OUI, « Propager l'avancement » = NON.

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | Refaire une lecture continue de `test_u2` (≥ 60 s) sur la première playlist (avancement OUI) | `test_u1` suit | | |
| 2 | Vérifier la playlist « SPIKE-PROC-58-B » | Le média **y est toujours** ; aucun lu propagé à `test_u3` ; pas de retrait parasite (les écritures du plugin ne sont pas prises pour une action de `test_u1`) | | |
| 3 | `Diagnostics/State.SkippedCounts["echo-consumed"]` | Augmente (les échos des écritures du plugin sont consommés) | | |

**Verdict** : [ ] PASS  [ ] FAIL

---

### Scénario 6 — Relecture (nouvelle session) et Progress tardif (CA6, S9d)

| Etape | Action | Résultat Attendu | Résultat Obtenu | OK ? |
|-------|--------|-----------------|----------------|------|
| 1 | Après le scénario 3, `test_u2` **relance** le même média (déjà lu) et lit ≥ 60 s | La position de `test_u2` est de nouveau propagée en continu à `test_u1` (nouvelle session de lecture) | | |
| 2 | `test_u2` arrête en fin de média puis, immédiatement après, ferme/rouvre la page de lecture | La position finale de `test_u1` reste celle de la règle de fin (0 si propager-lu=OUI) : **jamais ré-écrasée** par une valeur ~99 % | | |

**Verdict** : [ ] PASS  [ ] FAIL

## Critères de Validation

- [ ] Scénarios 1, 2 et 3 (CA1–CA4, CA8) : PASS — le scénario 3 est le cas du bug et est **bloquant**
- [ ] Le journal n'est jamais saturé par les Progress périodiques ; `trigger=` présent sur pause/stop/completion
- [ ] Aucune régression : propagation du lu (R4b) et retrait (R4a) inchangés ; PlaySync affiche la nouvelle description
- [ ] Spike U15 documenté (ordre des événements, `PlayedToCompletion`, cadence des Progress d'Emby Web)
- [ ] Charge : `Diagnostics/State.Handler` — durée max du gestionnaire ≤ 2 s (p95 attendu ≤ 300 ms) pendant une lecture de 5 min

## Notes QA

[Espace pour observations : client utilisé (Web/TV/mobile), cadence réelle des Progress, valeur de `PlayedToCompletion`, anomalies]
