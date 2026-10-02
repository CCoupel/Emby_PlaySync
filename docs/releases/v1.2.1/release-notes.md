# Release v1.2.1 - Synchronisation continue de la position, correction fin de lecture

**Date** : 2026-10-02

## Correction majeure : position synchronisée en continu

### Symptôme corrigé

Avec v1.2.0, si vous commenciez une vidéo sur un compte et la regardiez sans pause jusqu'à la fin, l'autre compte (membre) ne voyait **jamais** votre progression — la position n'était mise à jour que si vous mettiez en pause ou arrêtiez la vidéo manuellement.

**Exemple du bug** :
- U1 (propriétaire) lance une vidéo de 1 h sans jamais la pauser.
- U2 (membre) a « Propager l'avancement » activé, mais... rien ne s'affiche : la position de U1 ne s'actualise jamais chez U2.
- Quand U1 termine la vidéo, une seule mise à jour tardive arrive chez U2, qui a raté tout le film en direct.

### Comment c'est corrigé en v1.2.1

L'avancement est maintenant propagé **en continu pendant la lecture** :
- À chaque `PlaybackProgress` d'Emby (tous les ~10 secondes), la position est mise à jour chez les autres membres.
- **Au maximum une fois par 10 s par couple** (lecteur + média) : aucune surcharge.
- La pause et l'arrêt restent immédiats (priorité maximale).

**Exemple avec v1.2.1** :
- U1 lance la vidéo, puis U2 (membre) voit la position de U1 s'actualiser toutes les 10 s environ en temps réel.
- U1 met en pause : U2 voit immédiatement (propagation immédiate à la pause).
- U1 reprend : la mise à jour toutes les 10 s reprend.

### Fin de lecture : position 0 si « Propager le lu »

**Avant (v1.2.0)** : quand une vidéo se terminait naturellement sans pause, Emby remet la position à 0 chez le lecteur, mais le plugin n'avait **aucune règle explicite** pour les autres membres. Ils restaient bloqués à ~99 % (ou la position du moment du bug).

**Après (v1.2.1)** : quand une vidéo se termine et que « Propager le lu » est aussi activé, la position devient **0** chez tous les membres — miroir exact de l'état du lecteur. Vous ne verrez plus « Reprendre à 99 % » après qu'elle ait été marquée « lu ».

**Note** : si vous avez juste « Propager l'avancement » (sans « Propager le lu »), la position d'arrêt brute (~99–100 %) est propagée ; seul « Propager le lu » ajoute aussi le marquage « lu » et la remise à 0.

## Améliorations de diagnostic

- Nouvelles clés `Diagnostics/State.PositionProgress` : compteurs des mises à jour périodiques (`Propagated`, `Throttled` si verrouillé, `LockBusy`).
- Le journal diagnostics n'est plus saturé par les centaines d'entrées `PositionPropagation` périodiques (compteurs à la place).
- `Diagnostics/State.Handler` ne biaise plus ses moyennes et maximales en comptant les propagations périodiques (affichage vraiment représentatif des opérations discrètes).

## Aucun changement pour vous

Aucune action requise : les trois options restent les mêmes sur la page PlaySync, les playlists n'ont pas besoin d'être reconfigurées. C'est une amélioration sous le capot.

## Limitations connues

Deux problèmes pré-existants (hors périmètre de cette correction) sont documentés en issues ouvertes :
- **#59** : rare écart de compteur dans les diagnostics (1–2 entrées décalées après retrait).
- **#60** : test intermittent lors de l'enchaînement des scripts (aucun impact opérationnel).

## Comment mettre à jour

1. Téléchargez `EmbySharedPlaylist.dll` depuis la [page Releases GitHub](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.1).
2. Remplacez le fichier existant dans le dossier `plugins/` de votre serveur Emby (à la racine, pas dans un sous-dossier).
3. Redémarrez Emby Server.

Vos playlists et configurations restent inchangées. La synchronisation continue commence immédiatement.

## Liens

- [Documentation complète](https://github.com/CCoupel/Emby_PlaySync/blob/main/README.md)
- [Spécification technique](https://github.com/CCoupel/Emby_PlaySync/blob/main/docs/chronogrammes.md)
- [GitHub Release v1.2.1](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.1)
- [Issue #58 (bugfix)](https://github.com/CCoupel/Emby_PlaySync/issues/58)
