namespace EmbySharedPlaylist.Spike;

/// <summary>Singletons partagés entre le listener (IServerEntryPoint) et le service HTTP (instancié par requête).</summary>
public static class SpikeRuntime
{
    public static EventJournal Journal { get; } = new();
    public static PluginWriteTracker Tracker { get; } = new();

    /// <summary>Journal à deux canaux, renseigné au démarrage par le listener (null avant : aucune ligne n'est perdue, elle est ignorée).</summary>
    public static SpikeLog? Log { get; set; }
}
