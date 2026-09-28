# Publish PROD — adaptations projet Emby_PlaySync

> Compagnon de `publish.prod.template.md` (mode `rebuild-ci`) : **prevaut** sur le template.
> La CI (`.github/workflows/release.yml`) se declenche sur le tag `vX.Y.Z` (3 champs, sans `a`),
> reconstruit avec `dotnet build -p:Version=X.Y.Z` et publie une GitHub Release contenant `dist/EmbySharedPlaylist.dll`.

Differences avec le template :

- **Version** : lire `<Version>` du csproj (pas `cat`), garder les 3 premiers champs : `VERSION=X.Y.Z`. La reecrire dans la balise `<Version>` et committer avant le tag.
- **CI** : `gh run watch` sur le workflow `Release`. Un echec est presque toujours CODE (compilation, tests) ou CONFIG.
- **Artefact publie** : `https://github.com/CCoupel/Emby_PlaySync/releases/tag/vX.Y.Z`, asset `EmbySharedPlaylist.dll`. C'est l'entree de `deploy.prod.md`.
- **Rollback** : identique au template, avec suppression de la Release creee (`gh release delete vX.Y.Z --yes`) en plus du tag.
