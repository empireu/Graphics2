namespace WinterGame;

/// <summary>
///     The sections one view draws, as a range of <see cref="SectionGeometry"/>'s runs of commands. Written by <see cref="SectionGeometry.AddView"/> and valid only in the frame it was written for.
/// </summary>
internal readonly struct SectionDrawList(int firstRun, int runCount, int sectionCount, long indexCount)
{
    public readonly int FirstRun = firstRun;

    /// <summary>
    ///     The indirect draws the list issues, one per run of sections sharing a pair of buffers.
    /// </summary>
    public readonly int RunCount = runCount;

    public readonly int SectionCount = sectionCount;

    public readonly long IndexCount = indexCount;
}
