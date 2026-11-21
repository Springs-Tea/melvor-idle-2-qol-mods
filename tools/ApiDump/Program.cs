// Lists the types and member signatures of the game's assembly whose full name matches a pattern: what an
// editor's auto-completion shows, as text. It reads names and signatures only, no code.
//   dotnet run --project tools/ApiDump -- "EnemyCompletion|CombatAreas" ["<game data folder>"]
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

string pattern = args.Length > 0 ? args[0] : ".";
string dir = args.Length > 1 ? args[1] : @"D:\Steam\steamapps\common\Melvor Idle 2\data_GamesByMalcs.Melvor2_windows_x86_64";
var filter = new Regex(pattern, RegexOptions.IgnoreCase);

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string path = Path.Combine(dir, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};
Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir, "GamesByMalcs.Melvor2.dll"));

Type[] types;
try { types = game.GetTypes(); }
catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
foreach (Type type in types.Where(t => filter.IsMatch(t.FullName ?? "") && !t.Name.Contains('<')).OrderBy(t => t.FullName))
{
    string kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
    string bases = string.Join(", ", new[] { type.BaseType }.Where(b => b != null && b != typeof(object) && b != typeof(ValueType)).Select(b => Name(b!))
        .Concat(type.GetInterfaces().Except(type.BaseType?.GetInterfaces() ?? []).Select(Name)));
    Console.WriteLine($"\n{(type.IsPublic || type.IsNestedPublic ? "public" : "internal")} {kind} {type.FullName}{(bases != "" ? " : " + bases : "")}");
    if (type.IsEnum) { Console.WriteLine("  " + string.Join(", ", Enum.GetNames(type))); continue; }
    try
    {
        foreach (var f in type.GetFields(All).Where(f => !f.Name.Contains('<')))
            Console.WriteLine($"  {Access(f.IsPublic, f.IsPrivate)}{(f.IsStatic ? "static " : "")}field {Name(f.FieldType)} {f.Name}");
        foreach (var p in type.GetProperties(All))
        {
            MethodInfo? get = p.GetGetMethod(true), set = p.GetSetMethod(true);
            MethodInfo any = (get ?? set)!;
            Console.WriteLine($"  {Access(any.IsPublic, any.IsPrivate)}{(any.IsStatic ? "static " : "")}{Name(p.PropertyType)} {p.Name} {{ {(get != null ? "get; " : "")}{(set != null ? (set.IsPublic ? "set; " : "private set; ") : "")}}}");
        }
        foreach (var ev in type.GetEvents(All))
            Console.WriteLine($"  {Access(ev.AddMethod?.IsPublic ?? false, ev.AddMethod?.IsPrivate ?? true)}event {Name(ev.EventHandlerType!)} {ev.Name}");
        foreach (var c in type.GetConstructors(All).Where(c => !c.IsStatic))
            Console.WriteLine($"  {Access(c.IsPublic, c.IsPrivate)}ctor({Params(c)})");
        foreach (var m in type.GetMethods(All).Where(m => !m.IsSpecialName && !m.Name.Contains('<')))
            Console.WriteLine($"  {Access(m.IsPublic, m.IsPrivate)}{(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}({Params(m)})");
    }
    catch (Exception e) { Console.WriteLine("  (members not readable: " + e.GetType().Name + ")"); }
}

static string Access(bool isPublic, bool isPrivate) => isPublic ? "" : isPrivate ? "private " : "internal ";
static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}"));
static string Name(Type t)
{
    if (t.IsGenericType)
        return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
    return t.Name;
}
