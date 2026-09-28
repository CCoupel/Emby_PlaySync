namespace EmbySharedPlaylist.Core;

/// <summary>
/// Drapeau de réentrance (B53) : actif pendant toute écriture du plugin. Un gestionnaire d'événement appelé
/// pendant nos propres écritures (les événements d'Emby sont émis de façon synchrone) consulte <see cref="Active"/>
/// et retourne aussitôt (<c>Skipped reentrant</c>). Propre au contexte d'exécution (AsyncLocal) : une écriture d'un
/// autre fil ou d'une autre requête n'est pas affectée ; toujours restauré par <c>using</c>.
/// </summary>
public static class WriteScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool Active => Depth.Value > 0;

    public static IDisposable Enter()
    {
        var previous = Depth.Value;
        Depth.Value = previous + 1;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly int _previous;
        private int _disposed;

        public Scope(int previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Depth.Value = _previous;
        }
    }
}
