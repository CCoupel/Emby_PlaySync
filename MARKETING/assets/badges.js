/* PlaySync — recalcul des badges "Nouveau vX.Y.Z" a partir de la version courante.
   Purement cote client : ne jamais reecrire data-badge-version depuis l'agent,
   seul <meta name="current-major"> doit etre tenu a jour a chaque republication reelle. */
(function () {
  var NEW_LABEL = { fr: 'Nouveau', en: 'New' };

  function refresh(lang) {
    var meta = document.querySelector('meta[name="current-major"]');
    if (!meta) return;
    var CURRENT_MAJOR = parseInt(meta.content, 10);
    var label = NEW_LABEL[lang] || NEW_LABEL.fr;

    document.querySelectorAll('[data-badge-version]').forEach(function (el) {
      var m = el.dataset.badgeVersion.match(/^v(\d+)/);
      if (!m) return;
      var badgeMajor = parseInt(m[1], 10);
      var diff = CURRENT_MAJOR - badgeMajor;

      if (diff >= 2) { el.remove(); return; }

      el.classList.toggle('badge-orange', diff === 0);
      el.classList.toggle('badge-blue', diff === 1);
      el.textContent = diff === 0 ? (label + ' ' + el.dataset.badgeVersion) : el.dataset.badgeVersion;
    });
  }

  window.playSyncRefreshBadges = refresh;
  refresh(document.documentElement.getAttribute('lang') || 'fr');
})();
