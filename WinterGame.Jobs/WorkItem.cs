using System.Diagnostics;

namespace WinterGame.Jobs;

internal static class WorkItem
{
    public const int MaxProviderId = (1 << 8) - 1;
    public const int MaxJobSlot = (1 << 16) - 1;
    public const int MaxPayloadSlot = (1 << 24) - 1;

    private const int ProviderShift = 40;
    private const int JobSlotShift = 24;

    public static long Pack(int providerId, int jobSlot, int payloadSlot)
    {
        Debug.Assert(providerId is >= 0 and <= MaxProviderId, $"The provider id {providerId} does not fit");
        Debug.Assert(jobSlot is >= 0 and <= MaxJobSlot, $"The job slot {jobSlot} does not fit");
        Debug.Assert(payloadSlot is >= 0 and <= MaxPayloadSlot, $"The payload slot {payloadSlot} does not fit");

        return ((long)providerId << ProviderShift) | ((long)jobSlot << JobSlotShift) | (uint)payloadSlot;
    }

    public static int ProviderId(long workItem)
    {
        return (int)((ulong)workItem >> ProviderShift) & MaxProviderId;
    }

    public static int JobSlot(long workItem)
    {
        return (int)((ulong)workItem >> JobSlotShift) & MaxJobSlot;
    }

    public static int PayloadSlot(long workItem)
    {
        return (int)workItem & MaxPayloadSlot;
    }
}
