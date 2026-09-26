using EmbySharedPlaylist.Core;
using EmbySharedPlaylist.Emby;

namespace EmbySharedPlaylist.Spike;

/// <summary>
/// Singletons du spike (temporaire, retiré avec Spike/* — #15) : ils DÉLÈGUENT à <see cref="PluginRuntime"/> pour que la sonde
/// et le moteur partagent le même journal (Spike/Events voit aussi les décisions du moteur), le même verrou par playlist
/// et le même suivi des écritures du plugin.
/// </summary>
public static class SpikeRuntime
{
    public static EventJournal Journal => PluginRuntime.JournalStore;
    public static PluginWriteTracker Tracker => PluginRuntime.Tracker;

    /// <summary>Verrou par playlist partagé (B53) : sonde U11, moteur, passe planifiée et première détection.</summary>
    public static PlaylistLocks Locks => PluginRuntime.Locks;

    /// <summary>Journal à deux canaux, renseigné au démarrage par le listener (null avant : aucune ligne n'est perdue, elle est ignorée).</summary>
    public static Log? Log { get; set; }
}
