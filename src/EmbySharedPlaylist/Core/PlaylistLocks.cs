using System.Collections.Concurrent;

namespace EmbySharedPlaylist.Core;

/// <summary>
/// Verrou par playlist (B53) : <see cref="Monitor"/> réentrant, un objet de verrou par identifiant de playlist.
/// Règles : (1) prise avec délai (null = délai dépassé, l'appelant journalise <c>Skipped lock-busy</c>) ;
/// (2) un fil ne détient jamais deux verrous de playlist à la fois (pas d'interblocage entre playlists) — une
/// tentative lève <see cref="InvalidOperationException"/> (erreur de programmation) ; (3) le même fil peut retraverser
/// le verrou de la MÊME playlist (réentrance) ; (4) à utiliser dans du code synchrone : un Monitor est lié au fil.
/// Toujours libérer par <c>using</c>.
/// <para>
/// <b>D18/D-b (v0.5.0, #33) Décision consciente : <c>_locks</c> ne retire JAMAIS une entrée</b>, contrairement à
/// <see cref="SeenPlaylists"/> (explicitement borné, LRU 5000). Chaque identifiant de playlist un jour verrouillé laisse
/// un objet minuscule (quelques dizaines d'octets) en mémoire pour la durée de vie du processus, même après suppression
/// de la playlist. Coût réel négligeable (borné en pratique par le nombre total de playlists jamais créées sur un
/// serveur, pas un nombre pathologique). Alternative rejetée : une éviction active retirerait une entrée du
/// dictionnaire pendant qu'un fil détient encore son objet de verrou, ce qui romprait l'exclusion mutuelle pour cette
/// playlist (un nouvel appelant obtiendrait un AUTRE objet de verrou et entrerait en même temps que le premier) — le
/// coût de complexité/risque d'une purge sûre (compteur de référence, verrou du verrou) est disproportionné par
/// rapport au bénéfice. Ne pas « corriger » sans revoir cette analyse.
/// </para>
/// </summary>
public sealed class PlaylistLocks
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private sealed class Held
    {
        public string? Id;
        public int Depth;
    }

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly ThreadLocal<Held> _held = new(() => new Held());

    /// <summary>Prend le verrou de la playlist ; null si le délai est dépassé.</summary>
    public IDisposable? TryAcquire(string playlistId, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(playlistId)) throw new ArgumentException("playlistId requis", nameof(playlistId));
        var held = _held.Value!;
        if (held.Depth > 0 && !string.Equals(held.Id, playlistId, StringComparison.Ordinal))
            throw new InvalidOperationException("Un fil ne détient jamais deux verrous de playlist à la fois.");

        var gate = _locks.GetOrAdd(playlistId, _ => new object());
        if (!Monitor.TryEnter(gate, timeout)) return null;

        held.Id = playlistId;
        held.Depth++;
        return new Releaser(this, gate, held);
    }

    /// <summary>Vrai si le fil courant détient le verrou de cette playlist.</summary>
    public bool IsHeldByCurrentThread(string playlistId)
    {
        var held = _held.Value!;
        return held.Depth > 0 && string.Equals(held.Id, playlistId, StringComparison.Ordinal);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly PlaylistLocks _owner;
        private readonly object _gate;
        private readonly Held _held;
        private int _disposed;

        public Releaser(PlaylistLocks owner, object gate, Held held)
        {
            _owner = owner;
            _gate = gate;
            _held = held;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _held.Depth--;
            if (_held.Depth == 0) _held.Id = null;
            Monitor.Exit(_gate);
        }
    }
}
