namespace EtAlii.Lark;

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

/// <summary>How the arguments of a callback are provided.</summary>
internal enum ArgsMode
{
    Children,
    MetaChildren,
    Tree,
    Inline,
    MetaInline,
}

/// <summary>A method bound to a rule or terminal name.</summary>
internal sealed class BoundMethod
{
    public BoundMethod(MethodInfo method, ArgsMode mode)
    {
        Method = method;
        Mode = mode;
        Parameters = method.GetParameters();
    }

    public MethodInfo Method { get; }

    public ArgsMode Mode { get; }

    public ParameterInfo[] Parameters { get; }

    public bool NeedsMeta => Mode is ArgsMode.MetaChildren or ArgsMode.MetaInline;

    /// <summary>Calls the method with the given rule data.</summary>
    public object? InvokeRule(object target, string data, List<object?> children, Meta? meta, Func<Tree>? makeTree = null)
    {
        object?[] args;
        switch (Mode)
        {
            case ArgsMode.Children:
                args = new[] { ConvertList(children, Parameters[0].ParameterType) };
                break;
            case ArgsMode.MetaChildren:
                args = new[] { meta, ConvertList(children, Parameters[1].ParameterType) };
                break;
            case ArgsMode.Tree:
                args = new object?[] { makeTree is not null ? makeTree() : new Tree(data, children, meta) };
                break;
            case ArgsMode.Inline:
                args = BindInline(children, 0, null);
                break;
            case ArgsMode.MetaInline:
                args = BindInline(children, 1, meta);
                break;
            default:
                throw new InvalidOperationException();
        }
        return Invoke(target, args);
    }

    public object? InvokeToken(object target, Token token)
    {
        var arg = CallbackBinder.ConvertArg(token, Parameters[0].ParameterType);
        return Invoke(target, new[] { arg });
    }

    public object? InvokeTree(object target, Tree tree)
    {
        var arg = CallbackBinder.ConvertArg(tree, Parameters[0].ParameterType);
        return Invoke(target, new[] { arg });
    }

    public object? InvokeRaw(object target, object?[] args) => Invoke(target, args);

    private object? Invoke(object target, object?[] args)
    {
        try
        {
            return Method.Invoke(target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private object?[] BindInline(List<object?> children, int offset, Meta? meta)
    {
        var ps = Parameters;
        var args = new object?[ps.Length];
        if (offset == 1) args[0] = meta;
        var hasParams = ps.Length > offset && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
        var fixedCount = ps.Length - offset - (hasParams ? 1 : 0);

        if (!hasParams && children.Count > fixedCount)
        {
            throw new ArgumentException($"{Method.Name}() takes {fixedCount} arguments but {children.Count} were given");
        }

        for (var i = 0; i < fixedCount; i++)
        {
            var p = ps[offset + i];
            if (i < children.Count)
            {
                args[offset + i] = CallbackBinder.ConvertArg(children[i], p.ParameterType);
            }
            else if (p.HasDefaultValue)
            {
                args[offset + i] = p.DefaultValue;
            }
            else
            {
                throw new ArgumentException($"{Method.Name}() missing required argument '{p.Name}'");
            }
        }

        if (hasParams)
        {
            var elementType = ps[^1].ParameterType.GetElementType()!;
            var rest = children.Count > fixedCount ? children.Skip(fixedCount).ToList() : new List<object?>();
            var array = Array.CreateInstance(elementType, rest.Count);
            for (var i = 0; i < rest.Count; i++) array.SetValue(CallbackBinder.ConvertArg(rest[i], elementType), i);
            args[^1] = array;
        }
        return args;
    }

    private static object? ConvertList(List<object?> children, Type target)
    {
        if (target.IsAssignableFrom(typeof(List<object?>))) return children;
        return CallbackBinder.ConvertArg(children, target);
    }
}

/// <summary>Discovers the callback methods of transformers, visitors and interpreters by reflection.</summary>
internal static class CallbackBinder
{
    private sealed class TypeBindings
    {
        public readonly Dictionary<string, BoundMethod> ByAttribute = new(StringComparer.Ordinal);
        public readonly Dictionary<string, BoundMethod> ByTerminalAttribute = new(StringComparer.Ordinal);
        public readonly Dictionary<string, BoundMethod> ByName = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<Type, TypeBindings> Cache = new();

    private static readonly Assembly LibraryAssembly = typeof(CallbackBinder).Assembly;

    /// <summary>Finds the method bound to the given rule name.</summary>
    public static BoundMethod? FindRule(Type type, string name)
    {
        var b = Cache.GetOrAdd(type, Build);
        if (b.ByAttribute.TryGetValue(name, out var m)) return m;
        if (b.ByName.TryGetValue(name, out m)) return m;
        var pascal = ToPascalCase(name);
        if (pascal.Length > 0 && pascal != name && b.ByName.TryGetValue(pascal, out m)) return m;
        return null;
    }

    /// <summary>Finds the method bound to the given terminal name. Only methods accepting a single token qualify.</summary>
    public static BoundMethod? FindTerminal(Type type, string name)
    {
        var b = Cache.GetOrAdd(type, Build);
        if (b.ByTerminalAttribute.TryGetValue(name, out var m)) return m;
        if (b.ByName.TryGetValue(name, out m) && AcceptsSingleToken(m)) return m;
        var pascal = ToPascalCase(name);
        if (pascal.Length > 0 && pascal != name && b.ByName.TryGetValue(pascal, out m) && AcceptsSingleToken(m)) return m;
        return null;
    }

    /// <summary>All the rule and terminal names that have explicit bindings or method names.</summary>
    public static IEnumerable<string> BoundNames(Type type)
    {
        var b = Cache.GetOrAdd(type, Build);
        return b.ByAttribute.Keys.Concat(b.ByTerminalAttribute.Keys).Concat(b.ByName.Keys).Distinct();
    }

    private static bool AcceptsSingleToken(BoundMethod m) =>
        m.Parameters.Length == 1 && (m.Parameters[0].ParameterType == typeof(Token) || m.Parameters[0].ParameterType == typeof(object));

    /// <summary>Converts a snake_case rule or terminal name into PascalCase: <c>add_expr</c> → <c>AddExpr</c>, <c>NUMBER</c> → <c>Number</c>.</summary>
    public static string ToPascalCase(string name)
    {
        var sb = new StringBuilder();
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            var rest = part.Substring(1);
            sb.Append(rest.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? rest.ToLowerInvariant() : rest);
        }
        return sb.ToString();
    }

    private static TypeBindings Build(Type type)
    {
        var bindings = new TypeBindings();
        var classVArgs = type.GetCustomAttribute<VArgsAttribute>(inherit: true);

        // Walk from the most derived type upwards so that overrides in derived classes win.
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.Assembly == LibraryAssembly) break;
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                if (method.IsSpecialName || method.IsGenericMethodDefinition) continue;
                if (method.IsDefined(typeof(NotARuleAttribute), true)) continue;
                if (method.Name.Contains('<') || method.Name.Contains('.')) continue; // compiler generated / explicit implementations
                var baseDef = method.GetBaseDefinition();
                if (baseDef.DeclaringType is not null && baseDef.DeclaringType.Assembly == LibraryAssembly) continue; // overrides of library hooks

                var methodVArgs = method.GetCustomAttribute<VArgsAttribute>(true) ?? classVArgs;
                var mode = DetermineMode(method, methodVArgs);
                var bound = new BoundMethod(method, mode);

                var ruleAttributes = method.GetCustomAttributes<RuleAttribute>(true).ToList();
                var termAttributes = method.GetCustomAttributes<TerminalAttribute>(true).ToList();
                foreach (var a in ruleAttributes) bindings.ByAttribute.TryAdd(a.Name, bound);
                foreach (var a in termAttributes) bindings.ByTerminalAttribute.TryAdd(a.Name, new BoundMethod(method, ArgsMode.Inline));
                if (ruleAttributes.Count == 0 && termAttributes.Count == 0)
                {
                    bindings.ByName.TryAdd(method.Name, bound);
                }
            }
        }
        return bindings;
    }

    private static bool IsListType(Type t) =>
        t == typeof(List<object?>) || t == typeof(IList<object?>) || t == typeof(IReadOnlyList<object?>) ||
        t == typeof(IEnumerable<object?>) || t == typeof(ICollection<object?>) || t == typeof(IReadOnlyCollection<object?>) ||
        t == typeof(object[]) || t == typeof(IList) || t == typeof(IEnumerable);

    private static ArgsMode DetermineMode(MethodInfo method, VArgsAttribute? vargs)
    {
        var ps = method.GetParameters();
        if (vargs is not null && (vargs.Inline || vargs.Meta || vargs.Tree))
        {
            if (vargs.Tree && (vargs.Inline || vargs.Meta))
            {
                throw new ArgumentException("Visitor functions cannot combine 'tree' with 'meta' or 'inline'.");
            }
            if (vargs.Tree) return ArgsMode.Tree;
            if (vargs.Meta) return vargs.Inline ? ArgsMode.MetaInline : ArgsMode.MetaChildren;
            return ArgsMode.Inline;
        }

        if (ps.Length == 1 && IsListType(ps[0].ParameterType) && !ps[0].IsDefined(typeof(ParamArrayAttribute), false)) return ArgsMode.Children;
        if (ps.Length == 2 && ps[0].ParameterType == typeof(Meta) && IsListType(ps[1].ParameterType)) return ArgsMode.MetaChildren;
        if (ps.Length == 1 && typeof(Tree).IsAssignableFrom(ps[0].ParameterType)) return ArgsMode.Tree;
        if (ps.Length >= 1 && ps[0].ParameterType == typeof(Meta)) return ArgsMode.MetaInline;
        return ArgsMode.Inline;
    }

    /// <summary>Converts a tree child into the type of a callback parameter.</summary>
    public static object? ConvertArg(object? value, Type target)
    {
        if (value is null)
        {
            if (!target.IsValueType || Nullable.GetUnderlyingType(target) is not null) return null;
            throw new InvalidCastException($"Cannot convert None to {target.Name}");
        }
        if (target.IsInstanceOfType(value)) return value;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        var text = value switch
        {
            Token t => t.Value,
            string s => s,
            _ => null,
        };

        if (underlying == typeof(string))
        {
            return text ?? value.ToString();
        }
        if (underlying == typeof(char) && text is { Length: 1 }) return text[0];
        if (text is not null)
        {
            if (underlying.IsEnum) return Enum.Parse(underlying, text, ignoreCase: true);
            if (underlying == typeof(bool)) return bool.Parse(text);
            if (underlying.IsPrimitive || underlying == typeof(decimal))
            {
                return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
            }
        }
        if (value is IConvertible && (underlying.IsPrimitive || underlying == typeof(decimal)))
        {
            return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }

        // Lists of children into typed lists / arrays.
        if (value is IEnumerable enumerable and not string)
        {
            if (underlying.IsArray)
            {
                var elementType = underlying.GetElementType()!;
                var items = enumerable.Cast<object?>().Select(x => ConvertArg(x, elementType)).ToList();
                var array = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }
            if (underlying.IsGenericType)
            {
                var def = underlying.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) || def == typeof(IEnumerable<>) ||
                    def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
                {
                    var elementType = underlying.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                    foreach (var x in enumerable) list.Add(ConvertArg(x, elementType));
                    return list;
                }
            }
        }

        throw new InvalidCastException($"Cannot convert {value.GetType().Name} to {target.Name}");
    }
}
