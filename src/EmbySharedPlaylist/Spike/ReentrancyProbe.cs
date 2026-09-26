using System.Collections.Concurrent;
using System.Diagnostics;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Sonde TEMPORAIRE de réentrance (B52, essai U11, retirée avec Spike/* — #15). Inactive par défaut
/// (<c>EnableReentrancyProbe</c>). Répond à : peut-on écrire (retrait, métadonnées, données utilisateur) DEPUIS un
/// gestionnaire d'événement, sans blocage, interblocage, boucle ni verrou de base ? AUCUNE garde de comptes (décision de
/// l'utilisateur, instance QUALIF : la sonde peut être déclenchée par n'importe quel compte, admin compris). Restent : l'option
/// <c>EnableReentrancyProbe</c> (faux par défaut), le nom de la playlist (SPIKE-P1 … SPIKE-P6, préfixe exact, sinon la sonde ne
/// fait rien), le verrou par playlist et le try/catch total. Le scénario est choisi par ce nom :
/// <list type="bullet">
/// <item>P1 : <c>RemoveFromPlaylist</c> depuis <c>UserDataSaved</c> (le média passe à lu).</item>
/// <item>P2 : mise à jour de métadonnées (<c>UpdateToRepository</c>) depuis <c>UserDataSaved</c>.</item>
/// <item>P3 : <c>SaveUserData</c> d'un AUTRE membre de la playlist (n'importe quelle ligne de partage ≥ Read ; garde <see cref="PluginWriteTracker"/>) depuis <c>UserDataSaved</c>.</item>
/// <item>P4 : mise à jour de métadonnées depuis <c>PlaylistItemsAdded</c>/<c>ItemUpdated</c> (première détection simulée).</item>
/// <item>P5 : comme P1, mais pour une rafale concurrente (plusieurs transitions simultanées sur la même playlist) sous verrou.</item>
/// <item>P6 : le gestionnaire attend 2 s (le propriétaire édite l'étiquette par REST pendant ce temps), puis relit et écrit.</item>
/// </list>
/// Résultat : entrée de journal Kind=Probe, Detail = <c>scenario=Pn durationMs=… lockWaitMs=… echoes=… outcome=OK|KO …</c>.
/// <c>echoes</c> = événements reçus sur la playlist (ou sur le couple utilisateur/média pour P3) dans les 1,5 s suivant l'écriture.
/// </summary>
public sealed class ReentrancyProbe
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(1500);
    private const int CallTimeoutMs = 5000;
    private const int LoopSuspicionEchoes = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IItemRepository _itemRepository;
    private readonly PlaylistLocks _locks;
    private readonly PlaylistEntryReader _entryReader;
    private readonly Log _log;

    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private int _counter;

    public ReentrancyProbe(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager,
        IPlaylistManager playlistManager, IItemRepository itemRepository, PlaylistLocks locks, Log log)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _itemRepository = itemRepository;
        _locks = locks;
        _entryReader = new PlaylistEntryReader(libraryManager, userManager, itemRepository);
        _log = log;
    }

    public static bool Enabled => Plugin.Instance?.Configuration.EnableReentrancyProbe == true;

    private sealed class Session
    {
        public string? PlaylistId;
        public string? EchoUserId;   // P3 : écho attendu = UserDataSaved de ce couple
        public string? EchoItemId;
        public int Echoes;
        public readonly ConcurrentDictionary<string, int> Kinds = new();
    }

    // ---- Observation des échos (appelée en tête de chaque gestionnaire, avant tout test de scope) -----------

    public void ObserveEcho(string kind, string? playlistId, string? userId, string? itemId)
    {
        foreach (var s in _sessions.Values)
        {
            var match = kind == "UserDataSaved"
                ? s.EchoUserId != null && s.EchoUserId == userId && s.EchoItemId == itemId
                : s.PlaylistId != null && s.PlaylistId == playlistId;
            if (!match) continue;
            Interlocked.Increment(ref s.Echoes);
            s.Kinds.AddOrUpdate(kind, 1, (_, n) => n + 1);
        }
    }

    // ---- Déclencheurs ------------------------------------------------------------------------------------

    /// <param name="pluginWrite">Vrai si l'événement est l'écho d'une écriture du plugin (garde P3 : jamais de rebond).</param>
    public void OnUserDataSaved(UserDataSaveEventArgs e, bool pluginWrite)
    {
        try
        {
            // Chaque sortie précoce est journalisée avec sa raison (ids seulement) : la sonde ne doit jamais rester muette.
            // Les rejets courants (lecture en cours, autre motif) sont en Debug ; les rejets décisifs en Info.
            var user = e.User.Id.ToString("N");
            var item = e.Item.InternalId.ToString();
            if (WriteScope.Active) { SkipDebug("write-scope-active", user, item); return; }
            if (pluginWrite) { SkipDebug("plugin-write", user, item); return; }
            if (e.UserData?.Played != true) { SkipDebug("not-played", user, item); return; }
            if (e.SaveReason != UserDataSaveReason.TogglePlayed && e.SaveReason != UserDataSaveReason.PlaybackFinished)
            { SkipDebug("save-reason-" + e.SaveReason, user, item); return; }

            var search = FindProbePlaylists(e.User, e.Item);
            if (search.Found.Count == 0)
            {
                // Bruit si aucune playlist SPIKE-Pn n'existe (chaque lecture d'un utilisateur quelconque passe ici) : Debug.
                // Info seulement quand une playlist SPIKE-Pn existe mais qu'aucune entrée du média n'y est trouvée.
                var detail = $"listed={search.Listed} probeNamed={search.ProbeNamed} withItem={search.WithItem} entries=[{string.Join(";", search.Diagnostics)}]";
                if (SpikeRules.NoProbePlaylistIsNoteworthy(search.ProbeNamed)) SkipInfo("no-probe-playlist", user, item, detail);
                else SkipDebug("no-probe-playlist", user, item, detail);
                return;
            }

            var (playlist, scenario) = search.Found[0]; // une playlist de sonde par transition
            _log.Info($"{SpikeLogFormat.Prefix}Probe trigger scenario=P{scenario} playlist={playlist.InternalId} user={user} item={item}");
            Run(scenario, playlist, e.User, e.Item);
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist spike : erreur dans la sonde (UserDataSaved)", ex);
        }
    }

    private void SkipDebug(string reason, string? user, string? item, string? extra = null) =>
        _log.Debug($"{SpikeLogFormat.Prefix}Probe skipped reason={reason} user={user ?? "-"} item={item ?? "-"}" + (extra != null ? " " + extra : string.Empty));

    private void SkipInfo(string reason, string? user, string? item, string? extra) =>
        _log.Info($"{SpikeLogFormat.Prefix}Probe skipped reason={reason} user={user ?? "-"} item={item ?? "-"}" + (extra != null ? " " + extra : string.Empty));

    public void OnPlaylistItemsAdded(PlaylistItemsAddedEventArgs e)
    {
        try
        {
            if (SpikeRules.ProbeScenario(e.Playlist.Name) != 4) return;
            HandleP4(e.Playlist, "PlaylistItemsAdded");
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist spike : erreur dans la sonde (PlaylistItemsAdded)", ex);
        }
    }

    public void OnItemUpdated(ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is not Playlist playlist || SpikeRules.ProbeScenario(playlist.Name) != 4) return;
            HandleP4(playlist, "ItemUpdated");
        }
        catch (Exception ex)
        {
            _log.Error("EmbySharedPlaylist spike : erreur dans la sonde (ItemUpdated)", ex);
        }
    }

    private void HandleP4(Playlist playlist, string trigger)
    {
        var id = playlist.InternalId.ToString();
        if (WriteScope.Active) { Emit(id, null, null, $"scenario=P4 trigger={trigger} durationMs=0 echoes=0 outcome=OK skipped=reentrant"); return; }
        if (!_seen.TryAdd(id, 0)) { Emit(id, null, null, $"scenario=P4 trigger={trigger} durationMs=0 echoes=0 outcome=OK skipped=already-seen"); return; }
        Run(4, playlist, null, null);
    }

    // ---- Exécution d'un scénario -------------------------------------------------------------------------

    private void Run(int scenario, Playlist playlist, User? user, BaseItem? item)
    {
        var playlistId = playlist.InternalId.ToString();
        var session = new Session { PlaylistId = playlistId };
        var sessionId = Guid.NewGuid();
        var outcome = "OK";
        var extra = string.Empty;
        long lockWaitMs = 0;
        var total = Stopwatch.StartNew();

        User? target = null;
        if (scenario == 3)
        {
            target = FindOtherMember(playlist, user!);
            if (target == null) { outcome = "KO"; extra = " reason=no-target-user"; }
            else { session.EchoUserId = target.Id.ToString("N"); session.EchoItemId = item!.InternalId.ToString(); }
        }

        if (outcome == "OK")
        {
            _sessions[sessionId] = session;
            try
            {
                var waited = Stopwatch.StartNew();
                using var gate = _locks.TryAcquire(playlistId, PlaylistLocks.DefaultTimeout);
                lockWaitMs = waited.ElapsedMilliseconds;
                if (gate == null) { outcome = "KO"; extra = " reason=lock-busy"; }
                else
                {
                    using (WriteScope.Enter())
                    {
                        extra = scenario switch
                        {
                            1 or 5 => BodyRemove(playlist, item!, user),
                            2 => BodyMetadata(playlist, "probe-p2"),
                            3 => BodySaveUserData(target!, item!),
                            4 => BodyMetadata(playlist, "probe-p4"),
                            6 => BodySlowThenWrite(playlist),
                            _ => string.Empty
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                outcome = "KO";
                extra = " error=" + (ex is TimeoutException ? "timeout-deadlock-suspected" : ex.GetType().Name);
                _log.Error("EmbySharedPlaylist spike : sonde P" + scenario, ex);
            }
        }
        total.Stop();

        var durationMs = total.ElapsedMilliseconds;
        var uid = user?.Id.ToString("N");
        var iid = item?.InternalId.ToString();
        var prefix = $"scenario=P{scenario} durationMs={durationMs} lockWaitMs={lockWaitMs}";

        if (outcome != "OK")
        {
            _sessions.TryRemove(sessionId, out _);
            Emit(playlistId, uid, iid, $"{prefix} echoes=0 outcome=KO{extra}");
            return;
        }

        // Les échos éventuels arrivent pendant et juste après l'écriture : on clôt la session après la fenêtre.
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EchoWindow).ConfigureAwait(false);
                _sessions.TryRemove(sessionId, out _);
                var echoes = session.Echoes;
                var kinds = string.Join(",", session.Kinds.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value));
                var final = echoes > LoopSuspicionEchoes ? "KO" : "OK";
                var loop = echoes > LoopSuspicionEchoes ? " reason=loop-suspected" : string.Empty;
                Emit(playlistId, uid, iid, $"{prefix} echoes={echoes} outcome={final}{extra}{loop}" + (kinds.Length > 0 ? " echoKinds=" + kinds : string.Empty));
            }
            catch (Exception ex)
            {
                _log.Error("EmbySharedPlaylist spike : sonde P" + scenario + " (clôture)", ex);
            }
        });
    }

    private string BodyRemove(Playlist playlist, BaseItem item, User? user)
    {
        var read = _entryReader.Read(playlist, user);
        var target = read.Entries.FirstOrDefault(c => c.ItemId == item.InternalId);
        if (target == null) return $" removed=0 note=absent entries={read.Strategy}";
        WaitOrThrow(_playlistManager.RemoveFromPlaylist(playlist, new[] { target.EntryId }));
        var remaining = _entryReader.Read(playlist, user).Entries.Count(c => c.ItemId == item.InternalId);
        return $" removed=1 remaining={remaining} entries={read.Strategy}";
    }

    private string BodyMetadata(Playlist playlist, string tag)
    {
        var fresh = _libraryManager.GetItemById(playlist.InternalId) as Playlist ?? playlist;
        // Modification réelle à chaque itération (sinon Emby pourrait ignorer l'écriture et masquer la latence).
        fresh.Overview = $"{tag} #{Interlocked.Increment(ref _counter)}";
        fresh.SetTags(SpikeRules.ApplyTagChanges(fresh.Tags, new[] { tag }, null));
        fresh.UpdateToRepository(ItemUpdateType.MetadataEdit);
        return string.Empty;
    }

    private string BodySaveUserData(User target, BaseItem item)
    {
        var data = _userDataManager.GetUserData(target, item);
        data.Played = true;
        if (data.PlayCount < 1) data.PlayCount = 1;
        SpikeRuntime.Tracker.Register(target.InternalId, item.InternalId); // garde : l'écho est reconnu (pluginWrite) et ne rebondit pas
        _userDataManager.SaveUserData(target, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        return $" target={target.Id:N} playedAfter={(_userDataManager.GetUserData(target, item).Played ? "true" : "false")}";
    }

    private string BodySlowThenWrite(Playlist playlist)
    {
        // Le propriétaire édite l'étiquette par REST pendant cette attente ; on relit ensuite (jamais d'écrasement).
        Thread.Sleep(2000);
        BodyMetadata(playlist, "probe-p6");
        var fresh = _libraryManager.GetItemById(playlist.InternalId) as Playlist ?? playlist;
        return $" note=sleep2000 tagsAfter={(fresh.Tags ?? Array.Empty<string>()).Length}";
    }

    // ---- Aides -------------------------------------------------------------------------------------------

    private static void WaitOrThrow(Task task)
    {
        if (!task.Wait(CallTimeoutMs)) throw new TimeoutException("appel SDK > 5 s");
        task.GetAwaiter().GetResult(); // relève l'exception éventuelle
    }

    private sealed record ProbeSearch(List<(Playlist Playlist, int Scenario)> Found, int Listed, int ProbeNamed, int WithItem, List<string> Diagnostics);

    /// <summary>Playlists de sonde (P1, P2, P3, P5, P6 ; P4 est déclenchée par les événements de playlist) contenant le média.</summary>
    private ProbeSearch FindProbePlaylists(User user, BaseItem item)
    {
        var found = new List<(Playlist, int)>();
        var diagnostics = new List<string>();
        var listed = 0;
        var probeNamed = 0;
        var withItem = 0;
        var query = new InternalItemsQuery(user) { IncludeItemTypes = new[] { "Playlist" }, Recursive = true };
        foreach (var candidate in _libraryManager.GetItemList(query))
        {
            if (candidate is not Playlist playlist) continue;
            listed++;
            var scenario = SpikeRules.ProbeScenario(playlist.Name);
            if (scenario == 0 || scenario == 4) continue; // P4 est déclenché par les événements de playlist
            probeNamed++;
            var read = _entryReader.Read(playlist, user);
            diagnostics.Add($"{playlist.InternalId}:P{scenario}:{read.Entries.Count}:{read.Strategy}");
            if (!read.Entries.Any(c => c.ItemId == item.InternalId)) continue;
            withItem++;
            found.Add((playlist, scenario));
        }
        return new ProbeSearch(found, listed, probeNamed, withItem, diagnostics);
    }

    /// <summary>Cible de P3 : n'importe quel AUTRE membre (ligne de partage ≥ Read). Sonde temporaire sans garde de comptes (QUALIF).</summary>
    private User? FindOtherMember(Playlist playlist, User eventUser)
    {
        var shares = _itemRepository.GetUserItemShares(new UserItemShareQuery { ItemIds = new[] { playlist.InternalId } }, CancellationToken.None);
        foreach (var share in shares.Where(s => (s.ShareLevel ?? UserItemShareLevel.None) >= UserItemShareLevel.Read))
        {
            if (share.UserId == eventUser.InternalId) continue;
            var other = _userManager.GetUserById(share.UserId);
            if (other != null) return other;
        }
        return null;
    }

    private void Emit(string? playlistId, string? userId, string? itemId, string detail)
    {
        var entry = new JournalEntry
        {
            Ts = DateTime.UtcNow.ToString("o"),
            Kind = "Probe",
            PlaylistId = playlistId,
            UserId = userId,
            ItemId = itemId,
            Detail = detail
        };
        SpikeRuntime.Journal.Add(entry);
        _log.Info(SpikeLogFormat.Event(entry));
    }
}
