using System.Collections.Concurrent;
using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Marker;
using EmbySharedPlaylist.Reconciliation;
using EmbySharedPlaylist.UserPage;
using Xunit;

namespace EmbySharedPlaylist.Tests;

/// <summary>Socle commun des suites de spécification de <see cref="UserPlaylistService"/> (faux ports, <c>Rig</c>, constantes) :
/// scindé en lots de moins de 50 cas (COMMON §15) — UserPlaylistServiceSpecTests (autorisation, orchestration, bascule, journal),
/// UserPlaylistServiceSecuritySpecTests (forme, sécurité point 7, trois familles v1.2.0).</summary>
public abstract class UserPlaylistServiceSpecBase
{
    protected const string Owner = "u1";
    protected const string MemberWrite = "u2";
    protected const string MemberRead = "u3";
    protected const string Stranger = "u4"; // ne possède rien, cible d'IDOR

    /// <summary>Faux minimal d'<see cref="IPlaylistGateway"/> pour ce fichier (distinct du <c>FakeGateway</c> de
    /// <c>ReconciliationDevTests</c> : n'implémente que ce dont <see cref="DefaultsService"/> et
    /// <see cref="UserPlaylistService"/> ont besoin, avec <c>ReplaceFamily</c>, D19).</summary>
    internal sealed class FakeUserPageGateway : IPlaylistGateway
    {
        public sealed class State
        {
            public string? Owner;
            public List<string> Members = new();
            public List<string> Tags = new();
            public string? Overview;
        }

        public readonly ConcurrentDictionary<string, State> Playlists = new();
        public Func<string, bool>? ThrowOnReplaceFamilyFor;
        public int ReplaceFamilyCalls;

        // --- #55 (lot C) : port CreatePlaylist(ownerId, name) -> id (contrat proposé, plan C1) ---
        public readonly ConcurrentDictionary<string, string> CreatedNames = new();
        public readonly ConcurrentQueue<(string Owner, string Name)> CreateLog = new();
        public int CreateCalls;
        public Action? OnCreate;
        public bool ThrowOnCreate;
        private int _createSeq;

        public string CreatePlaylist(string ownerId, string name)
        {
            Interlocked.Increment(ref CreateCalls);
            OnCreate?.Invoke();
            if (ThrowOnCreate) throw new InvalidOperationException("création en échec avec un message secret");
            var id = "new-" + Interlocked.Increment(ref _createSeq);
            Add(id, ownerId);
            CreatedNames[id] = name;
            CreateLog.Enqueue((ownerId, name));
            return id;
        }

        public State Add(string id, string owner, params string[] tags)
        {
            var s = new State { Owner = owner, Tags = tags.ToList(), Members = new List<string> { owner } };
            Playlists[id] = s;
            return s;
        }

        private PlaylistSnapshot Snap(string id, State s) => new(id, s.Owner, s.Members.ToList(), s.Tags.ToList(), s.Overview);

        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylists() =>
            Playlists.Select(p => Snap(p.Key, p.Value)).Where(s => s.IsShared).ToList();

        public IReadOnlyList<PlaylistSnapshot> ListSharedPlaylistsOfUserContaining(string userId, string itemId) =>
            Array.Empty<PlaylistSnapshot>();

        public PlaylistSnapshot? Get(string playlistId) =>
            Playlists.TryGetValue(playlistId, out var s) ? Snap(playlistId, s) : null;

        public bool RemoveOneEntry(string playlistId, string itemId) => false;

        public ApplyResult ApplyDefaults(string playlistId, IReadOnlyList<MarkerFamily> familiesToPose, OverviewChange? overview)
        {
            var s = Playlists[playlistId];
            var posed = new List<MarkerFamily>();
            foreach (var f in familiesToPose)
            {
                if (MarkerEvaluator.Evaluate(s.Tags, f) != MarkerState.None) continue;
                s.Tags.Add(MarkerEvaluator.NonTag(f));
                posed.Add(f);
            }
            var wrote = false;
            if (overview != null && (overview.RequiredCurrent == null ? string.IsNullOrWhiteSpace(s.Overview) : s.Overview == overview.RequiredCurrent))
            {
                s.Overview = overview.NewValue;
                wrote = true;
            }
            return new ApplyResult(posed, wrote);
        }

        public ReplaceFamilyResult ReplaceFamily(string playlistId, MarkerFamily family, bool enabled)
        {
            Interlocked.Increment(ref ReplaceFamilyCalls);
            if (ThrowOnReplaceFamilyFor?.Invoke(playlistId) == true) throw new InvalidOperationException("écriture en échec avec un message secret");
            var s = Playlists[playlistId];
            var (newTags, removed) = MarkerEditor.Replace(s.Tags, family, enabled);
            s.Tags = newTags.ToList();
            return new ReplaceFamilyResult(newTags, removed);
        }
    }

    internal sealed class FakeShareGateway : IShareGateway
    {
        private readonly FakeUserPageGateway _playlists;
        public readonly ConcurrentDictionary<string, List<OwnedPlaylistMember>> MembersByPlaylist = new();
        public readonly ConcurrentQueue<string> ListOwnedOwners = new();
        public readonly ConcurrentDictionary<string, string> NamesByPlaylist = new();
        public readonly ConcurrentDictionary<string, int> ItemCountByPlaylist = new();
        public int UpsertCalls;
        public int DeleteCalls;

        /// <summary>Sécurité (audit 20260928-154256 point 7, review 20260928-154519 "retour bool jamais vérifié") :
        /// quand faux, <see cref="DeleteShare"/> se comporte comme le port RÉEL le ferait sur un échec silencieux
        /// (rien n'est retiré, retourne faux) — sans exception, donc invisible au <c>catch</c> générique du service.</summary>
        public bool ForceDeleteShareResult = true;

        /// <summary>Simule la perte de la ligne <c>ManageDelete</c> du propriétaire APRÈS un <see cref="DeleteShare"/>
        /// réussi (EmbyShareGateway.DeleteShare : purge totale PUIS reconstruction non transactionnelle — un échec
        /// entre les deux appels SDK perd la ligne du propriétaire, cf. security-audit point 7). Pas un mock
        /// artificiel : reproduit exactement ce que <see cref="GetOwned"/> renverrait réellement dans ce cas
        /// (indiscernable d'une playlist non possédée, comme le fait <c>EmbyShareGateway.ToOwned</c>).</summary>
        public bool SimulateOwnerRowLostAfterDelete;

        private readonly HashSet<string> _ownerRowLost = new();

        public FakeShareGateway(FakeUserPageGateway playlists) => _playlists = playlists;

        public string Add(string id, string owner, string name, int itemCount, params (string UserId, string Level)[] members)
        {
            _playlists.Add(id, owner);
            NamesByPlaylist[id] = name;
            ItemCountByPlaylist[id] = itemCount;
            MembersByPlaylist[id] = members.Select(m => new OwnedPlaylistMember(m.UserId, m.Level)).ToList();
            return id;
        }

        public IReadOnlyList<OwnedPlaylist> ListOwnedPlaylists(string ownerId)
        {
            ListOwnedOwners.Enqueue(ownerId);   // #55 : l'unicité ne consulte JAMAIS les playlists d'autres comptes
            return _playlists.Playlists.Where(p => p.Value.Owner == ownerId)
                .Select(p => ToOwned(p.Key)).Where(o => o != null).Select(o => o!).ToList();
        }

        /// <summary>#55 : simule une playlist créée mais non relue comme possédée (GetOwned null).</summary>
        public bool HideCreatedFromGetOwned;

        public OwnedPlaylist? GetOwned(string ownerId, string playlistId)
        {
            if (HideCreatedFromGetOwned && _playlists.CreatedNames.ContainsKey(playlistId)) return null;
            if (!_playlists.Playlists.TryGetValue(playlistId, out var s) || s.Owner != ownerId) return null;
            return ToOwned(playlistId);
        }

        private OwnedPlaylist? ToOwned(string playlistId)
        {
            if (_ownerRowLost.Contains(playlistId)) return null; // ligne ManageDelete perdue : indiscernable d'une playlist non possédée (anti-IDOR, même comportement que le port réel)
            if (!_playlists.Playlists.TryGetValue(playlistId, out var s)) return null;
            var members = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : new List<OwnedPlaylistMember>();
            return new OwnedPlaylist(playlistId, NamesByPlaylist.GetValueOrDefault(playlistId, _playlists.CreatedNames.GetValueOrDefault(playlistId, playlistId)),
                ItemCountByPlaylist.GetValueOrDefault(playlistId, 0), members, s.Tags.ToList());
        }

        public bool UpsertShare(string playlistId, string userId, string level)
        {
            Interlocked.Increment(ref UpsertCalls);
            var list = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : MembersByPlaylist[playlistId] = new List<OwnedPlaylistMember>();
            list.RemoveAll(x => x.UserId == userId);
            list.Add(new OwnedPlaylistMember(userId, level));
            return true;
        }

        public bool DeleteShare(string playlistId, string userId)
        {
            Interlocked.Increment(ref DeleteCalls);
            if (!ForceDeleteShareResult) return false; // échec silencieux du port : AUCUNE mutation (comme un membre déjà absent côté SDK)
            var list = MembersByPlaylist.TryGetValue(playlistId, out var m) ? m : new List<OwnedPlaylistMember>();
            var removed = list.RemoveAll(x => x.UserId == userId) > 0;
            if (removed && SimulateOwnerRowLostAfterDelete) _ownerRowLost.Add(playlistId);
            return removed;
        }
    }

    internal sealed class FakeUserDirectory : IUserDirectory
    {
        private readonly Dictionary<string, DirectoryUser> _users = new();
        private readonly HashSet<string> _canShare = new();

        public void Add(string userId, string name, bool active = true, bool canShare = false)
        {
            _users[userId] = new DirectoryUser(userId, name, active);
            if (canShare) _canShare.Add(userId);
        }

        public IReadOnlyList<DirectoryUser> ListSelectable(string excludeUserId) =>
            _users.Values.Where(u => u.Active && u.UserId != excludeUserId).OrderBy(u => u.Name, StringComparer.Ordinal).ToList();

        public DirectoryUser? Find(string userId) => _users.TryGetValue(userId, out var u) ? u : null;

        public bool CanShare(string userId) => _canShare.Contains(userId);
    }

    internal sealed class Rig
    {
        public readonly FakeUserPageGateway Playlists = new();
        public readonly FakeShareGateway Shares;
        public readonly FakeUserDirectory Users = new();
        public readonly PlaylistLocks Locks = new();
        public readonly SeenPlaylists Seen = new();
        public readonly ListJournal Journal = new();
        public readonly FakeClock Clock = new();
        public readonly DefaultsService Defaults;
        public readonly UserPlaylistService Service;

        public Rig()
        {
            Shares = new FakeShareGateway(Playlists);
            Defaults = new DefaultsService(Playlists, Seen, Locks, Journal, "AIDE", () => 2, Clock, TimeSpan.FromMilliseconds(500));
            Service = new UserPlaylistService(Shares, Users, Playlists, Locks, Defaults, Journal, Clock, TimeSpan.FromMilliseconds(500));
            Users.Add(Owner, "Alice", canShare: true);
            Users.Add(MemberWrite, "Bob", canShare: false);
            Users.Add(MemberRead, "Carla", canShare: false);
            Users.Add(Stranger, "Dan", canShare: true);
        }
    }

}
