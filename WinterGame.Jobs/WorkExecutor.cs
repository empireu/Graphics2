namespace WinterGame.Jobs;

public readonly struct WorkExecutionContext(Job handle, int threadId)
{
    public readonly Job Handle = handle;
    public readonly int ThreadId = threadId;
}

public interface IWorkExecutor
{
    /// <summary>
    ///     Executes a work item.
    ///     The staging buffer for the input and the output buffers are managed by the executor, ideally, or owned by some other greater system.
    ///     The point is, a caller can get a slot keyed by an integer, load up any input data, then execute the job, passing in the slot.
    ///     Any outputs can go into that slot, which the caller will later read.
    /// </summary>
    public void Execute(in WorkExecutionContext context, int item);
}