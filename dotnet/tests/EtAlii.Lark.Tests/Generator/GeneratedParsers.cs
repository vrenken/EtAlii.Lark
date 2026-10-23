namespace EtAlii.Lark.Tests.Generator;

[LarkGrammar("calc.lark", Parser = ParserKind.Lalr)]
public partial class CalcParser;

[LarkGrammar(Grammar = """
    start: pair*
    pair: KEY "=" value _NL
    value: STRING | NUMBER | list
    list: "[" [value ("," value)*] "]"
    !flag: "on" | "off"
    KEY: /[a-z_]+/
    _NL: /\r?\n/
    %import common.ESCAPED_STRING -> STRING
    %import common.SIGNED_NUMBER -> NUMBER
    %import common.WS_INLINE
    %ignore WS_INLINE
    """, Parser = ParserKind.Lalr, Start = new[] { "start", "value" })]
public static partial class ConfigParser;

public static partial class Outer
{
    [LarkGrammar(Grammar = """
        start: greeting NAME
        greeting: "hello" | "hi"
        %import common.CNAME -> NAME
        %import common.WS
        %ignore WS
        """)]
    internal partial class GreetingParser
    {
        public static int ConfigureCalls;

        static partial void ConfigureOptions(LarkOptions options)
        {
            ConfigureCalls++;
            options.PropagatePositions = true;
        }
    }
}
