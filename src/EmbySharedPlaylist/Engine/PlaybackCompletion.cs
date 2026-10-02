namespace EmbySharedPlaylist.Engine;

/// <summary>
/// Repli de fin de lecture (spike U15) si <c>PlayedToCompletion</c> n'est pas fiable : fonction PURE (testable hors SDK).
/// Emby a déjà appliqué la fin de lecture au déclencheur (lu = vrai ET position remise à 0) et l'arrêt est près de la fin du média.
/// </summary>
public static class PlaybackCompletion
{
    /// <summary>Part minimale de la durée du média à laquelle l'arrêt doit se produire.</summary>
    public const double FallbackRatio = 0.9;

    public static bool LooksCompleted(long? runtimeTicks, long? stopPositionTicks, bool? triggerPlayed, long? triggerStoredPosition)
    {
        if (runtimeTicks is not long total || total <= 0) return false;
        if (stopPositionTicks is not long pos || pos < total * FallbackRatio) return false;
        return triggerPlayed == true && triggerStoredPosition == 0;
    }
}
