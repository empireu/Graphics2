using System.Collections.Concurrent;
using System.Diagnostics;

namespace WinterGame.Jobs;

/// <summary>
///     Staging buffer for <see cref="IWorkExecutor"/>, pairing a per-slot input with a per-slot output.
///     The dispatcher owns a slot's lifetime. It allocates the slot, writes the input, dispatches a work item carrying the slot, and later reads the output and frees the slot.
///     A worker only reads the input of the slot it was handed and writes that slot's output.
/// </summary>
public sealed class WorkExecutorTransformScratch<TInput, TOutput>
{
    /// <summary>
    ///     Whether a slot's input is reset to <c>default</c> when the slot is freed.
    /// </summary>
    public bool ClearInputToDefault { get; }

    private enum ValidationState
    {
        /// <summary>
        ///     The slot has no ongoing operation associated with it.
        /// </summary>
        Free,
        /// <summary>
        ///     The slot has been allocated for input, but no output has been written by the worker yet.
        /// </summary>
        AllocatedInput,
        /// <summary>
        ///     The worker has written the output, and the dispatcher will read and free the slot later.
        /// </summary>
        OutputWritten
    }

    private readonly TInput[] _input;
    private readonly TOutput[] _output;
    private readonly ConcurrentStack<int> _freeList = new();

#if DEBUG
    private readonly ValidationState[] _validation;
#endif

    public WorkExecutorTransformScratch(TInput[] inputStaging, bool clearInputToDefault)
    {
        if (inputStaging == null || inputStaging.Length == 0)
        {
            throw new ArgumentException("Provided input staging arena is invalid", nameof(inputStaging));
        }

        ClearInputToDefault = clearInputToDefault;

        _input = inputStaging;
        _output = new TOutput[inputStaging.Length];

        for (var index = _input.Length - 1; index >= 0; index--)
        {
            _freeList.Push(index);
        }

#if DEBUG
        _validation = new ValidationState[inputStaging.Length];
#endif

        Capacity = inputStaging.Length;
    }

    public int Capacity { get; }

    [Conditional("DEBUG")]
    private void ValidateSlot(int slot)
    {
        Debug.Assert(slot >= 0 && slot < Capacity, $"Tried to access slot {slot}, which is out-of-range");
    }

    /// <summary>
    ///     Takes a slot for the input of a work item.
    ///     False means every slot is taken.
    /// </summary>
    public bool TryAllocateInputSlot(out int slot)
    {
        if (!_freeList.TryPop(out slot))
        {
            return false;
        }

#if DEBUG
        if (_validation[slot] != ValidationState.Free)
        {
            throw new InvalidOperationException("A newly allocated slot was not marked as free");
        }

        _validation[slot] = ValidationState.AllocatedInput;
#endif

        return true;
    }

    /// <summary>
    ///     Takes a slot for the input of a work item, throwing when none is free.
    /// </summary>
    public ref TInput AllocateInputSlot(out int slot)
    {
        if (!TryAllocateInputSlot(out slot))
        {
            throw new InvalidOperationException("Transform scratch is out of space");
        }

        return ref _input[slot];
    }

    /// <summary>
    ///     Gets the input of a slot.
    ///     The dispatcher calls this to write the input before dispatching, and the worker calls it to read it.
    /// </summary>
    public ref TInput GetInputSlot(int slot)
    {
        ValidateSlot(slot);

#if DEBUG
        if (_validation[slot] != ValidationState.AllocatedInput)
        {
            throw new InvalidOperationException($"Tried to read the input of a slot that was {_validation[slot]}");
        }
#endif

        return ref _input[slot];
    }

    /// <summary>
    ///     Writes the result into the output of a slot.
    /// </summary>
    public void WriteOutput(int slot, in TOutput output)
    {
        ValidateSlot(slot);

#if DEBUG
        if (_validation[slot] != ValidationState.AllocatedInput)
        {
            throw new InvalidOperationException($"Tried to write the output of a slot that was {_validation[slot]}");
        }

        _validation[slot] = ValidationState.OutputWritten;
#endif

        _output[slot] = output;
    }

    /// <summary>
    ///     Reads the output of a slot without freeing it.
    ///     A slot whose task never wrote an output reads as <c>default</c>, which is the no-result value.
    /// </summary>
    public TOutput GetOutput(int slot)
    {
        ValidateSlot(slot);

#if DEBUG
        if (_validation[slot] == ValidationState.Free)
        {
            throw new InvalidOperationException("Tried to read the output of a slot that was not allocated");
        }
#endif

        return _output[slot];
    }

    /// <summary>
    ///     Returns a slot to the free list, resetting its output to <c>default</c>.
    /// </summary>
    public void Free(int slot)
    {
        ValidateSlot(slot);

#if DEBUG
        if (_validation[slot] == ValidationState.Free)
        {
            throw new InvalidOperationException("Tried to free a slot that was already free");
        }

        _validation[slot] = ValidationState.Free;
#endif

        _output[slot] = default!;

        if (ClearInputToDefault)
        {
            _input[slot] = default!;
        }

        _freeList.Push(slot);
    }

    /// <summary>
    ///     Reads the output of a slot and returns the slot to the free list.
    /// </summary>
    public TOutput ReadOutputAndFree(int slot)
    {
        var output = GetOutput(slot);
        Free(slot);
        return output;
    }
}
