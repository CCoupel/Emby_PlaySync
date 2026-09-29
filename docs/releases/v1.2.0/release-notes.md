# Release v1.2.0 - Trois options de synchronisation, création de playlists

**Date** : 2026-09-29

## Nouveautés

### Trois options au lieu de deux

Jusqu'à v1.1.0, « Propager le lu » couvrait à la fois le flag *lu* et l'avancement de lecture. La v1.2.0 les sépare en deux options indépendantes :

- **Propager le lu** : le flag *lu* seul. Quand un membre termine un média, il est marqué lu chez les autres.
- **Propager l'avancement** (nouvelle) : la position de lecture seule. À la pause ou l'arrêt d'une lecture (≥ 30 s), la position est copiée chez les autres — commencez sur un compte, poursuivez sur l'autre.
- **Retirer si lu** : quand un média passe à « lu », il est retiré de la liste pour tous — **maintenant subordonné à « Propager le lu »** (voir ci-dessous).

Chaque option fonctionne indépendamment. Par exemple, vous pouvez maintenant propager juste la position sans marquer lu chez les autres, ou réciproquement.

### Dépendance « Retirer si lu » ← « Propager le lu »

Depuis v1.2.0, l'option « Retirer si lu » n'a d'effet que si « Propager le lu » est aussi activée. Cela signifie :

- Si vous aviez « Retirer si lu » activé **sans** « Propager le lu », le retrait ne fonctionne plus — il faut maintenant activer « Propager le lu ».
- Si vous aviez juste « Propager le lu » (sans retrait), la propagation du flag continue, et l'avancement devient indépendant (activez « Propager l'avancement » si vous le voulez).

Sur la page PlaySync, l'interrupteur « Retirer si lu » est grisé tant que « Propager le lu » n'est pas cochée, avec une mention explicite. Ils restent indépendants une fois activés — vous pouvez décocher « Propager le lu » et laisser « Retirer si lu » en place (aucun effet tant que « Propager le lu » reste décochée).

### Relecture avec synchronisation (issue #57)

La position est désormais propagée **même lors de la relecture d'un média déjà lu**. Par exemple :
- U1 et U2 terminent ensemble un film, tous deux le marquent lu.
- Plus tard, U1 le relit : sa position est propagée à U2 en temps réel.
- U2 voit « Reprendre à… » et peut regarder d'où U1 a laissé, sans que U1 ne marque le film « en cours de lecture » ni « non lu ».

### Créer une playlist depuis PlaySync

Bouton « Nouvelle playlist » dans la page PlaySync : créez directement une playlist vide sans passer par l'interface Emby. Noms de 1 à 100 caractères, unique par propriétaire (insensible à la casse). Quota : 10 playlists possédées (comptées sur toutes vos playlists Emby).

## Changements de comportement

### ⚠️ Si vous migrez depuis v1.1.0

Si vous aviez des playlists avec certains paramètres, lire ceci :

1. **Playlist avec « Propager le lu » seule (sans retrait)** → Pas de changement. La propagation du flag lu continue. L'avancement ne se propage plus : pour le retrouver, activez « Propager l'avancement ».

2. **Playlist avec « Retirer si lu » seule (sans « Propager le lu »)** → Le retrait **s'arrête**. Aucune étiquette n'est modifiée. Pour restaurer le retrait, activez « Propager le lu ».

3. **Playlist avec les deux activées** → Aucun changement immédiat. Le retrait et la propagation du flag continuent. L'avancement devient indépendant : activez « Propager l'avancement » si souhaité.

4. **Position ~99 % écrite par le plugin** → Le média **ne sera pas marqué « lu »** chez les autres membres (c'est le rôle de « Propager le lu »). Vous verrez « Reprendre à 99 % » sans que le film soit marqué comme commencé. Activez « Propager le lu » si vous voulez aussi le flag.

**Aucune migration automatique** : les étiquettes restent inchangées, simplement réinterprétées selon les nouvelles règles.

## Corrections et améliorations

- **Anti-écho amélioré** : écritures concurrentes du flag lu et de la position sérialisées par un verrou partagé, garantissant qu'aucune mise à jour n'est perdue.
- **Message d'aide mis à jour** : les descriptions vides des playlists gérées reçoivent la version v3 du message, mentionnant les trois options. Les versions v1 et v2 détectées sont automatiquement remplacées.
- **Audit de sécurité passé** : création de playlist et nouvelles options auditées pour injection, IDOR, élévation de droits. Aucun problème critique trouvé.

## Comment mettre à jour

1. Téléchargez `EmbySharedPlaylist.dll` depuis la [page Releases GitHub](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.0).
2. Remplacez le fichier existant dans le dossier `plugins/` de votre serveur Emby (à la racine, pas dans un sous-dossier).
3. Redémarrez Emby Server.

Vos playlists existantes restent intactes et continuent de fonctionner. Ouvrez la page PlaySync (Avatar → PlaySync) pour voir les trois interrupteurs et vous adapter aux changements décrits ci-dessus.

## Liens

- [Documentation complète](https://github.com/CCoupel/Emby_PlaySync/blob/main/README.md)
- [Spécification technique](https://github.com/CCoupel/Emby_PlaySync/blob/main/docs/chronogrammes.md)
- [GitHub Release v1.2.0](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.0)
