using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.World.Storage;

namespace WinterGame.World;

public enum SectionStatus
{
    /// <summary>
    ///     The section is not loaded.
    /// </summary>
    None,
    /// <summary>
    ///     The section is being generated off-thread.
    /// </summary>
    InFlight,
    /// <summary>
    ///     The section is loaded fully.
    /// </summary>
    Ready
}

public sealed class Level
{
    private struct SectionState
    {
        public enum SectionStatus
        {
            None,
            /// <summary>
            ///     The section is being generated off-thread.
            /// </summary>
            Generating,
            /// <summary>
            ///     The section is ready and in the arena.
            /// </summary>
            Ready,
            /// <summary>
            ///     The section is no longer being referenced by any loader, and will expire.
            /// </summary>
            Expiring
        }

        public SectionStatus Status;
        public Job GenerationJob;
        public int ExpiryTicks;
    }
    
    private readonly JobScheduler _worldgenScheduler;
    
    private readonly SectionArena _terrain = new();
    private readonly Dictionary<ulong, SectionState> _stateBySection = [];
    
    public Level(JobScheduler worldgenScheduler)
    {
        _worldgenScheduler = worldgenScheduler;
    }
    
    public void UpdateWorldgen(BoundingBox3i playerViewField)
    {
        
            
        
    }
    
}