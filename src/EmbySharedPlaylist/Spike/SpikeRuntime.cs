namespace EmbySharedPlaylist.Spike;

/// <summary>Singletons partagés entre le listener (IServerEntryPoint) et le service HTTP (instancié par requête).</summary>
public static class SpikeRuntime
{
    public static EventJournal Journal { get; } = new();
    public static PluginWriteTracker Tracker { get; } = new();
}
