namespace WinterGame.World.Lighting;

/// <summary>
///     A handle to a section column's record in a <see cref="SkyColumnAllocator"/>, holding the record's index.
/// </summary>
internal readonly struct SkyColumnRecord(int index)
{
    public readonly int Index = index;
}

/// <summary>
///     The part of a section column's record besides its floors.
/// </summary>
internal struct SkyColumnHeader
{
    /// <summary>
    ///     A bit per brick column, set when its floor is found. A floor is found once the brick below it is known to block.
    ///     When clear, the column's walk waits at its floor for the section below to become ready.
    /// </summary>
    public ulong FoundColumns;

    /// <summary>
    ///     The lowest of the record's floors.
    /// </summary>
    public int MinimumFloor;

    /// <summary>
    ///     The highest of the record's floors.
    /// </summary>
    public int MaximumFloor;
}

/// <summary>
///     Stores the sky column records in pages of numbers, one page list for the floors and one for the headers.
///     Records are handed out in order and never freed.
/// </summary>
internal sealed class SkyColumnAllocator
{
    private const int PageLog = 10;

    /// <summary>
    ///     The number of records per page.
    /// </summary>
    private const int PageSize = 1 << PageLog;

    private const int PageMask = PageSize - 1;

    private readonly List<int[]> _floorPages = [];
    private readonly List<SkyColumnHeader[]> _headerPages = [];

    public int Count { get; private set; }

    /// <summary>
    ///     Adds a record with every floor at <paramref name="floor"/> and no floor found.
    /// </summary>
    public SkyColumnRecord Allocate(int floor)
    {
        if (Count == _headerPages.Count * PageSize)
        {
            _floorPages.Add(new int[PageSize * SkyBlockers.ColumnsPerSection]);
            _headerPages.Add(new SkyColumnHeader[PageSize]);
        }

        var record = new SkyColumnRecord(Count++);

        GetFloors(record).Fill(floor);
        GetHeader(record) = new SkyColumnHeader { MinimumFloor = floor, MaximumFloor = floor };

        return record;
    }

    /// <summary>
    ///     The record's floors, one per brick column, indexed like <see cref="SkyBlockers.ColumnsPerSection"/>.
    /// </summary>
    public Span<int> GetFloors(SkyColumnRecord record)
    {
        return _floorPages[record.Index >> PageLog].AsSpan((record.Index & PageMask) * SkyBlockers.ColumnsPerSection, SkyBlockers.ColumnsPerSection);
    }

    public ref SkyColumnHeader GetHeader(SkyColumnRecord record)
    {
        return ref _headerPages[record.Index >> PageLog][record.Index & PageMask];
    }
}
