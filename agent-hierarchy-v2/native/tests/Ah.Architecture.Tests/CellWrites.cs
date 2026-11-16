using System.Reflection;
using System.Reflection.Emit;

namespace Ah.Architecture.Tests;

/// <summary>
/// Finds the read-only static fields of an assembly that hold something writable (an array, a collection, a box) and are written by
/// a method other than their own type's initializer. Each method body is read with a stack simulation from every load of a cell to
/// the instruction that consumes it: an array store or element address, a store into the box, a mutating call on the collection or
/// <c>Array</c>, or taking the field's address is a write. A scan stops at a branch, so a cell loaded across one is not followed.
/// Limit: a cell handed to another method as an argument is not followed into it, a write from another assembly is not seen, and a
/// method whose local types cannot be loaded here (a web-framework assembly the test does not reference) is not read; a cell written
/// that way goes in the allow list with the reason it is never written.
/// </summary>
internal static class CellWrites
{
    private static readonly HashSet<string> InstanceMutators = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Insert", "InsertRange", "Push", "Pop",
        "Enqueue", "Dequeue", "TryAdd", "TryRemove", "TryUpdate", "TryPop", "TryDequeue", "set_Item", "AddOrUpdate", "GetOrAdd",
        "Sort", "Reverse", "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith", "TrimExcess", "EnsureCapacity",
        "set_Value", "Fill", "CopyTo",
    };

    private static readonly HashSet<string> ArrayStatics = new(StringComparer.Ordinal) { "Fill", "Clear", "Sort", "Reverse", "Resize" };

    private const BindingFlags Declared = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>The members of <paramref name="cells"/> that a method of <paramref name="types"/> writes outside the declaring type's initializer.</summary>
    internal static List<FieldInfo> Written(IEnumerable<Type> types, IReadOnlyList<FieldInfo> cells)
    {
        var byKey = cells.ToDictionary(Key, StringComparer.Ordinal);
        var written = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                byte[]? il;
                try
                {
                    il = method.GetMethodBody()?.GetILAsByteArray();
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException or BadImageFormatException or IOException)
                {
                    continue;
                }

                if (il is not null)
                {
                    Scan(method, il, byKey, written);
                }
            }
        }

        return [.. written.Values.OrderBy(f => f.DeclaringType!.FullName + "." + f.Name, StringComparer.Ordinal)];
    }

    private static string Key(FieldInfo field) => $"{field.Module.ModuleVersionId}:{field.MetadataToken}";

    private static void Scan(MethodBase method, byte[] il, Dictionary<string, FieldInfo> cells, Dictionary<string, FieldInfo> written)
    {
        var body = IlScan.Instructions(il).ToList();
        for (var i = 0; i < body.Count; i++)
        {
            var op = body[i].Op;
            if (op != OpCodes.Ldsfld && op != OpCodes.Ldsflda)
            {
                continue;
            }

            if (Resolve(method, body[i].Operand) is not { } field || !cells.TryGetValue(Key(field), out var cell))
            {
                continue;
            }

            if (method is ConstructorInfo { IsStatic: true } && method.DeclaringType == cell.DeclaringType)
            {
                continue;
            }

            if (op == OpCodes.Ldsflda || Writes(method, body, i))
            {
                written[Key(cell)] = cell;
            }
        }
    }

    /// <summary>
    /// Whether the value pushed at <c>body[at]</c> is written through. The address of an array element is followed one step further:
    /// it is a write only if what takes the address stores through it, because reading a field of a struct element also takes it.
    /// </summary>
    private static bool Writes(MethodBase method, List<IlScan.Instruction> body, int at)
    {
        var (use, index) = Consumed(method, body, at);
        if (use != Use.Address)
        {
            return use == Use.Write;
        }

        var (stored, _) = Consumed(method, body, index);
        return stored == Use.Write;
    }

    private enum Use { Other, Write, Address }

    private static FieldInfo? Resolve(MethodBase method, int token)
    {
        try
        {
            return method.Module.ResolveField(
                token,
                method.DeclaringType is { IsGenericType: true } ? method.DeclaringType.GetGenericArguments() : null,
                method.IsGenericMethod ? method.GetGenericArguments() : null);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>What the instruction that takes the value pushed by <c>body[at]</c> off the evaluation stack does with it.</summary>
    private static (Use Use, int At) Consumed(MethodBase method, List<IlScan.Instruction> body, int at)
    {
        var height = 1;
        for (var j = at + 1; j < body.Count; j++)
        {
            var instruction = body[j];
            var op = instruction.Op;
            if (op.FlowControl is not (FlowControl.Next or FlowControl.Call or FlowControl.Meta) || Effects(method, instruction) is not var (pop, push, target))
            {
                return (Use.Other, j);
            }

            if (pop < height)
            {
                height = height - pop + push;
                continue;
            }

            var operand = pop - height;
            if (body[at].Op != OpCodes.Ldsfld && body[at].Op != OpCodes.Ldsflda)
            {
                return (StoresThroughAddress(op, target) ? Use.Write : Use.Other, j);
            }

            return op == OpCodes.Ldelema && operand == 0 ? (Use.Address, j) : (Classify(op, operand, target) ? Use.Write : Use.Other, j);
        }

        return (Use.Other, body.Count);
    }

    /// <summary>Whether what takes the address of an array element writes through it: a store, or a call that is handed it by reference (such as <c>Interlocked</c>).</summary>
    private static bool StoresThroughAddress(OpCode op, MethodBase? target) =>
        op == OpCodes.Stfld || op == OpCodes.Stobj || op == OpCodes.Initobj || op.Name!.StartsWith("stind.", StringComparison.Ordinal)
        || ((op == OpCodes.Call || op == OpCodes.Callvirt) && target is { IsStatic: true });

    private static bool Classify(OpCode op, int operand, MethodBase? target)
    {
        if (op == OpCodes.Stelem || op.Name!.StartsWith("stelem.", StringComparison.Ordinal) || op == OpCodes.Stfld)
        {
            return operand == 0;
        }

        if (target is null || (op != OpCodes.Call && op != OpCodes.Callvirt))
        {
            return false;
        }

        if (!target.IsStatic)
        {
            return operand == 0 && InstanceMutators.Contains(target.Name);
        }

        return target.DeclaringType == typeof(Array)
            && (ArrayStatics.Contains(target.Name) || (target.Name is "Copy" or "ConstrainedCopy" && operand >= 1));
    }

    private static (int Pop, int Push, MethodBase? Target)? Effects(MethodBase owner, IlScan.Instruction instruction)
    {
        var op = instruction.Op;
        if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
        {
            MethodBase target;
            try
            {
                target = owner.Module.ResolveMethod(
                    instruction.Operand,
                    owner.DeclaringType is { IsGenericType: true } ? owner.DeclaringType.GetGenericArguments() : null,
                    owner.IsGenericMethod ? owner.GetGenericArguments() : null)!;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or BadImageFormatException)
            {
                return null;
            }

            var pop = target.GetParameters().Length + (!target.IsStatic && op != OpCodes.Newobj ? 1 : 0);
            var push = op == OpCodes.Newobj || (target is MethodInfo { ReturnType: var returns } && returns != typeof(void)) ? 1 : 0;
            return (pop, push, target);
        }

        int? popped = Pops(op.StackBehaviourPop);
        int? pushed = Pushes(op.StackBehaviourPush);
        return popped is { } a && pushed is { } b ? (a, b, null) : null;
    }

    private static int? Pops(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4
            or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
        _ => null,
    };

    private static int? Pushes(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => null,
    };
}
