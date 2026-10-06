namespace WinterGame.World.Level;

/// <summary>
///     Section counts of a <see cref="WorldLevel"/>, as of its last <see cref="WorldLevel.Update"/>.
/// </summary>
public readonly struct LevelStats(int loadedSections, int generatingSections, int pendingSections, int unloadQueueCount)
{
    /// <summary>
    ///     Sections in the level's arena, which are the ones a read can see.
    /// </summary>
    public readonly int LoadedSections = loadedSections;

    /// <summary>
    ///     Sections with a generation job in flight, including a refused publish waiting to be retried.
    /// </summary>
    public readonly int GeneratingSections = generatingSections;

    /// <summary>
    ///     Sections inside some ticket's load shape that wait for a free generation slot. A section two load shapes hold is counted once for each.
    /// </summary>
    public readonly int PendingSections = pendingSections;

    /// <summary>
    ///     Released sections waiting to be destroyed.
    /// </summary>
    public readonly int UnloadQueueCount = unloadQueueCount;
}
