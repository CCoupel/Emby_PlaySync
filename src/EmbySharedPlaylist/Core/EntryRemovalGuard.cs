namespace EmbySharedPlaylist.Core;

/// <summary>
/// v1.2.2 (#59, F2-b) : retrait d'une entrée de playlist « à l'identifiant d'entrée » rendu sûr. Les identifiants d'entrée d'Emby
/// sont renumérotés par son worker de rafraîchissement : un identifiant périmé peut désigner un AUTRE média. Séquence :
/// attente de fin de rafraîchissement, lecture au plus près de l'écriture, suppression, relecture ; si un média autre que la cible
/// a perdu une entrée (multiensemble des ItemId avant/après), la perte est CONFIRMÉE (attente + 2ᵉ lecture, la réinsertion du worker
/// n'étant pas atomique) puis compensée par ré-ajout de l'ItemId et journal <c>Error wrong-entry</c>.
/// Pure (délégués) : aucun type Emby.
/// </summary>
public sealed class EntryRemovalGuard
{
    private readonly Func<IReadOnlyList<(long ItemId, long EntryId)>> _read;
    private readonly Action<long> _removeByEntryId;
    private readonly Action<long> _reAdd;
    private readonly Action _waitRefreshIdle;
    private readonly Action<string> _logWrongEntry;
    private readonly Action? _pause;

    /// <param name="read">Entrées courantes (lecture fraîche) ; EntryId = 0 si Emby n'en fournit pas.</param>
    /// <param name="removeByEntryId">Suppression par identifiant d'entrée (peut lever).</param>
    /// <param name="reAdd">Ré-ajout d'un ItemId (compensation ; une exception est avalée).</param>
    /// <param name="waitRefreshIdle">Attente bornée de la fin du rafraîchissement ; peut être no-op.</param>
    /// <param name="logWrongEntry">Journalise <c>Error</c> avec le détail reçu (<c>wrong-entry removed=&lt;ItemId&gt; target=&lt;ItemId&gt;</c>).</param>
    /// <param name="pause">Courte pause avant la 2ᵉ lecture de confirmation ; null = aucune.</param>
    public EntryRemovalGuard(Func<IReadOnlyList<(long ItemId, long EntryId)>> read, Action<long> removeByEntryId, Action<long> reAdd,
        Action waitRefreshIdle, Action<string> logWrongEntry, Action? pause = null)
    {
        _read = read;
        _removeByEntryId = removeByEntryId;
        _reAdd = reAdd;
        _waitRefreshIdle = waitRefreshIdle;
        _logWrongEntry = logWrongEntry;
        _pause = pause;
    }

    /// <summary>Vrai si une entrée de la cible a été résolue et supprimée (la boucle appelante relit et rappelle) ; faux si la cible n'a aucune entrée identifiable.</summary>
    public bool RemoveOne(long itemId)
    {
        try { _waitRefreshIdle(); } catch { /* confort */ }
        var entries = _read();
        var entry = entries.FirstOrDefault(e => e.ItemId == itemId && e.EntryId != 0);
        if (entry.EntryId == 0) return false;

        var before = Counts(entries);
        _removeByEntryId(entry.EntryId);

        try { Compensate(itemId, before); } catch { /* jamais d'exception : le retrait a eu lieu */ }
        return true;
    }

    private void Compensate(long target, Dictionary<long, int> before)
    {
        var after = Counts(_read());
        if (HasLoss(before, after, target))
        {
            try { _waitRefreshIdle(); } catch { }
            _pause?.Invoke();
            after = Counts(_read());
        }
        // Lecture vide alors qu'il y avait plusieurs entrées : non fiable, jamais de ré-ajout à l'aveugle.
        if (after.Count == 0 && before.Count > 1) return;
        foreach (var (lost, count) in before)
        {
            if (lost == target) continue;
            after.TryGetValue(lost, out var now);
            for (var missing = count - now; missing > 0; missing--)
            {
                try { _reAdd(lost); } catch { /* journalisé ci-dessous : l'erreur reste visible */ }
                try { _logWrongEntry($"wrong-entry removed={lost} target={target}"); } catch { }
            }
        }
    }

    private static bool HasLoss(Dictionary<long, int> before, Dictionary<long, int> after, long target) =>
        before.Any(b => b.Key != target && (after.TryGetValue(b.Key, out var n) ? n : 0) < b.Value);

    private static Dictionary<long, int> Counts(IReadOnlyList<(long ItemId, long EntryId)> entries)
    {
        var counts = new Dictionary<long, int>();
        foreach (var e in entries) counts[e.ItemId] = counts.TryGetValue(e.ItemId, out var n) ? n + 1 : 1;
        return counts;
    }
}
