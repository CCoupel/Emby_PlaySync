/* global ApiClient */
/*
 * Page utilisateur PlaySync (D20, v1.1.0, #39). Maquette validée : docs/mockup/v1.1.0/ui/user-page__b39.html.
 * Contrat : contracts/http-endpoints.md § « Page utilisateur ». Règles :
 * - Composants Emby natifs uniquement (emby-select, emby-button, emby-toggle) ; dialogue de confirmation et toast
 *   d'erreur via les modules Emby (require(['confirm'|'toast'], ...)) — jamais de boîte de dialogue bloquante du
 *   navigateur (ni pour confirmer, ni pour alerter).
 * - Aucune donnée serveur (nom de compte, nom de playlist) insérée autrement qu'en textContent — jamais de HTML
 *   brut assigné directement à un élément.
 * - Codes d'erreur serveur (stables, non localisés) traduits UNIQUEMENT via le dictionnaire local (errorKey) —
 *   jamais le code brut inséré dans le DOM.
 * - Après chaque écriture : rechargement complet depuis le serveur (réponse serveur = source de vérité), jamais
 *   d'optimisme local sur l'état affiché.
 * - Entrée de menu visible pour tous (Q6b, GATE U13 : aucun masquage natif par permission) : l'absence de
 *   permission est détectée au premier appel (403 sharing-disabled) et affiche l'état "denied".
 */
define([], function () {
    'use strict';

    var FAMILIES = ['remove-si-lu', 'propager-lu'];

    var STRINGS = {
        fr: {
            title: "PlaySync",
            intro: "Vos playlists — partagez-les avec d'autres comptes et réglez leur comportement. Seules les playlists dont vous êtes propriétaire apparaissent ici.",
            denied: "Le partage de contenus personnels n'est pas activé pour votre compte. Contactez l'administrateur du serveur.",
            empty: "Vous ne possédez aucune playlist. Ajoutez un média à une nouvelle playlist, puis revenez ici pour la partager.",
            shared: "Partagée",
            unshared: "Non partagée",
            members: "membres",
            member: "membre",
            items: "médias",
            optionsHeader: "Options",
            removeOnPlayedTitle: "Retirer un média dès qu'il est lu",
            removeOnPlayedDesc: "Quand un membre passe un média à lu, il est retiré de la playlist pour tous.",
            propagateTitle: "Partager l'état de lecture",
            propagateDesc: "Le lu et la position de lecture sont recopiés chez les autres membres.",
            optionsUnavailable: "Disponibles une fois la playlist partagée.",
            conflict: "Étiquettes en conflit, option inactive. Le prochain changement corrige l'incohérence.",
            membersHeader: "Membres",
            accountHeader: "Compte",
            accessHeader: "Accès",
            ownerLabel: "Propriétaire (vous)",
            levelWrite: "Écriture",
            levelRead: "Lecture",
            remove: "Retirer",
            add: "Ajouter",
            share: "Partager",
            addPlaceholder: "— Ajouter un compte —",
            onlyYou: "Vous seul. Ajoutez un compte pour partager cette playlist.",
            confirmRemoveTitle: "Retirer ce membre ?",
            confirmRemoveText: "Ce compte n'aura plus accès à cette playlist.",
            errSharingDisabled: "Le partage de contenus personnels n'est pas activé pour votre compte.",
            errNotFound: "Playlist introuvable.",
            errInvalidLevel: "Niveau invalide.",
            errInvalidUser: "Compte invalide.",
            errSelf: "Action impossible sur vous-même.",
            errNotShared: "Cette playlist n'est pas partagée.",
            errBusy: "La playlist est en cours de modification, réessayez dans quelques secondes.",
            errInvalidFamily: "Option inconnue.",
            errInternal: "Une erreur est survenue."
        },
        en: {
            title: 'PlaySync',
            intro: 'Your playlists — share them with other accounts and choose how they behave. Only playlists you own are listed here.',
            denied: 'Sharing personal content is not enabled for your account. Contact the server administrator.',
            empty: 'You do not own any playlist. Add an item to a new playlist, then come back here to share it.',
            shared: 'Shared',
            unshared: 'Not shared',
            members: 'members',
            member: 'member',
            items: 'items',
            optionsHeader: 'Options',
            removeOnPlayedTitle: 'Remove an item once it is played',
            removeOnPlayedDesc: 'When a member marks an item as played, it is removed from the playlist for everyone.',
            propagateTitle: 'Share playback state',
            propagateDesc: 'Played status and playback position are copied to the other members.',
            optionsUnavailable: 'Available once the playlist is shared.',
            conflict: 'Conflicting tags, option inactive. The next change fixes the inconsistency.',
            membersHeader: 'Members',
            accountHeader: 'Account',
            accessHeader: 'Access',
            ownerLabel: 'Owner (you)',
            levelWrite: 'Write',
            levelRead: 'Read',
            remove: 'Remove',
            add: 'Add',
            share: 'Share',
            addPlaceholder: '— Add an account —',
            onlyYou: 'Only you. Add an account to share this playlist.',
            confirmRemoveTitle: 'Remove this member?',
            confirmRemoveText: 'This account will no longer have access to this playlist.',
            errSharingDisabled: 'Sharing personal content is not enabled for your account.',
            errNotFound: 'Playlist not found.',
            errInvalidLevel: 'Invalid level.',
            errInvalidUser: 'Invalid account.',
            errSelf: 'This action cannot target yourself.',
            errNotShared: 'This playlist is not shared.',
            errBusy: 'The playlist is being updated, please retry in a few seconds.',
            errInvalidFamily: 'Unknown option.',
            errInternal: 'Something went wrong.'
        }
    };

    function currentLang() {
        var navLang = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
        return navLang.indexOf('fr') === 0 ? 'fr' : 'en';
    }

    function t(key) {
        var dict = STRINGS[currentLang()] || STRINGS.en;
        return dict[key] != null ? dict[key] : key;
    }

    /// Codes serveur stables (contracts/http-endpoints.md) -> clé du dictionnaire local. Jamais le code brut affiché.
    function errorKey(code) {
        switch (code) {
            case 'sharing-disabled': return 'errSharingDisabled';
            case 'not-found': return 'errNotFound';
            case 'invalid-level': return 'errInvalidLevel';
            case 'invalid-user': return 'errInvalidUser';
            case 'self': return 'errSelf';
            case 'not-shared': return 'errNotShared';
            case 'busy': return 'errBusy';
            case 'invalid-family': return 'errInvalidFamily';
            default: return 'errInternal';
        }
    }

    function familyTitleKey(family) { return family === 'remove-si-lu' ? 'removeOnPlayedTitle' : 'propagateTitle'; }
    function familyDescKey(family) { return family === 'remove-si-lu' ? 'removeOnPlayedDesc' : 'propagateDesc'; }

    function el(tag, className) {
        var e = document.createElement(tag);
        if (className) e.className = className;
        return e;
    }

    function setText(target, key) { target.textContent = t(key); }

    function clear(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    /// Ligne flex (remplace un <table> sans classe Emby connue — chrome blanc par défaut du navigateur). Styles
    /// posés en ligne (display/gap/padding uniquement) : indépendants des classes CSS de l'hôte, la couleur/police
    /// du texte reste héritée normalement du thème de la page.
    function flexRow() {
        var row = document.createElement('div');
        row.style.display = 'flex';
        row.style.alignItems = 'center';
        row.style.gap = '8px';
        row.style.padding = '.35em 0';
        return row;
    }

    return function (view) {
        var selectableUsers = [];

        function apiGet(path, params) {
            return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(path, params || {}), dataType: 'json' });
        }

        function apiSend(method, path, body) {
            return ApiClient.ajax({
                type: method,
                url: ApiClient.getUrl(path, {}),
                data: JSON.stringify(body || {}),
                contentType: 'application/json',
                dataType: 'json'
            });
        }

        /// Statut HTTP d'une réponse en échec (ApiClient.ajax rejette avec la réponse brute selon les versions).
        function statusOf(err) {
            return err && (err.status || err.Status || 0);
        }

        function showToast(text) {
            require(['toast'], function (toastFn) { toastFn(text); });
        }

        /// Le code stable est lu s'il est disponible ({"Error":"<code>"}, corps de réponse) ; toujours traduit via
        /// le dictionnaire local (jamais le code brut, jamais un message d'exception, dans le DOM).
        function showError(err) {
            if (err && typeof err.json === 'function') {
                err.json().then(function (body) {
                    showToast(t(errorKey(body && body.Error)));
                }, function () { showToast(t('errInternal')); });
                return;
            }
            showToast(t(errorKey(err && err.Error)));
        }

        // GATE 4 (2e tentative, v1.1.0.5 : createElement(tag, {is:...}) seul n'a RIEN changé visuellement) :
        // les composants natifs sont maintenant CLONÉS depuis les prototypes écrits en dur dans userPage.html
        // (#PlaySyncPrototypes), jamais construits par document.createElement. Hypothèse retenue : le chargement
        // des composants dépend du HTML STATIQUE de la page, jamais vu pour un élément 100% généré en JS.
        function prototypes() { return view.querySelector('#PlaySyncPrototypes'); }

        /// DevTools (QUALIF, confirmé par l'utilisateur) : "Multiple form field elements... same id attribute" —
        /// le composant natif (upgradé une seule fois, sur le prototype caché) s'assigne apparemment un id lors
        /// de son upgrade ; cloneNode(true) copie cet id tel quel sur CHAQUE clone. Aucun id n'est nécessaire
        /// côté script (jamais de <label for="...">, jamais de getElementById sur ces éléments — l'association
        /// label/input se fait par imbrication, les écouteurs par référence directe) : retiré sans risque, sur le
        /// clone ET tout descendant (le composant peut construire son propre DOM interne lors de l'upgrade).
        function stripIds(node) {
            if (node.removeAttribute) node.removeAttribute('id');
            if (node.querySelectorAll) {
                var withId = node.querySelectorAll('[id]');
                for (var i = 0; i < withId.length; i++) withId[i].removeAttribute('id');
            }
            return node;
        }

        function cloneSelect() { return stripIds(prototypes().querySelector('select').cloneNode(true)); }
        function cloneToggle() { return stripIds(prototypes().querySelector('input').cloneNode(true)); }
        function cloneIconButton() { return stripIds(prototypes().querySelectorAll('button')[0].cloneNode(true)); }
        function cloneSubmitButton() { return stripIds(prototypes().querySelectorAll('button')[1].cloneNode(true)); }

        function levelSelect(selectedValue) {
            var select = cloneSelect(); // prototype : deux <option> déjà présentes (value="Write"/"Read")
            var options = select.querySelectorAll('option');
            options[0].textContent = t('levelWrite');
            options[0].selected = selectedValue === 'Write';
            options[1].textContent = t('levelRead');
            options[1].selected = selectedValue === 'Read';
            return select;
        }

        function renderOptions(playlist) {
            var section = el('div', 'section');
            var h3 = el('h3'); setText(h3, 'optionsHeader'); section.appendChild(h3);

            FAMILIES.forEach(function (family) {
                // Mise en page en flex, styles POSÉS EN LIGNE (jamais les classes checkboxContainer/
                // fieldDescription/checkboxFieldDescription — GATE 4, 3e passe : leur comportement réel dans ce
                // contexte produisait des puces parasites devant la description et un mauvais alignement). Même
                // structure que .opt/.opt-text de la maquette (docs/mockup/v1.1.0/ui/user-page__b39.html) :
                // interrupteur à gauche, colonne de texte (libellé + description) alignée juste à droite, sur
                // toute sa largeur — jamais de marqueur de liste, jamais de dépendance à une classe non vérifiée.
                var container = document.createElement('div');
                container.style.padding = '8px 0';

                var label = document.createElement('label');
                label.style.display = 'flex';
                label.style.alignItems = 'flex-start';
                label.style.gap = '12px';
                label.style.cursor = 'pointer';

                var input = cloneToggle();
                input.style.flexShrink = '0';
                input.style.marginTop = '2px';
                var state = (playlist.Options && playlist.Options[family]) || 'None';
                input.checked = state === 'Oui';
                input.disabled = !playlist.IsShared;
                label.appendChild(input);

                var textCol = document.createElement('div');

                var titleEl = document.createElement('div');
                titleEl.style.fontWeight = '500';
                setText(titleEl, familyTitleKey(family));
                textCol.appendChild(titleEl);

                var desc = document.createElement('div');
                desc.style.opacity = '.75';
                desc.style.fontSize = '.9em';
                desc.style.marginTop = '2px';
                setText(desc, playlist.IsShared ? familyDescKey(family) : 'optionsUnavailable');
                textCol.appendChild(desc);

                if (playlist.IsShared && state === 'Both') {
                    var warn = document.createElement('div');
                    warn.style.fontSize = '.9em';
                    warn.style.marginTop = '2px';
                    setText(warn, 'conflict');
                    textCol.appendChild(warn);
                }

                label.appendChild(textCol);
                container.appendChild(label);

                if (playlist.IsShared) {
                    input.addEventListener('change', function () {
                        var path = 'SharedPlaylist/User/Playlists/' + encodeURIComponent(playlist.PlaylistId) + '/Options';
                        apiSend('POST', path, { Family: family, Enabled: input.checked }).then(
                            function () { reload(); },
                            function (err) { showError(err); reload(); }
                        );
                    });
                }

                section.appendChild(container);
            });

            return section;
        }

        function renderAddRow(playlist) {
            var row = flexRow();
            row.style.flexWrap = 'wrap';
            row.style.marginTop = '.5em';

            var userSelect = cloneSelect();
            clear(userSelect); // vide les deux <option> du prototype (Write/Read) : liste variable de comptes
            userSelect.style.minWidth = '220px';
            var placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = t('addPlaceholder');
            userSelect.appendChild(placeholder);
            selectableUsers.forEach(function (u) {
                var opt = document.createElement('option');
                opt.value = u.UserId;
                opt.textContent = u.Name;
                userSelect.appendChild(opt);
            });
            row.appendChild(userSelect);

            var level = levelSelect('Write');
            row.appendChild(level);

            var addBtn = cloneSubmitButton();
            setText(addBtn, playlist.IsShared ? 'add' : 'share');
            addBtn.addEventListener('click', function () {
                if (!userSelect.value) return;
                var path = 'SharedPlaylist/User/Playlists/' + encodeURIComponent(playlist.PlaylistId) + '/Members';
                apiSend('POST', path, { UserId: userSelect.value, Level: level.value }).then(
                    function () { reload(); },
                    function (err) { showError(err); reload(); }
                );
            });
            row.appendChild(addBtn);

            return row;
        }

        function renderMembers(playlist) {
            var section = el('div', 'section');
            var h3 = el('h3'); setText(h3, 'membersHeader'); section.appendChild(h3);

            if (!playlist.Members || playlist.Members.length === 0) {
                var hint = el('p', 'fieldDescription');
                setText(hint, 'onlyYou');
                section.appendChild(hint);
            } else {
                // Pas de <table> : un <table>/<tr>/<td> sans classe Emby connue se rend avec le chrome BLANC par
                // défaut du navigateur (cause probable du "fond blanc" constaté en QUALIF v1.1.0.4, en plus des
                // composants non améliorés ci-dessous) — des lignes <div> en flex héritent normalement du thème.
                var head = flexRow();
                head.style.opacity = '.7';
                var headAccount = document.createElement('div');
                headAccount.style.flex = '1';
                setText(headAccount, 'accountHeader');
                var headAccess = document.createElement('div');
                headAccess.style.minWidth = '150px';
                setText(headAccess, 'accessHeader');
                head.appendChild(headAccount);
                head.appendChild(headAccess);
                head.appendChild(document.createElement('div'));
                section.appendChild(head);

                playlist.Members.forEach(function (member) {
                    var row = flexRow();

                    var name = document.createElement('div');
                    name.style.flex = '1';
                    name.textContent = member.Name; // donnée serveur : textContent uniquement
                    row.appendChild(name);

                    var levelHolder = document.createElement('div');
                    levelHolder.style.minWidth = '150px';
                    var select = levelSelect(member.Level);
                    select.addEventListener('change', function () {
                        var path = 'SharedPlaylist/User/Playlists/' + encodeURIComponent(playlist.PlaylistId) + '/Members';
                        apiSend('POST', path, { UserId: member.UserId, Level: select.value }).then(
                            function () { reload(); },
                            function (err) { showError(err); reload(); }
                        );
                    });
                    levelHolder.appendChild(select);
                    row.appendChild(levelHolder);

                    var removeBtn = cloneIconButton(); // '✕' déjà présent (prototype)
                    removeBtn.title = t('remove');
                    removeBtn.addEventListener('click', function () {
                        // Confirmation via le dialogue natif Emby (module "confirm"), jamais une boîte bloquante du navigateur.
                        require(['confirm'], function (confirmAction) {
                            confirmAction({
                                title: t('confirmRemoveTitle'),
                                text: t('confirmRemoveText'),
                                confirmText: t('remove'),
                                primary: 'delete'
                            }).then(function () {
                                var path = 'SharedPlaylist/User/Playlists/' + encodeURIComponent(playlist.PlaylistId) +
                                    '/Members/' + encodeURIComponent(member.UserId);
                                apiSend('DELETE', path, null).then(
                                    function () { reload(); },
                                    function (err) { showError(err); reload(); }
                                );
                            }, function () { /* annulé par l'utilisateur : rien à faire */ });
                        });
                    });
                    row.appendChild(removeBtn);

                    section.appendChild(row);
                });
            }

            section.appendChild(renderAddRow(playlist));
            return section;
        }

        function renderCard(playlist) {
            var card = el('div', 'detailSection');
            card.style.marginBottom = '1.2em'; // espace entre les cartes de playlists (maquette : 18px)

            // Retouches visuelles demandées par l'utilisateur (GATE 4) : espacement explicite (le nom collait au
            // badge) ; badge simplifié (le nombre de membres était redondant avec la liste juste en dessous, déjà
            // visible sans avoir à le répéter dans l'en-tête).
            var head = el('div', 'sectionTitleContainer flex align-items-center');
            head.style.gap = '10px';
            head.style.flexWrap = 'wrap';
            var h2 = document.createElement('h2');
            h2.className = 'sectionTitle';
            h2.style.margin = '0';
            h2.textContent = playlist.Name; // donnée serveur : textContent uniquement
            head.appendChild(h2);

            var badge = document.createElement('span');
            badge.textContent = playlist.IsShared ? t('shared') : t('unshared');
            head.appendChild(badge);

            var count = document.createElement('span');
            count.style.opacity = '.7';
            count.textContent = playlist.ItemCount + ' ' + t('items');
            head.appendChild(count);

            card.appendChild(head);
            card.appendChild(renderOptions(playlist));
            card.appendChild(renderMembers(playlist));

            return card;
        }

        function render(playlists) {
            var listRoot = view.querySelector('#PlaySyncList');
            clear(listRoot);

            var emptyBlock = view.querySelector('#PlaySyncEmpty');
            if (!playlists || playlists.length === 0) {
                emptyBlock.style.display = '';
                setText(view.querySelector('#PlaySyncEmptyText'), 'empty');
                return;
            }
            emptyBlock.style.display = 'none';

            playlists.forEach(function (playlist) { listRoot.appendChild(renderCard(playlist)); });
        }

        function showDenied() {
            view.querySelector('#PlaySyncDenied').style.display = '';
            setText(view.querySelector('#PlaySyncDeniedText'), 'denied');
            view.querySelector('#PlaySyncEmpty').style.display = 'none';
            clear(view.querySelector('#PlaySyncList'));
        }

        function reload() {
            apiGet('SharedPlaylist/User/Playlists').then(
                function (list) {
                    view.querySelector('#PlaySyncDenied').style.display = 'none';
                    render(list);
                },
                function (err) {
                    if (statusOf(err) === 403) { showDenied(); return; }
                    showError(err);
                }
            );
        }

        function localize() {
            setText(view.querySelector('#PlaySyncTitle'), 'title');
            setText(view.querySelector('#PlaySyncIntro'), 'intro');
        }

        view.addEventListener('viewshow', function () {
            localize();
            apiGet('SharedPlaylist/User/Users').then(
                function (list) { selectableUsers = list || []; reload(); },
                function () { selectableUsers = []; reload(); }
            );
        });
    };
});
