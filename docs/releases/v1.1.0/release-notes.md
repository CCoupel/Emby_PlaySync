# Release v1.1.0 - Gérez vos partages sans toucher aux étiquettes

**Date** : 2026-09-29

## Nouveautés

Jusqu'ici, partager une playlist « À voir » et régler ses options passait par l'édition manuelle
d'étiquettes sur la playlist — pratique quand on connaît le système, moins évident pour les
autres. La v1.1.0 ajoute une vraie page pour ça.

### Une page PlaySync dans votre menu Emby

Depuis le client web d'Emby, ouvrez votre avatar en haut à droite : une nouvelle entrée
**PlaySync** apparaît pour tout propriétaire de playlist partagée. Depuis cette page, en
quelques clics :

- **Ajoutez ou retirez des membres** de vos playlists partagées ;
- **Changez leur niveau d'accès** entre Lecture et Écriture ;
- **Activez ou désactivez** le retrait automatique des médias vus et la synchronisation de
  la progression de lecture, par un simple interrupteur — plus besoin de connaître le nom des
  étiquettes ni de les poser à la main.

La page s'affiche en français ou en anglais selon la langue de votre client Emby, et reste
invisible pour les comptes qui n'ont pas la permission de partage — rien ne change pour eux.

### Sous le capot

Cette nouvelle page s'appuie sur des points d'accès dédiés, distincts de l'administration Emby :
chaque propriétaire ne voit et ne modifie que ses propres playlists. Un audit de sécurité complet
a passé en revue ces nouveaux points d'accès (élévation de privilèges, accès à des données
d'autrui, script indésirable côté page) — aucun problème trouvé.

## Corrections

- **Format des réponses de l'API** : les options d'une playlist sont désormais renvoyées dans un
  format cohérent, ce qui fiabilise les outils qui s'y branchent.
- **Identifiants de compte invalides** : une erreur claire est renvoyée au lieu d'un message
  technique brut.
- **Intégrité en cas de retrait du dernier membre** : confirmée stable en conditions réelles.

## Comment mettre à jour

1. Téléchargez `EmbySharedPlaylist.dll` depuis la [page Releases GitHub](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.1.0).
2. Remplacez le fichier existant dans le dossier `plugins/` de votre serveur Emby (à la racine,
   pas dans un sous-dossier).
3. Redémarrez Emby Server.

Aucune action de configuration n'est requise — la page PlaySync apparaît automatiquement pour les
comptes autorisés au prochain chargement du menu.

## Liens

- [Documentation complète](https://github.com/CCoupel/Emby_PlaySync/blob/main/docs/chronogrammes.md)
- [GitHub Release v1.1.0](https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.1.0)
- [Site PlaySync](https://ccoupel.github.io/Emby_PlaySync/)
