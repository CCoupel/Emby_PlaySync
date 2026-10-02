namespace EmbySharedPlaylist.Core;

/// <summary>
/// Drapeau de réentrance (B53) : actif pendant toute écriture du plugin. Un gestionnaire d'événement appelé
/// pendant nos propres écritures (les événements d'Emby sont émis de façon synchrone) consulte <see cref="Active"/>
/// et retourne aussitôt (<c>Skipped reentrant</c>). Propre au contexte d'exécution (AsyncLocal) : une écriture d'un
/// autre fil ou d'une autre requête n'est pas affectée ; toujours restauré par <c>using</c>.
/// </summary>
public static class WriteScope
{
    // v1.2.2 (#59, F1) : jeton à identité. Task.Run / les workers d'Emby capturent l'ExecutionContext de l'appelant ; avec un
    // simple compteur, la copie ainsi capturée restait « active » pour toute la vie du worker (fuite). Ici, chaque Enter()
    // crée un jeton chaîné au parent, révoqué au Dispose : toute copie du contexte devient inactive dès la sortie du scope,
    // tandis que les tâches attendues DANS le scope restent actives.
    private static readonly AsyncLocal<ScopeToken?> Current = new();

    public static bool Active
    {
        get
        {
            for (var t = Current.Value; t != null; t = t.Parent)
                if (!t.Revoked) return true;
            return false;
        }
    }

    public static IDisposable Enter()
    {
        var previous = Current.Value;
        var token = new ScopeToken(previous);
        Current.Value = token;
        return new Scope(token, previous);
    }

    private sealed class ScopeToken
    {
        private volatile bool _revoked;
        public ScopeToken? Parent { get; }
        public bool Revoked => _revoked;
        public ScopeToken(ScopeToken? parent) => Parent = parent;
        public void Revoke() => _revoked = true;
    }

    private sealed class Scope : IDisposable
    {
        private readonly ScopeToken _token;
        private readonly ScopeToken? _previous;
        private int _disposed;

        public Scope(ScopeToken token, ScopeToken? previous)
        {
            _token = token;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _token.Revoke();
            Current.Value = _previous;
        }
    }
}
