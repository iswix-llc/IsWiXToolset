// Dumps the public + protected API of an assembly as sorted text lines, and checks that a candidate
// assembly still contains every line of a baseline. Used to keep a replacement WixToolset.Core.Native
// binary-compatible with the original: members may be added, but none may be removed or changed.
//
//   ApiContract dump <assembly.dll> [baseline.txt]      write the API (to stdout if no file given)
//   ApiContract compare <baseline.txt> <candidate.dll>  exit 1 if the candidate lacks any baseline line
//
// Add `--probe <folder>` (repeatable) when the assembly's dependencies, e.g. WixToolset.Data.dll,
// are not in the same folder as the assembly.

using System.Reflection;
using System.Text;

internal static class Program
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly List<string> Probes = new();

    private static int Main(string[] allArgs)
    {
        var rest = new List<string>();
        for (int i = 0; i < allArgs.Length; i++)
        {
            if (allArgs[i] == "--probe" && i + 1 < allArgs.Length)
            {
                Probes.Add(Path.GetFullPath(allArgs[++i]));
            }
            else
            {
                rest.Add(allArgs[i]);
            }
        }
        var args = rest.ToArray();

        try
        {
            if (args.Length >= 2 && args[0] == "dump")
            {
                var lines = Dump(args[1]);
                if (args.Length >= 3)
                {
                    File.WriteAllLines(args[2], lines);
                    Console.WriteLine($"Wrote {lines.Count} lines to {args[2]}");
                }
                else
                {
                    foreach (var line in lines) Console.WriteLine(line);
                }
                return 0;
            }

            if (args.Length == 3 && args[0] == "compare")
            {
                var baseline = File.ReadAllLines(args[1])
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToList();
                var candidate = Dump(args[2]);

                var missing = baseline.Where(l => !candidate.Contains(l)).ToList();
                var added = candidate.Count(l => !baseline.Contains(l));

                foreach (var line in missing) Console.WriteLine("MISSING  " + line);
                Console.WriteLine($"{baseline.Count} baseline lines, {missing.Count} missing, {added} added.");
                return missing.Count == 0 ? 0 : 1;
            }
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine("A dependency could not be found: " + ex.FileName);
            Console.Error.WriteLine("Add --probe <folder containing it>, e.g. --probe build\\api\\Debug\\netstandard2.0");
            return 2;
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine("Could not load all types. Put the assembly's dependencies next to it.");
            foreach (var e in ex.LoaderExceptions.Where(e => e != null).Take(5)) Console.Error.WriteLine("  " + e!.Message);
            return 2;
        }

        Console.Error.WriteLine("usage: ApiContract dump <assembly.dll> [baseline.txt] [--probe <dir>]...");
        Console.Error.WriteLine("       ApiContract compare <baseline.txt> <candidate.dll> [--probe <dir>]...");
        return 2;
    }

    private static SortedSet<string> Dump(string path)
    {
        var directories = new List<string> { Path.GetDirectoryName(Path.GetFullPath(path))! };
        directories.AddRange(Probes);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }

            return null;
        };

        var assembly = Assembly.LoadFrom(path);
        var lines = new SortedSet<string>(StringComparer.Ordinal);

        var name = assembly.GetName();
        var token = Convert.ToHexString(name.GetPublicKeyToken() ?? Array.Empty<byte>()).ToLowerInvariant();
        lines.Add($"A:{name.Name} publicKeyToken={token}");

        foreach (var type in assembly.GetExportedTypes())
        {
            AddType(lines, type);
        }

        return lines;
    }

    private static void AddType(ISet<string> lines, Type type)
    {
        var kind = type.IsEnum ? "enum"
            : type.IsInterface ? "interface"
            : type.IsValueType ? "struct"
            : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
            : "class";
        var modifiers = type.IsAbstract && type.IsSealed ? "static"
            : type.IsAbstract && !type.IsInterface ? "abstract"
            : type.IsSealed && !type.IsValueType && !type.IsEnum ? "sealed"
            : "";
        var baseType = type.BaseType == null || type.IsInterface ? "" : Name(type.BaseType);
        lines.Add($"T:{Name(type)}|{kind}|{modifiers}|base={baseType}");

        foreach (var iface in type.GetInterfaces())
        {
            lines.Add($"I:{Name(type)}:{Name(iface)}");
        }

        foreach (var member in type.GetMembers(AllDeclared))
        {
            switch (member)
            {
                case ConstructorInfo c when Visible(c):
                    lines.Add($"C:{Name(type)}({Params(c)})|{Access(c)}|{(c.IsStatic ? "static" : "")}");
                    break;

                case MethodInfo m when Visible(m) && !IsAccessor(m):
                    var generics = m.IsGenericMethodDefinition
                        ? "<" + string.Join(",", m.GetGenericArguments().Select(a => a.Name)) + ">"
                        : "";
                    lines.Add($"M:{Name(type)}.{m.Name}{generics}({Params(m)}):{Name(m.ReturnType)}|{Access(m)}|{MethodFlags(m)}");
                    break;

                case PropertyInfo p:
                    var get = p.GetMethod != null && Visible(p.GetMethod) ? Access(p.GetMethod) : "-";
                    var set = p.SetMethod != null && Visible(p.SetMethod) ? Access(p.SetMethod) : "-";
                    if (get == "-" && set == "-") break;
                    var index = string.Join(",", p.GetIndexParameters().Select(i => Name(i.ParameterType)));
                    var accessor = p.GetMethod ?? p.SetMethod!;
                    lines.Add($"P:{Name(type)}.{p.Name}[{index}]:{Name(p.PropertyType)}|get={get}|set={set}|{MethodFlags(accessor)}");
                    break;

                case FieldInfo f when f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly:
                    var constant = f.IsLiteral ? "|const=" + Convert.ToString(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) : "";
                    var flags = (f.IsStatic && !f.IsLiteral ? "static " : "") + (f.IsInitOnly ? "readonly" : "");
                    lines.Add($"F:{Name(type)}.{f.Name}:{Name(f.FieldType)}|{flags.Trim()}{constant}");
                    break;

                case EventInfo e when e.AddMethod != null && Visible(e.AddMethod):
                    lines.Add($"E:{Name(type)}.{e.Name}:{Name(e.EventHandlerType!)}|{MethodFlags(e.AddMethod)}");
                    break;
            }
        }
    }

    private static bool Visible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    private static bool IsAccessor(MethodInfo m) =>
        m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_"));

    private static string Access(MethodBase m) => m.IsPublic ? "public" : "protected";

    private static string MethodFlags(MethodBase m)
    {
        var flags = new List<string>();
        if (m.IsStatic) flags.Add("static");
        if (m.IsAbstract) flags.Add("abstract");
        else if (m.IsVirtual && !m.IsFinal) flags.Add("virtual");
        return string.Join(" ", flags);
    }

    private static string Params(MethodBase m) =>
        string.Join(",", m.GetParameters().Select(p =>
        {
            var prefix = p.ParameterType.IsByRef ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") : "";
            return prefix + Name(p.ParameterType);
        }));

    private static string Name(Type t)
    {
        if (t.IsByRef) return Name(t.GetElementType()!);
        if (t.IsArray) return Name(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (t.IsPointer) return Name(t.GetElementType()!) + "*";
        if (t.IsGenericParameter) return t.Name;

        var sb = new StringBuilder();
        if (t.IsNested) sb.Append(Name(t.DeclaringType!)).Append('+');
        else if (!string.IsNullOrEmpty(t.Namespace)) sb.Append(t.Namespace).Append('.');

        var name = t.Name;
        var tick = name.IndexOf('`');
        sb.Append(tick >= 0 ? name[..tick] : name);

        if (t.IsGenericType)
        {
            sb.Append('<').Append(string.Join(",", t.GetGenericArguments().Select(Name))).Append('>');
        }

        return sb.ToString();
    }
}
