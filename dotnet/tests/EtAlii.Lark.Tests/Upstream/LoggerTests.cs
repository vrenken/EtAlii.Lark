// Ported from lark/tests/test_logger.py.
// ReSharper disable InconsistentNaming
namespace EtAlii.Lark.Tests.Upstream;

using System.Text;

[Collection(GlobalStateCollection.Name)]
public class LoggerTests
{
    private const string CollisionGrammar = """
        start: as as
        as: a*
        a: "a"
        """;

    private static string CaptureLog(LarkLogLevel level, Action action)
    {
        var log = new StringBuilder();
        var (oldLevel, oldSink) = (LarkLog.Level, LarkLog.Sink);
        LarkLog.Level = level;
        LarkLog.Sink = (_, message) => log.AppendLine(message);
        try
        {
            action();
        }
        finally
        {
            LarkLog.Level = oldLevel;
            LarkLog.Sink = oldSink;
        }
        return log.ToString();
    }

    [Fact]
    public void test_debug()
    {
        var log = CaptureLog(LarkLogLevel.Debug, () => new Lark(CollisionGrammar, new LarkOptions { Parser = ParserKind.Lalr, Debug = true }));
        // since there are conflicts about A, symbol A should appear in the log message for hint
        Assert.Contains("A", log);
    }

    [Fact]
    public void test_non_debug()
    {
        var log = CaptureLog(LarkLogLevel.Warning, () => new Lark(CollisionGrammar, new LarkOptions { Parser = ParserKind.Lalr, Debug = false }));
        Assert.Equal("", log);
    }

    [Fact]
    public void test_loglevel_higher()
    {
        var log = CaptureLog(LarkLogLevel.Error, () => new Lark(CollisionGrammar, new LarkOptions { Parser = ParserKind.Lalr, Debug = true }));
        Assert.Equal("", log);
    }

    [Fact]
    public void test_regex_collision()
    {
        var log = CaptureLog(LarkLogLevel.Warning, () => new Lark("""
            start: A | B
            A: /a+/
            B: /(a|b)+/
            """, new LarkOptions { Parser = ParserKind.Lalr }));
        // since there are conflicts between A and B, symbols A and B should appear in the log message
        Assert.Contains("A", log);
        Assert.Contains("B", log);
    }

    [Fact]
    public void test_no_regex_collision()
    {
        var log = CaptureLog(LarkLogLevel.Warning, () => new Lark("""
            start: A " " B
            A: /a+/
            B: /(a|b)+/
            """, new LarkOptions { Parser = ParserKind.Lalr }));
        Assert.Equal("", log);
    }
}
