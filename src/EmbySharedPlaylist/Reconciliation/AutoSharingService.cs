using System.Diagnostics;
using EmbySharedPlaylist.Core;

namespace EmbySharedPlaylist.Reconciliation;

/// <summary>Bilan d'une passe de permission (#26). Nommé distinctement de <see cref="PassResult"/> (playlists) : même namespace.</summary>
public sealed record PermissionPassResult(int Users, int Enabled, int AlreadyEnabled, long DurationMs);

/// <summary>
/// Pose automatiquement <c>Policy.AllowSharingPersonalItems=true</c> pour tous les utilisateurs (#26), sous
/// l'interrupteur <c>AutoEnableSharing</c> (relu à CHAQUE appel : config live, pas figée à la construction).
/// Interrupteur faux : retour immédiat, AUCUNE écriture, AUCUNE entrée de journal (rien n'est tenté, D-c). Sinon : itère
/// tous les utilisateurs connus, une exception par utilisateur n'arrête pas les suivants (même discipline que
/// <see cref="ReconciliationService"/>/<c>ReadRemovalEngine</c>). Utilisateur déjà actif : no-op silencieux (compté, pas
/// journalisé individuellement) ; jamais de révocation (D-e, cohérent avec R11/D11 : aucune mémoire d'un décochage
/// manuel, réappliqué à la passe suivante).
/// <para>
/// Pas de verrou par utilisateur : chaque utilisateur est indépendant (aucune ressource partagée entre deux comptes,
/// contrairement à une playlist) et <see cref="IUserPolicyGateway.EnableSharingIfNeeded"/> est intrinsèquement
/// idempotent. Pas de budget de latence : cette passe tourne sur le fil de fond de la tâche planifiée
/// (<c>ReconciliationTask</c>), jamais sur un fil de requête/événement synchrone dont un appelant attendrait la
/// réponse — contrairement aux moteurs déclenchés par un événement de lecture (#12/#20/#45), aucune latence externe à
/// protéger ici (D-a).
/// </para>
/// </summary>
public sealed class AutoSharingService
{
    private readonly IUserPolicyGateway _policy;
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly Func<bool> _autoEnableSharing;

    public AutoSharingService(IUserPolicyGateway policy, IJournal journal, IClock clock, Func<bool> autoEnableSharing)
    {
        _policy = policy;
        _journal = journal;
        _clock = clock;
        _autoEnableSharing = autoEnableSharing;
    }

    public PermissionPassResult RunPass(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!_autoEnableSharing()) return new PermissionPassResult(0, 0, 0, sw.ElapsedMilliseconds); // D-c : rien tenté, rien journalisé

        IReadOnlyList<string> userIds;
        try
        {
            userIds = _policy.AllUserIds();
        }
        catch (Exception ex)
        {
            Journal(JournalEntries.ErrorEntry(_clock, null, ex));
            return new PermissionPassResult(0, 0, 0, sw.ElapsedMilliseconds);
        }

        var enabled = 0;
        var already = 0;
        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Apply(userId)) enabled++;
                else already++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Journal(JournalEntries.ErrorEntry(_clock, null, ex)); // un utilisateur en erreur n'arrête pas les suivants
            }
        }

        var result = new PermissionPassResult(userIds.Count, enabled, already, sw.ElapsedMilliseconds);
        Journal(JournalEntries.Of(_clock, "PermissionPass", null,
            $"users={result.Users} enabled={result.Enabled} alreadyEnabled={result.AlreadyEnabled} durationMs={result.DurationMs}"));
        return result;
    }

    /// <summary>Même garde d'interrupteur, pour un seul utilisateur (création immédiate, #26/D-d).</summary>
    public void OnUserCreated(string userId)
    {
        if (!_autoEnableSharing()) return; // D-c
        try
        {
            Apply(userId);
        }
        catch (Exception ex)
        {
            Journal(JournalEntries.ErrorEntry(_clock, null, ex));
        }
    }

    /// <returns>Vrai si une écriture a eu lieu (journalisée), faux si déjà activée ou utilisateur inconnu.</returns>
    private bool Apply(string userId)
    {
        if (!_policy.EnableSharingIfNeeded(userId)) return false;
        var entry = JournalEntries.Of(_clock, "PermissionPosed", null, null);
        entry.UserId = userId;
        Journal(entry);
        return true;
    }

    private void Journal(JournalEntry entry)
    {
        try { _journal.Add(entry); } catch { /* le journal ne doit jamais faire échouer le traitement */ }
    }
}
