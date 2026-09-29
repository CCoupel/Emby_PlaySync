using System.Collections.Concurrent;

namespace EmbySharedPlaylist.Core;

/// <summary>
/// Verrou par couple (utilisateur, média) (v1.2.0, D21 §5, B7) : sérialise les écritures du plugin sur la donnée
/// utilisateur d'un MEMBRE (<c>MarkPlayed</c> du lu, R4b ; <c>SetPosition</c> de la position, R10) que les deux flux
/// d'événements (<c>UserDataSaved</c> et <c>ISessionManager</c>) peuvent déclencher en parallèle, sans ordre garanti.
/// Sans lui, deux cycles lecture-modification-écriture concurrents chez le même membre s'écraseraient (perte du lu ou
/// de la position). Même patron que <see cref="PlaylistLocks"/> : <see cref="Monitor"/> réentrant, prise avec délai
/// (null = délai dépassé, l'appelant journalise <c>Skipped lock-busy</c>), un fil ne détient jamais deux verrous
/// (utilisateur, média) à la fois (<see cref="InvalidOperationException"/>), à utiliser dans du code synchrone.
/// C'est le verrou le PLUS INTERNE : toujours pris sous le verrou de playlist, jamais avant, et jamais imbriqué avec
/// lui-même. La relecture de la donnée, l'enregistrement anti-écho (<see cref="PluginWriteTracker"/>) et l'écriture
/// se font DANS ce verrou. Hypothèse : l'écho <c>UserDataSaved</c> peut arriver APRÈS la libération du verrou ; c'est pourquoi
/// <see cref="PluginWriteTracker"/> est un COMPTEUR (1 écriture enregistrée ↔ 1 consommation), pas un ensemble (B12).
/// <para>
/// Comme <see cref="PlaylistLocks"/>, <c>_locks</c> ne retire jamais d'entrée (l'éviction romprait l'exclusion mutuelle
/// d'un fil qui détient encore l'objet) ; coût : un objet minuscule par couple (utilisateur, média) un jour verrouillé,
/// borné en pratique par (membres de playlists partagées) × (médias lus).
/// </para>
/// </summary>
public sealed class UserItemLocks
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private sealed class Held
    {
        public string? Key;
        public int Depth;
    }

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly ThreadLocal<Held> _held = new(() => new Held());

    /// <summary>Prend le verrou du couple ; null si le délai est dépassé.</summary>
    public IDisposable? TryAcquire(string userId, string itemId, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(userId)) throw new ArgumentException("userId requis", nameof(userId));
        if (string.IsNullOrEmpty(itemId)) throw new ArgumentException("itemId requis", nameof(itemId));
        var key = userId + "|" + itemId;
        var held = _held.Value!;
        if (held.Depth > 0 && !string.Equals(held.Key, key, StringComparison.Ordinal))
            throw new InvalidOperationException("Un fil ne détient jamais deux verrous (utilisateur, média) à la fois.");

        var gate = _locks.GetOrAdd(key, _ => new object());
        if (!Monitor.TryEnter(gate, timeout)) return null;

        held.Key = key;
        held.Depth++;
        return new Releaser(gate, held);
    }

    /// <summary>Vrai si le fil courant détient le verrou de ce couple.</summary>
    public bool IsHeldByCurrentThread(string userId, string itemId)
    {
        var held = _held.Value!;
        return held.Depth > 0 && string.Equals(held.Key, userId + "|" + itemId, StringComparison.Ordinal);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly object _gate;
        private readonly Held _held;
        private int _disposed;

        public Releaser(object gate, Held held)
        {
            _gate = gate;
            _held = held;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _held.Depth--;
            if (_held.Depth == 0) _held.Key = null;
            Monitor.Exit(_gate);
        }
    }
}
