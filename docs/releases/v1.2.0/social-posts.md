# Posts v1.2.0 (non publiés)

## Twitter / X
🎚️ PlaySync v1.2.0 pour Emby : « Propager le lu » et « Propager l'avancement » deviennent deux réglages indépendants, et vous pouvez créer une playlist depuis la page PlaySync.
Attention : l'avancement est à réactiver à la main sur vos playlists.
#Emby #selfhosted

## LinkedIn
PlaySync v1.2.0 est disponible, le plugin Emby de playlists « À voir » partagées.

Jusqu'ici, un seul réglage gérait à la fois le marquage « lu » et la position de lecture. La v1.2.0 les sépare : trois interrupteurs indépendants dans la page PlaySync (Retirer si lu, Propager le lu, Propager l'avancement). Relire un média déjà vu propage désormais aussi la position. Et un bouton « Nouvelle playlist » permet de créer une playlist vide (nom unique, 10 playlists possédées maximum).

À connaître avant de mettre à jour :
- l'avancement n'est plus propagé tant que « Propager l'avancement » n'est pas activé, y compris sur les playlists qui avaient « Propager le lu » ;
- « Retirer si lu » n'agit que si « Propager le lu » est actif ;
- une position proche de 100 % ne marque pas le média « lu » chez les autres membres.

Aucune étiquette n'est modifiée automatiquement.
https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.0
#Emby #selfhosted #opensource

## Reddit (r/emby, r/selfhosted)
**[Release] PlaySync v1.2.0 — lu et avancement séparés, création de playlist**

Bonjour,

PlaySync v1.2.0 est publiée (plugin Emby de playlists « À voir » partagées).

**Ce qui change :**
- « Propager le lu » (flag lu) et « Propager l'avancement » (position de lecture) sont deux options indépendantes ; troisième interrupteur : « Retirer si lu ».
- Relire un média déjà lu propage bien la position (#57).
- Bouton « Nouvelle playlist » dans la page PlaySync : nom unique par propriétaire, quota de 10 playlists possédées (#55).

**Changements de comportement (pas de migration automatique) :**
- Playlists qui avaient « Propager le lu » : l'avancement est à réactiver via « Propager l'avancement ».
- « Retirer si lu » n'agit que si « Propager le lu » est actif.
- Une position ~100 % n'entraîne pas le marquage « lu ».

Release : https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.0
**Feedback bienvenu** dans les issues GitHub.
