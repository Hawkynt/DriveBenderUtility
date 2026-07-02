namespace DivisonM.Vfs.Caching;

/// <summary>
/// Replacement policy of a cache (FR-EVICT). All ten §6.5 algorithms implement this one
/// contract so a cache instance can swap policy without affecting correctness — only hit
/// rate. Implementations are not thread-safe; the owning cache serialises access.
/// </summary>
public interface ICacheEvictionPolicy<TKey> where TKey : class {
  /// <summary>Records a hit on an existing entry.</summary>
  void OnAccess(TKey key);

  /// <summary>Records the admission of a new entry.</summary>
  void OnInsert(TKey key);

  /// <summary>Picks and removes the entry to evict; null when empty.</summary>
  TKey? SelectVictim();

  /// <summary>Forgets an entry that was removed for external reasons (invalidation, delete).</summary>
  void Remove(TKey key);

  int Count { get; }
}

public static class EvictionPolicyFactory {
  public static ICacheEvictionPolicy<TKey> Create<TKey>(EvictionPolicy policy, int capacityHint = 1024) where TKey : class => policy switch {
    EvictionPolicy.Lru => new LruPolicy<TKey>(),
    EvictionPolicy.Mru => new MruPolicy<TKey>(),
    EvictionPolicy.Fifo => new FifoPolicy<TKey>(),
    EvictionPolicy.Random => new RandomPolicy<TKey>(),
    EvictionPolicy.Lfu => new LfuPolicy<TKey>(),
    EvictionPolicy.Clock => new ClockPolicy<TKey>(),
    EvictionPolicy.ClockPro => new ClockProPolicy<TKey>(capacityHint),
    EvictionPolicy.Slru => new SlruPolicy<TKey>(capacityHint),
    EvictionPolicy.TwoQueue => new TwoQueuePolicy<TKey>(capacityHint),
    EvictionPolicy.Arc => new ArcPolicy<TKey>(capacityHint),
    _ => throw new ArgumentOutOfRangeException(nameof(policy)),
  };
}

/// <summary>Least-recently-used: victim is the entry untouched for longest.</summary>
public sealed class LruPolicy<TKey> : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly LinkedList<TKey> _order = [];
  private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = [];

  public int Count => this._nodes.Count;

  public void OnAccess(TKey key) {
    if (!this._nodes.TryGetValue(key, out var node))
      return;

    this._order.Remove(node);
    this._order.AddLast(node);
  }

  public void OnInsert(TKey key) {
    if (this._nodes.ContainsKey(key)) {
      this.OnAccess(key);
      return;
    }

    this._nodes.Add(key, this._order.AddLast(key));
  }

  public TKey? SelectVictim() {
    var first = this._order.First;
    if (first == null)
      return default;

    this._order.RemoveFirst();
    this._nodes.Remove(first.Value);
    return first.Value;
  }

  public void Remove(TKey key) {
    if (!this._nodes.Remove(key, out var node))
      return;

    this._order.Remove(node);
  }
}

/// <summary>Most-recently-used: victim is the entry touched last (useful for cyclic scans larger than the cache).</summary>
public sealed class MruPolicy<TKey> : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly LinkedList<TKey> _order = [];
  private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = [];

  public int Count => this._nodes.Count;

  public void OnAccess(TKey key) {
    if (!this._nodes.TryGetValue(key, out var node))
      return;

    this._order.Remove(node);
    this._order.AddLast(node);
  }

  public void OnInsert(TKey key) {
    if (this._nodes.ContainsKey(key)) {
      this.OnAccess(key);
      return;
    }

    this._nodes.Add(key, this._order.AddLast(key));
  }

  public TKey? SelectVictim() {
    var last = this._order.Last;
    if (last == null)
      return default;

    this._order.RemoveLast();
    this._nodes.Remove(last.Value);
    return last.Value;
  }

  public void Remove(TKey key) {
    if (!this._nodes.Remove(key, out var node))
      return;

    this._order.Remove(node);
  }
}

/// <summary>First-in-first-out: eviction order is admission order; recency is deliberately ignored.</summary>
public sealed class FifoPolicy<TKey> : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly LinkedList<TKey> _order = [];
  private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = [];

  public int Count => this._nodes.Count;

  public void OnAccess(TKey key) {
    // FIFO ignores recency by definition
  }

  public void OnInsert(TKey key) {
    if (this._nodes.ContainsKey(key))
      return;

    this._nodes.Add(key, this._order.AddLast(key));
  }

  public TKey? SelectVictim() {
    var first = this._order.First;
    if (first == null)
      return default;

    this._order.RemoveFirst();
    this._nodes.Remove(first.Value);
    return first.Value;
  }

  public void Remove(TKey key) {
    if (!this._nodes.Remove(key, out var node))
      return;

    this._order.Remove(node);
  }
}

/// <summary>Uniform-random victim; deterministic seed so tests are reproducible.</summary>
public sealed class RandomPolicy<TKey>(int seed = 0x5eed) : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly List<TKey> _keys = [];
  private readonly Dictionary<TKey, int> _indices = [];
  private readonly Random _random = new(seed);

  public int Count => this._keys.Count;

  public void OnAccess(TKey key) {
    // random replacement keeps no access state
  }

  public void OnInsert(TKey key) {
    if (this._indices.ContainsKey(key))
      return;

    this._indices.Add(key, this._keys.Count);
    this._keys.Add(key);
  }

  public TKey? SelectVictim() {
    if (this._keys.Count == 0)
      return default;

    var index = this._random.Next(this._keys.Count);
    var victim = this._keys[index];
    this._RemoveAt(victim, index);
    return victim;
  }

  public void Remove(TKey key) {
    if (this._indices.TryGetValue(key, out var index))
      this._RemoveAt(key, index);
  }

  private void _RemoveAt(TKey key, int index) {
    var lastIndex = this._keys.Count - 1;
    var last = this._keys[lastIndex];
    this._keys[index] = last;
    this._indices[last] = index;
    this._keys.RemoveAt(lastIndex);
    this._indices.Remove(key);
  }
}

/// <summary>Least-frequently-used with insertion-order tie-break.</summary>
public sealed class LfuPolicy<TKey> : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly Dictionary<TKey, (long frequency, long sequence)> _entries = [];
  private long _sequence;

  public int Count => this._entries.Count;

  public void OnAccess(TKey key) {
    if (this._entries.TryGetValue(key, out var entry))
      this._entries[key] = (entry.frequency + 1, entry.sequence);
  }

  public void OnInsert(TKey key) {
    if (this._entries.ContainsKey(key)) {
      this.OnAccess(key);
      return;
    }

    this._entries.Add(key, (1, this._sequence++));
  }

  public TKey? SelectVictim() {
    if (this._entries.Count == 0)
      return default;

    var victim = default(TKey);
    var best = (frequency: long.MaxValue, sequence: long.MaxValue);
    foreach (var (key, entry) in this._entries)
      if (entry.frequency < best.frequency || (entry.frequency == best.frequency && entry.sequence < best.sequence)) {
        best = entry;
        victim = key;
      }

    this._entries.Remove(victim!);
    return victim;
  }

  public void Remove(TKey key) => this._entries.Remove(key);
}

/// <summary>Clock (second chance): a circular sweep clears reference bits and evicts the first unreferenced entry.</summary>
public sealed class ClockPolicy<TKey> : ICacheEvictionPolicy<TKey> where TKey : class {
  private sealed class Entry {
    public bool Referenced;
  }

  private readonly LinkedList<TKey> _ring = [];
  private readonly Dictionary<TKey, (LinkedListNode<TKey> node, Entry entry)> _entries = [];
  private LinkedListNode<TKey>? _hand;

  public int Count => this._entries.Count;

  public void OnAccess(TKey key) {
    if (this._entries.TryGetValue(key, out var slot))
      slot.entry.Referenced = true;
  }

  public void OnInsert(TKey key) {
    if (this._entries.ContainsKey(key)) {
      this.OnAccess(key);
      return;
    }

    var node = this._hand == null ? this._ring.AddLast(key) : this._ring.AddBefore(this._hand, key);
    this._entries.Add(key, (node, new()));
  }

  public TKey? SelectVictim() {
    while (this._entries.Count > 0) {
      this._hand ??= this._ring.First;
      var current = this._hand!;
      this._hand = current.Next ?? this._ring.First;

      var slot = this._entries[current.Value];
      if (slot.entry.Referenced) {
        slot.entry.Referenced = false;
        continue;
      }

      if (this._hand == current)
        this._hand = null;

      this._ring.Remove(current);
      this._entries.Remove(current.Value);
      return current.Value;
    }

    return default;
  }

  public void Remove(TKey key) {
    if (!this._entries.Remove(key, out var slot))
      return;

    if (this._hand == slot.node)
      this._hand = slot.node.Next ?? this._ring.First;

    this._ring.Remove(slot.node);
    if (this._ring.Count == 0)
      this._hand = null;
  }
}

/// <summary>
/// Segmented LRU: new entries enter a probationary segment; a hit promotes into the
/// protected segment (bounded to ~80% of capacity). Victims come from probation first,
/// making one-shot scans unable to flush the protected working set.
/// </summary>
public sealed class SlruPolicy<TKey>(int capacityHint) : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly LruPolicy<TKey> _probation = new();
  private readonly LruPolicy<TKey> _protected = new();
  private readonly HashSet<TKey> _inProtected = [];
  private readonly int _protectedCapacity = Math.Max(1, capacityHint * 4 / 5);

  public int Count => this._probation.Count + this._protected.Count;

  public void OnAccess(TKey key) {
    if (this._inProtected.Contains(key)) {
      this._protected.OnAccess(key);
      return;
    }

    // promotion out of probation
    this._probation.Remove(key);
    this._protected.OnInsert(key);
    this._inProtected.Add(key);

    // keep the protected segment bounded by demoting its LRU end back to probation
    if (this._protected.Count <= this._protectedCapacity)
      return;

    var demoted = this._protected.SelectVictim();
    if (demoted == null)
      return;

    this._inProtected.Remove(demoted);
    this._probation.OnInsert(demoted);
  }

  public void OnInsert(TKey key) {
    if (this._inProtected.Contains(key)) {
      this._protected.OnAccess(key);
      return;
    }

    this._probation.OnInsert(key);
  }

  public TKey? SelectVictim() {
    var victim = this._probation.SelectVictim();
    if (victim != null)
      return victim;

    victim = this._protected.SelectVictim();
    if (victim != null)
      this._inProtected.Remove(victim);

    return victim;
  }

  public void Remove(TKey key) {
    if (this._inProtected.Remove(key)) {
      this._protected.Remove(key);
      return;
    }

    this._probation.Remove(key);
  }
}

/// <summary>
/// 2Q: first-time entries live in a FIFO (A1in); on eviction their identity is remembered
/// in a ghost list (A1out); a re-admission from the ghost list goes straight to the main
/// LRU (Am). Correlated one-shot references never pollute Am.
/// </summary>
public sealed class TwoQueuePolicy<TKey>(int capacityHint) : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly FifoPolicy<TKey> _a1In = new();
  private readonly LruPolicy<TKey> _am = new();
  private readonly HashSet<TKey> _inAm = [];
  private readonly LinkedList<TKey> _a1OutOrder = [];
  private readonly HashSet<TKey> _a1Out = [];
  private readonly int _a1InCapacity = Math.Max(1, capacityHint / 4);
  private readonly int _a1OutCapacity = Math.Max(1, capacityHint / 2);

  public int Count => this._a1In.Count + this._am.Count;

  public void OnAccess(TKey key) {
    if (this._inAm.Contains(key))
      this._am.OnAccess(key);
    // a hit inside A1in deliberately does not promote (2Q rule: correlated references stay in A1in)
  }

  public void OnInsert(TKey key) {
    if (this._inAm.Contains(key)) {
      this._am.OnAccess(key);
      return;
    }

    if (this._a1Out.Remove(key)) {
      // seen before and evicted from A1in — this is real re-use, admit into Am
      this._a1OutOrder.Remove(key);
      this._am.OnInsert(key);
      this._inAm.Add(key);
      return;
    }

    this._a1In.OnInsert(key);
  }

  public TKey? SelectVictim() {
    if (this._a1In.Count >= this._a1InCapacity || this._am.Count == 0) {
      var victim = this._a1In.SelectVictim();
      if (victim != null) {
        this._RememberGhost(victim);
        return victim;
      }
    }

    var amVictim = this._am.SelectVictim();
    if (amVictim != null)
      this._inAm.Remove(amVictim);

    return amVictim;
  }

  private void _RememberGhost(TKey key) {
    if (!this._a1Out.Add(key))
      return;

    this._a1OutOrder.AddLast(key);
    if (this._a1Out.Count <= this._a1OutCapacity)
      return;

    var oldest = this._a1OutOrder.First!.Value;
    this._a1OutOrder.RemoveFirst();
    this._a1Out.Remove(oldest);
  }

  public void Remove(TKey key) {
    if (this._inAm.Remove(key)) {
      this._am.Remove(key);
      return;
    }

    this._a1In.Remove(key);
  }
}

/// <summary>
/// ARC: balances a recency list (T1) against a frequency list (T2), steering the split
/// point p by hits in the ghost lists (B1/B2). Scan-resistant and self-tuning.
/// </summary>
public sealed class ArcPolicy<TKey>(int capacityHint) : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly LruPolicy<TKey> _t1 = new();
  private readonly LruPolicy<TKey> _t2 = new();
  private readonly HashSet<TKey> _inT1 = [];
  private readonly HashSet<TKey> _inT2 = [];
  private readonly LinkedList<TKey> _b1Order = [];
  private readonly HashSet<TKey> _b1 = [];
  private readonly LinkedList<TKey> _b2Order = [];
  private readonly HashSet<TKey> _b2 = [];
  private readonly int _capacity = Math.Max(1, capacityHint);
  private double _p;

  public int Count => this._inT1.Count + this._inT2.Count;

  public void OnAccess(TKey key) {
    if (this._inT1.Remove(key)) {
      // second reference — promote from recency to frequency
      this._t1.Remove(key);
      this._t2.OnInsert(key);
      this._inT2.Add(key);
      return;
    }

    if (this._inT2.Contains(key))
      this._t2.OnAccess(key);
  }

  public void OnInsert(TKey key) {
    if (this._inT1.Contains(key) || this._inT2.Contains(key)) {
      this.OnAccess(key);
      return;
    }

    if (this._b1.Remove(key)) {
      // ghost hit in B1 — recency was undervalued, grow p
      this._b1Order.Remove(key);
      this._p = Math.Min(this._capacity, this._p + Math.Max(1.0, (double)this._b2.Count / Math.Max(1, this._b1.Count)));
      this._t2.OnInsert(key);
      this._inT2.Add(key);
      return;
    }

    if (this._b2.Remove(key)) {
      // ghost hit in B2 — frequency was undervalued, shrink p
      this._b2Order.Remove(key);
      this._p = Math.Max(0, this._p - Math.Max(1.0, (double)this._b1.Count / Math.Max(1, this._b2.Count)));
      this._t2.OnInsert(key);
      this._inT2.Add(key);
      return;
    }

    this._t1.OnInsert(key);
    this._inT1.Add(key);
  }

  public TKey? SelectVictim() {
    // evict from T1 while it exceeds the adaptive target p, else from T2
    if (this._inT1.Count > 0 && (this._inT1.Count > this._p || this._inT2.Count == 0)) {
      var victim = this._t1.SelectVictim()!;
      this._inT1.Remove(victim);
      this._RememberGhost(this._b1, this._b1Order, victim);
      return victim;
    }

    var t2Victim = this._t2.SelectVictim();
    if (t2Victim != null) {
      this._inT2.Remove(t2Victim);
      this._RememberGhost(this._b2, this._b2Order, t2Victim);
      return t2Victim;
    }

    var t1Victim = this._t1.SelectVictim();
    if (t1Victim != null) {
      this._inT1.Remove(t1Victim);
      this._RememberGhost(this._b1, this._b1Order, t1Victim);
    }

    return t1Victim;
  }

  private void _RememberGhost(HashSet<TKey> ghosts, LinkedList<TKey> order, TKey key) {
    if (!ghosts.Add(key))
      return;

    order.AddLast(key);
    if (ghosts.Count <= this._capacity)
      return;

    var oldest = order.First!.Value;
    order.RemoveFirst();
    ghosts.Remove(oldest);
  }

  public void Remove(TKey key) {
    if (this._inT1.Remove(key)) {
      this._t1.Remove(key);
      return;
    }

    if (this._inT2.Remove(key))
      this._t2.Remove(key);
  }
}

/// <summary>
/// Clock-Pro (simplified): a clock over cold and hot pages with ghost test periods —
/// a cold page re-referenced during its test period becomes hot; hot pages get a second
/// chance via their reference bit.
/// </summary>
public sealed class ClockProPolicy<TKey>(int capacityHint) : ICacheEvictionPolicy<TKey> where TKey : class {
  private sealed class Page {
    public bool Hot;
    public bool Referenced;
  }

  private readonly LinkedList<TKey> _ring = [];
  private readonly Dictionary<TKey, (LinkedListNode<TKey> node, Page page)> _pages = [];
  private readonly LinkedList<TKey> _testOrder = [];
  private readonly HashSet<TKey> _testGhosts = [];
  private readonly int _capacity = Math.Max(1, capacityHint);
  private LinkedListNode<TKey>? _hand;

  public int Count => this._pages.Count;

  public void OnAccess(TKey key) {
    if (this._pages.TryGetValue(key, out var slot))
      slot.page.Referenced = true;
  }

  public void OnInsert(TKey key) {
    if (this._pages.ContainsKey(key)) {
      this.OnAccess(key);
      return;
    }

    var hot = this._testGhosts.Remove(key);
    if (hot)
      this._testOrder.Remove(key);

    var node = this._hand == null ? this._ring.AddLast(key) : this._ring.AddBefore(this._hand, key);
    this._pages.Add(key, (node, new() { Hot = hot }));
  }

  public TKey? SelectVictim() {
    while (this._pages.Count > 0) {
      this._hand ??= this._ring.First;
      var current = this._hand!;
      this._hand = current.Next ?? this._ring.First;

      var (_, page) = this._pages[current.Value];
      if (page.Referenced) {
        page.Referenced = false;
        if (!page.Hot)
          page.Hot = true; // re-referenced during its test period → promote
        continue;
      }

      if (page.Hot) {
        page.Hot = false; // demote; next sweep may evict
        continue;
      }

      if (this._hand == current)
        this._hand = null;

      this._ring.Remove(current);
      this._pages.Remove(current.Value);
      this._RememberTestGhost(current.Value);
      return current.Value;
    }

    return default;
  }

  private void _RememberTestGhost(TKey key) {
    if (!this._testGhosts.Add(key))
      return;

    this._testOrder.AddLast(key);
    if (this._testGhosts.Count <= this._capacity)
      return;

    var oldest = this._testOrder.First!.Value;
    this._testOrder.RemoveFirst();
    this._testGhosts.Remove(oldest);
  }

  public void Remove(TKey key) {
    if (!this._pages.Remove(key, out var slot))
      return;

    if (this._hand == slot.node)
      this._hand = slot.node.Next ?? this._ring.First;

    this._ring.Remove(slot.node);
    if (this._ring.Count == 0)
      this._hand = null;
  }
}
