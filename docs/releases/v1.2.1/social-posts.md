# Posts v1.2.1 (NON PUBLIÉS)

## Twitter / X
🔄 PlaySync v1.2.1 pour Emby : la position de lecture est maintenant synchronisée en continu chez les membres de la playlist, et une fin de lecture remet la position à 0 avec « Propager le lu ». Rien à reconfigurer.
#Emby #selfhosted

## LinkedIn
PlaySync v1.2.1 est disponible, le plugin Emby de playlists « À voir » partagées.

Cette version corrige un défaut de la v1.2.0 : lors d'une lecture sans pause, les autres membres ne voyaient pas la progression avant la toute fin.

Ce qui change :
- la position est propagée pendant la lecture, environ toutes les 10 secondes ; la pause et l'arrêt restent immédiats ;
- en fin de lecture, si « Propager le lu » est actif, la position repasse à 0 chez tous les membres : plus de « Reprendre à 99 % » sur un média déjà marqué lu.

Aucune action requise : remplacez le fichier du plugin et redémarrez Emby, vos playlists et réglages restent inchangés.
https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.1
#Emby #selfhosted #opensource

## Reddit (r/emby, r/selfhosted)
**[Release] PlaySync v1.2.1 — position synchronisée en continu, fin de lecture à 0**

Bonjour,

PlaySync v1.2.1 est publiée (plugin Emby de playlists « À voir » partagées). Correctif de la v1.2.0 (#58).

**Ce qui change :**
- La position de lecture est propagée aux autres membres pendant la lecture (au plus une fois par 10 s par couple lecteur/média), plus seulement à la pause ou à l'arrêt.
- Fin de lecture avec « Propager le lu » actif : position = 0 chez les membres, comme chez le lecteur.
- Diagnostics : compteurs `PositionProgress` à la place des entrées périodiques dans le journal.

**Mise à jour :** remplacer la DLL et redémarrer Emby, aucune reconfiguration.

Release : https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.1
**Feedback bienvenu** dans les issues GitHub.
