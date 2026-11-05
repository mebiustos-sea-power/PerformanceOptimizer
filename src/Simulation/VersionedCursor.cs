using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace PerformanceOptimizer.Simulation;

internal struct VersionedCursor<T>
{
    private static readonly Func<Collection<T>, List<T>> backing = Backing();
    private static readonly Func<List<T>, int> getVersion = Version();
    private List<T> list;
    private int version, index;
    private T current;

    internal static VersionedCursor<T> Begin(Collection<T> source)
    {
        var list = backing(source);
        return new VersionedCursor<T> { list = list, version = getVersion(list), current = default! };
    }

    internal static T Current(ref VersionedCursor<T> cursor) => cursor.current;
    internal static bool MoveNext(ref VersionedCursor<T> cursor)
    {
        if (cursor.version != getVersion(cursor.list))
            throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        if (cursor.index < cursor.list.Count)
        {
            cursor.current = cursor.list[cursor.index++];
            return true;
        }
        cursor.index = cursor.list.Count + 1;
        cursor.current = default!;
        return false;
    }

    internal static void Dispose(ref VersionedCursor<T> cursor) { }

    private static Func<Collection<T>, List<T>> Backing()
    {
        var fields = typeof(Collection<T>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(IList<T>)).ToArray();
        if (fields.Length != 1) throw new NotSupportedException("Unverified Collection<T> backing storage.");
        var method = new DynamicMethod("NativeCollectionBacking", typeof(List<T>), new[] { typeof(Collection<T>) }, typeof(VersionedCursor<T>).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fields[0]); il.Emit(OpCodes.Castclass, typeof(List<T>)); il.Emit(OpCodes.Ret);
        return (Func<Collection<T>, List<T>>)method.CreateDelegate(typeof(Func<Collection<T>, List<T>>));
    }

    private static Func<List<T>, int> Version()
    {
        var field = typeof(List<T>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != typeof(int)) throw new NotSupportedException("Unverified List<T> mutation tracking.");
        var method = new DynamicMethod("NativeCollectionVersion", typeof(int), new[] { typeof(List<T>) }, typeof(VersionedCursor<T>).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        return (Func<List<T>, int>)method.CreateDelegate(typeof(Func<List<T>, int>));
    }
}
