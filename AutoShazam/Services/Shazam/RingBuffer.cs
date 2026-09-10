namespace AutoShazam.Services.Shazam;

/// <summary>
/// Fixed-size circular buffer with Python-list-style negative/overflowing index wraparound
/// (index is taken modulo the buffer size in both directions), matching the semantics relied
/// upon by the ported Shazam signature algorithm.
/// </summary>
internal sealed class RingBuffer<T>
{
    private readonly T[] _data;

    public RingBuffer(int size, Func<T> factory)
    {
        Size = size;
        _data = new T[size];
        for (int i = 0; i < size; i++)
        {
            _data[i] = factory();
        }
    }

    public int Size { get; }

    public int Position { get; set; }

    public int NumWritten { get; private set; }

    public T this[int index]
    {
        get => _data[Wrap(index)];
        set => _data[Wrap(index)] = value;
    }

    public void Append(T value)
    {
        _data[Position] = value;
        Position = (Position + 1) % Size;
        NumWritten++;
    }

    private int Wrap(int index)
    {
        int m = index % Size;
        return m < 0 ? m + Size : m;
    }
}
