namespace EtAlii.Lark.Tests;

internal static class TestHelpers
{
    /// <summary>Creates a tree, like Python's <c>Tree(data, [children])</c>.</summary>
    public static Tree T(string data, params object?[] children) => new(data, children.ToList());

    /// <summary>Asserts that the action throws an exception implementing <typeparamref name="TInterface"/>.</summary>
    public static Exception ThrowsIface<TInterface>(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is TInterface)
        {
            return e;
        }
        catch (Exception e)
        {
            Assert.Fail($"Expected an exception implementing {typeof(TInterface).Name}, but got {e.GetType().Name}: {e}");
        }
        Assert.Fail($"Expected an exception implementing {typeof(TInterface).Name}, but nothing was thrown");
        return null!;
    }

    public static Token Tok(string type, string value) => new(type, value);

    /// <summary>Asserts structural equality of two values (trees, tokens, strings, lists), like Python's ==.</summary>
    public static void AssertEq(object? expected, object? actual)
    {
        if (!DeepEquals(expected, actual))
        {
            Assert.Fail($"Values differ.\nExpected: {Show(expected)}\nActual:   {Show(actual)}");
        }
    }

    public static bool DeepEquals(object? a, object? b)
    {
        if (a is System.Collections.IList la && b is System.Collections.IList lb)
        {
            if (la.Count != lb.Count) return false;
            for (var i = 0; i < la.Count; i++)
            {
                if (!DeepEquals(la[i], lb[i])) return false;
            }
            return true;
        }
        if (a is System.Collections.IDictionary da && b is System.Collections.IDictionary db)
        {
            if (da.Count != db.Count) return false;
            foreach (var k in da.Keys)
            {
                if (!db.Contains(k) || !DeepEquals(da[k], db[k])) return false;
            }
            return true;
        }
        if (a is IConvertible && b is IConvertible && a is not string && b is not string && a is not Token && b is not Token && IsNumber(a) && IsNumber(b))
        {
            return Convert.ToDouble(a) == Convert.ToDouble(b);
        }
        if (a is string s && b is Token t1) return t1.Value == s;
        return Tree.ValueEquals(a, b);
    }

    private static bool IsNumber(object o) => o is int or long or double or float or decimal or short or byte;

    public static string Show(object? o) => o switch
    {
        null => "null",
        Tree t => t.ToString(),
        Token t => t.Repr(),
        string s => "\"" + s + "\"",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => o.ToString() ?? "",
    };
}

internal static class SetAssert
{
    /// <summary>Asserts that two collections contain the same (structurally equal) items, ignoring order and duplicates.</summary>
    public static void Equal(IEnumerable<object?> expected, IEnumerable<object?> actual)
    {
        var e = Dedup(expected);
        var a = Dedup(actual);
        var missing = e.Where(x => !a.Any(y => TestHelpers.DeepEquals(x, y))).ToList();
        var extra = a.Where(x => !e.Any(y => TestHelpers.DeepEquals(x, y))).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            Assert.Fail($"Sets differ.\nMissing: {TestHelpers.Show(missing)}\nExtra:   {TestHelpers.Show(extra)}");
        }
    }

    private static List<object?> Dedup(IEnumerable<object?> items)
    {
        var result = new List<object?>();
        foreach (var i in items)
        {
            if (!result.Any(r => TestHelpers.DeepEquals(r, i))) result.Add(i);
        }
        return result;
    }
}
