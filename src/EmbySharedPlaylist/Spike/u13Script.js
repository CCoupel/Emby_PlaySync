/* global ApiClient */
/* SONDE TEMPORAIRE U13 (#39, branche spike/u13, JAMAIS mergée). Appelle /SharedPlaylist/SpikeU13/* en tant que
   compte connecté (pas d'en-tête particulier : ApiClient ajoute le token de session comme pour tout autre appel —
   c'est justement ce qui est sondé, question a/d). Résultat brut affiché tel quel (JSON), aucune mise en forme :
   page de diagnostic, pas la page produit finale (Phase 3). */
define([], function () {
    'use strict';

    return function (view) {

        function show(obj) {
            view.querySelector('#Result').textContent = JSON.stringify(obj, null, 2);
        }

        function showError(err) {
            view.querySelector('#Result').textContent = 'ERREUR : ' + (err && err.status ? err.status + ' ' : '') + JSON.stringify(err);
        }

        function call(method, path, params, body) {
            var url = ApiClient.getUrl(path, params || {});
            var options = { type: method, url: url, dataType: 'json' };
            if (body !== undefined) {
                options.data = JSON.stringify(body);
                options.contentType = 'application/json';
            }
            return ApiClient.ajax(options).then(show, showError);
        }

        function playlistName() { return view.querySelector('#PlaylistName').value; }

        view.querySelector('#btnWhoami').addEventListener('click', function () {
            call('GET', 'SharedPlaylist/SpikeU13/Whoami');
        });

        view.querySelector('#btnShares').addEventListener('click', function () {
            call('GET', 'SharedPlaylist/SpikeU13/Shares', { PlaylistName: playlistName() });
        });

        view.querySelector('#btnShare').addEventListener('click', function () {
            call('POST', 'SharedPlaylist/SpikeU13/Share', {}, {
                PlaylistName: playlistName(),
                TargetUserId: view.querySelector('#TargetUserId').value,
                Level: view.querySelector('#Level').value
            });
        });

        view.querySelector('#btnDeleteAll').addEventListener('click', function () {
            call('POST', 'SharedPlaylist/SpikeU13/DeleteShares', {}, {
                PlaylistName: playlistName(),
                MaxShareLevel: null
            });
        });
    };
});
