using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PerformanceOptimizer.Simulation;

// Each pool belongs to one native method. Keys use managed reference identity,
// and do not keep a destroyed unit/sonar alive. Nested calls get separate nodes.
internal sealed class ScratchPool
{
    private ConditionalWeakTable<object, Owner> owners = new();
    private readonly ConditionalWeakTable<object, Owner>.CreateValueCallback create = _ => new Owner();
    private readonly object sync = new();
    private int activeCalls;
    private bool clearPending;

    internal Lease Enter(object owner)
    {
        lock (sync)
        {
            var scope = owners.GetValue(owner, create).Enter(this);
            activeCalls++;
            return scope;
        }
    }
    internal ScratchWorkspace Current(object owner) => owners.TryGetValue(owner, out var value)
        ? value.Current : throw new InvalidOperationException("No scratch invocation is active.");
    internal void Clear()
    {
        lock (sync)
        {
            if (activeCalls != 0) { clearPending = true; return; }
            owners = new ConditionalWeakTable<object, Owner>();
            clearPending = false;
        }
    }

    private void Left()
    {
        lock (sync)
        {
            activeCalls--;
            if (activeCalls == 0 && clearPending) Clear();
        }
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly ScratchPool? pool;
        private readonly Owner? owner;
        private readonly Node? node;
        internal Lease(ScratchPool pool, Owner owner, Node node) { this.pool = pool; this.owner = owner; this.node = node; }
        public void Dispose()
        {
            if (owner == null) return;
            owner.Exit(node!);
            pool!.Left();
        }
    }

    internal sealed class Node
    {
        internal readonly ScratchWorkspace Workspace = new();
        internal Node? Next;
    }

    internal sealed class Owner
    {
        private Node? active, free;
        private int thread;

        internal Lease Enter(ScratchPool pool)
        {
            lock (this)
            {
                if (active != null && thread != Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("Concurrent simulation calls cannot share scratch storage.");
                var node = free ?? new Node();
                free = node.Next;
                node.Next = active;
                active = node;
                thread = Environment.CurrentManagedThreadId;
                return new Lease(pool, this, node);
            }
        }

        internal ScratchWorkspace Current
        {
            get
            {
                lock (this)
                {
                    if (active == null || thread != Environment.CurrentManagedThreadId)
                        throw new InvalidOperationException("Scratch storage was accessed outside its invocation.");
                    return active.Workspace;
                }
            }
        }

        internal void Exit(Node node)
        {
            lock (this)
            {
                if (!ReferenceEquals(active, node) || thread != Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("Scratch invocations ended out of order.");
                node.Workspace.Clear();
                active = node.Next;
                node.Next = free;
                free = node;
            }
        }
    }
}

internal sealed class ScratchWorkspace
{
    private readonly Dictionary<Type, object> values = new();
    private readonly List<Action> clear = new();

    internal List<T> List<T>(int capacity = 0)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!values.TryGetValue(typeof(List<T>), out var value))
        {
            var created = new List<T>(capacity);
            values.Add(typeof(List<T>), created);
            clear.Add(created.Clear);
            value = created;
        }
        var result = (List<T>)value;
        if (result.Count != 0) throw new InvalidOperationException("A scratch list was requested twice before clearing.");
        if (result.Capacity < capacity) result.Capacity = capacity;
        return result;
    }

    internal HashSet<T> Set<T>()
    {
        if (!values.TryGetValue(typeof(HashSet<T>), out var value))
        {
            var created = new HashSet<T>();
            values.Add(typeof(HashSet<T>), created);
            clear.Add(created.Clear);
            value = created;
        }
        var result = (HashSet<T>)value;
        if (result.Count != 0) throw new InvalidOperationException("A scratch set was requested twice before clearing.");
        return result;
    }

    internal List<T> Snapshot<T>(IEnumerable<T> source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var result = List<T>(source is ICollection<T> collection ? collection.Count : 0);
        result.AddRange(source);
        return result;
    }

    internal void Clear()
    {
        // Clear references on normal return and exceptions. Capacity survives;
        // no previously observed contact or decision survives the invocation.
        foreach (var reset in clear) reset();
    }
}
