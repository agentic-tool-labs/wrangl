using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Ah.Architecture.Tests;

/// <summary>The architecture checks. Each returns its violations; an empty list means the check holds.</summary>
internal static class Checks
{
    private static readonly string[] RestrictedTypes = ["IRawFileWriter", "IRecordAppender"];

    // (a) allowed edges

    internal static List<string> EdgeViolations(string project, IEnumerable<string> referenced)
    {
        var expected = Layers.AllowedReferences[project].ToHashSet();
        var actual = referenced.ToHashSet();
        var violations = new List<string>();
        violations.AddRange(actual.Except(expected).Order().Select(r => $"{project}: forbidden reference {r}"));
        violations.AddRange(expected.Except(actual).Order().Select(r => $"{project}: missing reference {r}"));
        return violations;
    }

    internal static List<string> ProjectReferences(string csprojPath) =>
        [.. XDocument.Load(csprojPath).Descendants("ProjectReference")
            .Where(e => (string?)e.Attribute("OutputItemType") != "Analyzer")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))];

    internal static List<string> BuiltEdgeViolations(string project, string dllPath)
    {
        var allowed = Layers.AllowedReferences[project].ToHashSet();
        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        return
        [
            .. reader.AssemblyReferences
                .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
                .Where(n => n.StartsWith("Ah.", StringComparison.Ordinal) && !allowed.Contains(n))
                .Order()
                .Select(n => $"{project}: built assembly references {n}"),
        ];
    }

    internal static List<string> Cycles(IReadOnlyDictionary<string, string[]> graph)
    {
        var cycles = new List<string>();
        var done = new HashSet<string>();
        var path = new List<string>();

        void Visit(string node)
        {
            var at = path.IndexOf(node);
            if (at >= 0)
            {
                cycles.Add("cycle: " + string.Join(" -> ", path.Skip(at).Append(node)));
                return;
            }

            if (!done.Add(node))
            {
                return;
            }

            path.Add(node);
            foreach (var next in graph.GetValueOrDefault(node, []))
            {
                Visit(next);
            }

            path.RemoveAt(path.Count - 1);
        }

        foreach (var node in graph.Keys)
        {
            Visit(node);
        }

        return cycles;
    }

    // (b) public surface

    internal static List<string> PublicSurfaceViolations(string logicalName, string dllPath)
    {
        var moduleName = logicalName[(logicalName.LastIndexOf('.') + 1)..] + "Module";

        if (logicalName == "Ah.Generators")
        {
            return GeneratorSurfaceViolations(logicalName, dllPath);
        }

        var exported = Assembly.LoadFrom(dllPath).GetExportedTypes();

        var violations = new List<string>();
        var modules = 0;
        foreach (var type in exported)
        {
            if (IsModule(type, moduleName))
            {
                modules++;
            }

            if (logicalName == "Ah.Abstractions" ? AbstractionsAllows(type) :
                logicalName == "Ah.Core" ? CoreAllows(type) : DomainAllows(type, moduleName))
            {
                continue;
            }

            violations.Add($"{logicalName}: public type {type.FullName} is outside the public surface of {logicalName}");
        }

        if (!Layers.WithoutModule.Contains(logicalName) && modules != 1)
        {
            violations.Add($"{logicalName}: expected one public static class {moduleName}, found {modules}");
        }

        return violations;
    }

    // The generator's own dependencies (Roslyn) are not loadable here, so its surface is read from metadata.
    private static List<string> GeneratorSurfaceViolations(string logicalName, string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        var visible = new[] { TypeAttributes.Public, TypeAttributes.NestedPublic };
        var exported = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
            .Where(t => visible.Contains(t.Attributes & TypeAttributes.VisibilityMask)).ToList();

        var isGenerator = exported is [var only] &&
            (only.Attributes & TypeAttributes.Sealed) != 0 &&
            (only.Attributes & TypeAttributes.Abstract) == 0 &&
            only.GetCustomAttributes().Select(reader.GetCustomAttribute).Any(a =>
                a.Constructor.Kind == HandleKind.MemberReference &&
                reader.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } parent &&
                reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name) == "GeneratorAttribute");

        return isGenerator
            ? []
            : [$"{logicalName}: expected exactly one public sealed [Generator] class, found [{string.Join(", ", exported.Select(t => reader.GetString(t.Name)))}]"];
    }

    private static bool IsModule(Type type, string moduleName) =>
        type is { IsAbstract: true, IsSealed: true, DeclaringType: null } && type.Name == moduleName;

    private static bool IsReadOnlyStruct(Type type) =>
        type is { IsValueType: true, IsEnum: false } && type.CustomAttributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute");

    // Shared by Ah.Cli, Ah.Commands and Ah.Launch: the one place the headless transport id is built and parsed.
    private static readonly string[] AbstractionsShared = ["Ah.Abstractions.HeadlessTransport"];

    // Shared across assemblies: AskRecord and RulesOfEngagement by Ah.Cli and Ah.Hooks; ParkedQuestion and ParkedQuestions by Ah.Cli, Ah.Hooks and Ah.Watch.
    private static readonly string[] HierarchyShared = ["Ah.Hierarchy.AskRecord", "Ah.Hierarchy.ParkedQuestion", "Ah.Hierarchy.ParkedQuestions", "Ah.Hierarchy.RulesOfEngagement"];

    private static bool AbstractionsAllows(Type type) =>
        type.IsInterface || type.IsEnum || typeof(Attribute).IsAssignableFrom(type) || IsRecordClass(type) ||
        IsReadOnlyRecordStruct(type) || (type.IsSealed && typeof(Exception).IsAssignableFrom(type)) ||
        AbstractionsShared.Contains(type.FullName);

    private static bool CoreAllows(Type type) =>
        type.IsEnum || IsRecordClass(type) || IsReadOnlyStruct(type) || (type.IsClass && type.IsSealed) || (type.IsInterface && InCoreJson(type.Namespace));

    private static bool InCoreJson(string? ns) => ns == "Ah.Core.Json" || (ns?.StartsWith("Ah.Core.Json.", StringComparison.Ordinal) ?? false);

    private static bool DomainAllows(Type type, string moduleName) =>
        type.IsInterface || type.IsEnum || typeof(Attribute).IsAssignableFrom(type) || IsRecordClass(type) ||
        IsReadOnlyRecordStruct(type) || IsModule(type, moduleName) || HierarchyShared.Contains(type.FullName);

    private static bool IsRecordClass(Type type) => type.IsClass && type.GetMethod("<Clone>$") is not null;

    private static bool IsReadOnlyRecordStruct(Type type) =>
        type is { IsValueType: true, IsEnum: false } &&
        type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null &&
        type.CustomAttributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute");

    internal static List<string> InternalsVisibleToViolations(string project, string dllPath)
    {
        var granted = Assembly.LoadFrom(dllPath).GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName).ToList();
        return granted.SequenceEqual([project + ".Tests"])
            ? []
            : [$"{project}: InternalsVisibleTo is [{string.Join(", ", granted)}], expected only {project}.Tests"];
    }

    // (c) restricted interfaces

    internal static List<string> RestrictedInterfaceViolations(string logicalName, string dllPath)
    {
        if (Layers.RawWriterUsers.Contains(logicalName))
        {
            return [];
        }

        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        var violations = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Where(t => reader.GetString(t.Namespace) == "Ah.Abstractions" &&
                        RestrictedTypes.Contains(reader.GetString(t.Name)))
            .Select(t => $"{logicalName}: references restricted {reader.GetString(t.Name)}")
            .Distinct()
            .ToList();

        // One named exception (0112 §6(c)): the hierarchy directory bootstrap writes a Hierarchy store with the raw writer.
        return logicalName == "Ah.Hierarchy" && violations.Count > 0 && !ReferencedOutsideBootstrap(dllPath) ? [] : violations;
    }

    /// <summary>
    /// Whether a type other than the Hierarchy bootstrap (namespace <c>Ah.Hierarchy.Bootstrap</c>, and the module class that builds
    /// it) holds a restricted writer in a field, a parameter, a return value, a property or a local.
    /// </summary>
    private static bool ReferencedOutsideBootstrap(string dllPath)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static bool Restricted(Type t) => RestrictedTypes.Contains(t.Name) && t.Namespace == "Ah.Abstractions";
        var assembly = Assembly.LoadFrom(dllPath);
        foreach (var type in LoadableTypes(assembly))
        {
            var top = type;
            while (top.DeclaringType is not null)
            {
                top = top.DeclaringType;
            }

            if (top.Namespace == "Ah.Hierarchy.Bootstrap" || top.FullName == "Ah.Hierarchy.HierarchyModule")
            {
                continue;
            }

            if (type.GetFields(all).Any(f => Restricted(f.FieldType)) || type.GetProperties(all).Any(p => Restricted(p.PropertyType)))
            {
                return true;
            }

            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method.GetParameters().Any(p => Restricted(p.ParameterType))
                    || (method is MethodInfo info && Restricted(info.ReturnType))
                    || (method.GetMethodBody()?.LocalVariables.Any(l => Restricted(l.LocalType)) ?? false))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // (d) P/Invoke

    /// <summary>The assemblies that may declare P/Invokes; Ah.StateDb binds the system SQLite library and nothing else.</summary>
    internal static readonly string[] PInvokeOwners = ["Ah.Platform", "Ah.StateDb"];

    internal static List<string> PInvokeViolations(string logicalName, string dllPath)
    {
        if (PInvokeOwners.Contains(logicalName))
        {
            return [];
        }

        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        return
        [
            .. reader.MethodDefinitions
                .Select(reader.GetMethodDefinition)
                .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                .Select(m => $"{logicalName}: P/Invoke {reader.GetString(reader.GetTypeDefinition(m.GetDeclaringType()).Name)}.{reader.GetString(m.Name)}"),
        ];
    }

    // (e) one owner per store

    internal static List<(string Assembly, string Store)> DeclaredStores(string logicalName, string dllPath) =>
        [.. LoadableTypes(Assembly.LoadFrom(dllPath))
            .SelectMany(t => t.CustomAttributes)
            .Where(a => a.AttributeType.FullName == "Ah.Abstractions.StateDomainAttribute")
            .SelectMany(a => ((IEnumerable<CustomAttributeTypedArgument>)a.ConstructorArguments[1].Value!)
                .Select(v => (logicalName, (string)v.Value!)))];

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    internal static IReadOnlyDictionary<string, string> LoadStoreOwners(string jsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    internal static List<string> StoreOwnerViolations(
        IEnumerable<(string Assembly, string Store)> declared, IReadOnlyDictionary<string, string> owners)
    {
        var violations = new List<string>();
        var list = declared.Distinct().ToList();
        foreach (var group in list.GroupBy(d => d.Store).Where(g => g.Count() > 1))
        {
            violations.Add($"store {group.Key} is declared by more than one assembly: {string.Join(", ", group.Select(d => d.Assembly).Order())}");
        }

        foreach (var (assembly, store) in list)
        {
            if (owners.TryGetValue(store, out var owner) && owner != assembly)
            {
                violations.Add($"store {store} is owned by {owner} and declared by {assembly}");
            }
        }

        return violations;
    }

    // (f) one JSON writer

    internal static List<string> JsonWriterViolations(string logicalName, string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var reader = pe.GetMetadataReader();
        var violations = new HashSet<string>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var ns = TopLevelNamespace(reader, type);
            if (logicalName == "Ah.Core" && (ns == "Ah.Core.Json" || ns.StartsWith("Ah.Core.Json.", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var owner = $"{ns}.{reader.GetString(type.Name)}.{reader.GetString(method.Name)}";
                foreach (var token in IlScan.CalledTokens(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!))
                {
                    if (ForbiddenCallee(reader, token) is { } callee)
                    {
                        violations.Add($"{logicalName}: {owner} calls {callee}");
                    }
                }
            }
        }

        return [.. violations.Order()];
    }

    // (i) no mutable static state

    /// <summary>
    /// Static fields that can be assigned after type initialisation: any static field that is not read-only (a static property
    /// with a setter included). Constants, read-only fields of an immutable type and compiler-generated types (lambda caches) are
    /// not state. A read-only field whose type is an array or a mutable collection or box is a cell, and is state only when some method
    /// of the same assembly writes it outside the declaring type's own initializer (see <see cref="CellWrites"/>). A violation is
    /// named <c>Namespace.Type.Field</c>; one in <paramref name="allowed"/> (assignable fields) or <paramref name="allowedCells"/>
    /// (cells) is permitted.
    /// </summary>
    internal static List<string> MutableStaticViolations(string logicalName, string dllPath, IReadOnlySet<string> allowed, IReadOnlySet<string>? allowedCells = null)
    {
        if (logicalName == "Ah.Generators")
        {
            // A compile-time analyzer: it needs Roslyn to load and never runs in the shipped program.
            return [];
        }

        var violations = new List<string>();
        var types = LoadableTypes(Assembly.LoadFrom(dllPath)).ToList();
        var cells = new List<FieldInfo>();
        foreach (var type in types)
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(All))
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                var name = $"{type.FullName}.{field.Name}";
                if (!field.IsInitOnly && !allowed.Contains(name))
                {
                    violations.Add($"{logicalName}: static {name} is not read-only");
                }
                else if (field.IsInitOnly && IsMutableContainer(field.FieldType) && allowedCells?.Contains(name) != true)
                {
                    cells.Add(field);
                }
            }
        }

        foreach (var cell in CellWrites.Written(types, cells))
        {
            violations.Add($"{logicalName}: static {cell.DeclaringType!.FullName}.{cell.Name} is a read-only cell of mutable type {cell.FieldType.Name} written after initialisation");
        }

        return [.. violations.Order()];
    }

    private static readonly HashSet<string> MutableContainerTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedList`2",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Runtime.CompilerServices.StrongBox`1",
        "System.Collections.ArrayList",
        "System.Collections.Hashtable",
    };

    /// <summary>An array, or a collection or box whose contents or value can change after the type is initialised.</summary>
    private static bool IsMutableContainer(Type type) =>
        type.IsArray || MutableContainerTypes.Contains(type.IsGenericType ? type.GetGenericTypeDefinition().FullName! : type.FullName ?? string.Empty);

    private static bool IsCompilerGenerated(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }

    private static string TopLevelNamespace(MetadataReader reader, TypeDefinition type)
    {
        while (type.IsNested)
        {
            type = reader.GetTypeDefinition(type.GetDeclaringType());
        }

        return reader.GetString(type.Namespace);
    }

    private static string? ForbiddenCallee(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        if (handle.Kind != HandleKind.MemberReference)
        {
            return null;
        }

        var member = reader.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind != HandleKind.TypeReference)
        {
            return null;
        }

        var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
        var ns = reader.GetString(parent.Namespace);
        var type = reader.GetString(parent.Name);
        var name = reader.GetString(member.Name);
        var forbidden =
            (ns == "System.Text.Json" && type == "JsonSerializer" && name.StartsWith("Serialize", StringComparison.Ordinal)) ||
            (ns == "System.Text.Json" && type == "Utf8JsonWriter") ||
            (ns == "System.Text.Json.Nodes" && type == "JsonNode" && name == "ToJsonString");
        return forbidden ? $"{type}.{name}" : null;
    }
}
