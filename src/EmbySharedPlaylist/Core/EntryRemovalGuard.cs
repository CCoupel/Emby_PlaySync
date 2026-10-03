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
    private readonly Action<long, bool> _reAdd;
    private readonly Func<bool> _waitRefreshIdle;
    private readonly Action<string> _logWrongEntry;
    private readonly Action? _pause;

    /// <param name="read">Entrées courantes (lecture fraîche) ; EntryId = 0 si Emby n'en fournit pas.</param>
    /// <param name="removeByEntryId">Suppression par identifiant d'entrée (peut lever).</param>
    /// <param name="reAdd">Ré-ajout d'un ItemId (compensation ; une exception est avalée). 2ᵉ argument : skipDuplicates (vrai si l'ItemId n'avait qu'UNE entrée avant le retrait).</param>
    /// <param name="waitRefreshIdle">Attente bornée de la fin du rafraîchissement ; renvoie faux si elle a expiré (file toujours active). Une exception vaut « non confirmé ».</param>
    /// <param name="logWrongEntry">Journalise <c>Error</c> avec le détail reçu (<c>wrong-entry removed=&lt;ItemId&gt; target=&lt;ItemId&gt;</c>, ou <c>wrong-entry-unconfirmed …</c> sans ré-ajout).</param>
    /// <param name="pause">Courte pause avant la 2ᵉ lecture de confirmation ; null = aucune.</param>
    public EntryRemovalGuard(Func<IReadOnlyList<(long ItemId, long EntryId)>> read, Action<long> removeByEntryId, Action<long, bool> reAdd,
        Func<bool> waitRefreshIdle, Action<string> logWrongEntry, Action? pause = null)
    {
        _read = read;
        _removeByEntryId = removeByEntryId;
        _reAdd = reAdd;
        _waitRefreshIdle = waitRefreshIdle;
        _logWrongEntry = logWrongEntry;
        _pause = pause;
    }

    /// <summary>Compat (v1.2.2 initial) : attente sans signal d'expiration (toujours « confirmée »), ré-ajout sans skipDuplicates.</summary>
    public EntryRemovalGuard(Func<IReadOnlyList<(long ItemId, long EntryId)>> read, Action<long> removeByEntryId, Action<long> reAdd,
        Action waitRefreshIdle, Action<string> logWrongEntry, Action? pause = null)
        : this(read, removeByEntryId, (lost, _) => reAdd(lost), () => { waitRefreshIdle(); return true; }, logWrongEntry, pause)
    {
    }

    /// <summary>Compat : vrai si une entrée de la cible a été résolue et soumise à suppression (≠ <see cref="RemoveOutcome.NotFound"/>) ; faux sinon.</summary>
    public bool RemoveOne(long itemId) => Remove(itemId).Outcome != RemoveOutcome.NotFound;

    /// <summary>
    /// F3 (#59) : retrait d'UNE entrée de la cible avec résultat. <see cref="RemoveOutcome.Removed"/> : le compte de la cible a baissé
    /// (<see cref="RemoveResult.TargetRemaining"/> = entrées restantes, −1 si la relecture n'est pas fiable) ; <see cref="RemoveOutcome.NoEffect"/> :
    /// le compte de la cible est inchangé (identifiant d'entrée périmé) — <see cref="RemoveResult.OtherLost"/> vrai si, en plus, un autre média
    /// a perdu une entrée (compensation tentée, déjà journalisée <c>wrong-entry</c>) ; <see cref="RemoveOutcome.NotFound"/> : aucune entrée identifiable.
    /// </summary>
    public RemoveResult Remove(long itemId)
    {
        // F3-1 : une 1ʳᵉ attente expirée/en échec = le worker d'Emby peut être en pleine réinsertion ; la relecture post-écriture ne prouve
        // alors pas que la cible a disparu (R4a : jamais d'arrêt silencieux avec un média lu non retiré).
        var firstIdle = true;
        try { firstIdle = _waitRefreshIdle(); } catch { firstIdle = false; }
        var entries = _read();
        var entry = entries.FirstOrDefault(e => e.ItemId == itemId && e.EntryId != 0);
        if (entry.EntryId == 0) return new RemoveResult(RemoveOutcome.NotFound);

        var before = Counts(entries);
        _removeByEntryId(entry.EntryId);

        try { return Compensate(itemId, before, firstIdle); }
        catch { return new RemoveResult(RemoveOutcome.Removed, -1); /* jamais d'exception : l'écriture a eu lieu, relecture inconnue */ }
    }

    private RemoveResult Compensate(long target, Dictionary<long, int> before, bool firstIdle)
    {
        var after = Counts(_read());
        var idleConfirmed = true;
        var otherLost = false;
        if (HasLoss(before, after, target))
        {
            try { idleConfirmed = _waitRefreshIdle(); } catch { idleConfirmed = false; }
            _pause?.Invoke();
            after = Counts(_read());
        }
        // Lecture vide alors qu'il y avait plusieurs entrées : non fiable, jamais de ré-ajout à l'aveugle.
        if (after.Count == 0 && before.Count > 1) return new RemoveResult(RemoveOutcome.Removed, -1);
        foreach (var (lost, count) in before)
        {
            if (lost == target) continue;
            after.TryGetValue(lost, out var now);
            for (var missing = count - now; missing > 0; missing--)
            {
                otherLost = true;
                if (!idleConfirmed)
                {
                    // R4a : un doublon est plus tolérable qu'une perte, mais pas de faux ré-ajout tant que le worker d'Emby est actif.
                    try { _logWrongEntry($"wrong-entry-unconfirmed removed={lost} target={target}"); } catch { }
                    continue;
                }
                try { _reAdd(lost, count == 1); } catch { /* journalisé ci-dessous : l'erreur reste visible */ }
                try { _logWrongEntry($"wrong-entry removed={lost} target={target}"); } catch { }
            }
        }

        before.TryGetValue(target, out var targetBefore);
        after.TryGetValue(target, out var targetAfter);
        // Cible apparemment absente mais 1ʳᵉ attente non confirmée : reste inconnu (-1), la boucle rappelle (attente + relecture fraîche).
        return targetAfter < targetBefore
            ? new RemoveResult(RemoveOutcome.Removed, !firstIdle && targetAfter == 0 ? -1 : targetAfter, otherLost)
            : new RemoveResult(RemoveOutcome.NoEffect, targetAfter, otherLost);
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
