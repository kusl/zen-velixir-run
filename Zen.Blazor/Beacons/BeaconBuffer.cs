using Microsoft.Extensions.Options;

namespace Zen.Blazor.Beacons;

public readonly record struct BufferedBeacon(BeaconEntry Entry, bool Persisted);

public readonly record struct BufferStats(int Entries, long Bytes, int Unsaved, long Dropped);

public sealed class BeaconBuffer(IOptions<BeaconOptions> options, ILogger<BeaconBuffer> logger)
{
    private readonly BeaconOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly LinkedList<Slot> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<Slot>> _index = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private long _bytes;
    private int _unsaved;
    private long _dropped;

    public event Action? Changed;

    public void Add(BeaconEntry entry)
    {
        lock (_gate)
        {
            _index[entry.Id] = _order.AddLast(new Slot(entry));
            _bytes += entry.Size;
            _unsaved++;

            while (_order.First is { } oldest && oldest != _order.Last
                   && (_bytes > _options.MemoryBytes || _order.Count > _options.MemoryEntries))
            {
                Remove(oldest);
                if (!oldest.Value.Persisted)
                {
                    _unsaved--;
                    _dropped++;
                }
            }
        }

        Signal();
        Notify();
    }

    public bool TryGet(Guid id, out BeaconEntry entry)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(id, out var node))
            {
                entry = node.Value.Entry;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public IReadOnlyList<BeaconEntry> TakeUnsaved(int max)
    {
        var batch = new List<BeaconEntry>(Math.Min(max, 64));
        lock (_gate)
        {
            for (var node = _order.First; node is not null && batch.Count < max; node = node.Next)
            {
                if (!node.Value.Persisted)
                {
                    batch.Add(node.Value.Entry);
                }
            }
        }

        return batch;
    }

    public void MarkPersisted(IEnumerable<BeaconEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (_index.TryGetValue(entry.Id, out var node) && !node.Value.Persisted)
                {
                    node.Value.Persisted = true;
                    _unsaved--;
                }
            }
        }
    }

    public IReadOnlyList<BufferedBeacon> Snapshot()
    {
        lock (_gate)
        {
            var items = new BufferedBeacon[_order.Count];
            var i = 0;
            foreach (var slot in _order)
            {
                items[i++] = new BufferedBeacon(slot.Entry, slot.Persisted);
            }

            return items;
        }
    }

    public BufferStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new BufferStats(_order.Count, _bytes, _unsaved, _dropped);
            }
        }
    }

    public async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Remove(LinkedListNode<Slot> node)
    {
        _order.Remove(node);
        _index.Remove(node.Value.Entry.Id);
        _bytes -= node.Value.Entry.Size;
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void Notify()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Beacon buffer subscriber failed");
            }
        }
    }

    private sealed class Slot(BeaconEntry entry)
    {
        public BeaconEntry Entry { get; } = entry;
        public bool Persisted { get; set; }
    }
}
