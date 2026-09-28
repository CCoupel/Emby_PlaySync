/* PlaySync — commutateur de langue FR/EN */
(function () {
  var CACHE = {};

  function loadLocale(lang) {
    if (CACHE[lang]) return Promise.resolve(CACHE[lang]);
    return fetch('locales/' + lang + '.json')
      .then(function (r) { return r.json(); })
      .then(function (data) { CACHE[lang] = data; return data; });
  }

  function applyStrings(lang, strings) {
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var key = el.getAttribute('data-i18n');
      var val = strings[key];
      if (val !== undefined) el.innerHTML = val;
    });
    document.querySelectorAll('.lang-btn').forEach(function (btn) {
      btn.classList.toggle('active', btn.dataset.lang === lang);
    });
    document.documentElement.setAttribute('lang', lang);
    try { localStorage.setItem('playsync-lang', lang); } catch (e) { /* per-viewer convenience only */ }
    if (typeof window.playSyncRefreshBadges === 'function') window.playSyncRefreshBadges(lang);
  }

  function setLang(lang) {
    loadLocale(lang).then(function (strings) { applyStrings(lang, strings); });
  }

  document.querySelectorAll('.lang-btn').forEach(function (btn) {
    btn.addEventListener('click', function () { setLang(btn.dataset.lang); });
  });

  var saved = 'fr';
  try { saved = localStorage.getItem('playsync-lang') || 'fr'; } catch (e) { /* ignore */ }
  setLang(saved);
})();
