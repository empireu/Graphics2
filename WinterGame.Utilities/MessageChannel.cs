namespace WinterGame.Utilities;

/// <summary>
///     Messages one thread sends to another, in order, under one lock.
///     The lock also hands over whatever a message refers to, so the taking thread sees everything the sending thread wrote before it posted.
/// </summary>
public sealed class MessageChannel<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _messages = [];

    /// <summary>
    ///     Sends every message in <paramref name="messages"/>, in order. The list is left as it was.
    /// </summary>
    public void Post(List<T> messages)
    {
        lock (_lock)
        {
            _messages.AddRange(messages);
        }
    }

    /// <summary>
    ///     Adds every message sent since the last take to <paramref name="destination"/>, in the order they were sent.
    /// </summary>
    public void Take(List<T> destination)
    {
        lock (_lock)
        {
            destination.AddRange(_messages);
            _messages.Clear();
        }
    }
}
