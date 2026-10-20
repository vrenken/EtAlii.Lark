using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EtAlii.Lark.Generators
{
    /// <summary>
    /// Generates a type-safe parser for each <c>partial</c> class with the <c>[LarkGrammar]</c> attribute.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class LarkGrammarGenerator : IIncrementalGenerator
    {
        private const string AttributeName = "EtAlii.Lark.LarkGrammarAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var classes = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateModel(ctx));

            var grammarFiles = context.AdditionalTextsProvider
                .Where(static f => f.Path.EndsWith(".lark", StringComparison.OrdinalIgnoreCase))
                .Select(static (f, ct) => new GrammarFile(f.Path, f.GetText(ct)?.ToString() ?? ""))
                .Collect();

            context.RegisterSourceOutput(classes.Combine(grammarFiles), static (spc, pair) => Execute(spc, pair.Left, pair.Right));
        }

        private static GrammarClassModel CreateModel(GeneratorAttributeSyntaxContext ctx)
        {
            var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
            var syntax = (ClassDeclarationSyntax)ctx.TargetNode;
            var attribute = ctx.Attributes[0];

            var containing = new List<ContainingType>();
            for (var parent = syntax.Parent as TypeDeclarationSyntax; parent is not null; parent = parent.Parent as TypeDeclarationSyntax)
            {
                var keyword = parent is RecordDeclarationSyntax record
                    ? "record" + (record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? " struct" : "")
                    : parent.Keyword.ValueText;
                containing.Insert(0, new ContainingType(keyword, parent.Identifier.ValueText + parent.TypeParameterList, parent.Modifiers.Any(SyntaxKind.PartialKeyword)));
            }

            var path = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
            string? inline = null;
            var options = new List<OptionValue>();
            var start = new List<string>();
            var keepAllTokens = false;
            foreach (var argument in attribute.NamedArguments)
            {
                var value = argument.Value;
                switch (argument.Key)
                {
                    case "Grammar":
                        inline = value.Value as string;
                        break;
                    case "Parser":
                    case "Lexer":
                    case "Ambiguity":
                        var enumType = argument.Key == "Parser" ? "ParserKind" : argument.Key == "Lexer" ? "LexerKind" : "Ambiguity";
                        options.Add(new OptionValue(argument.Key, $"(global::EtAlii.Lark.{enumType}){Convert.ToInt32(value.Value, CultureInfo.InvariantCulture)}"));
                        break;
                    case "Start" when !value.IsNull:
                        start.AddRange(value.Values.Select(v => v.Value as string).Where(v => v is not null)!);
                        options.Add(new OptionValue("Start", "new string[] { " + string.Join(", ", start.Select(Literal)) + " }"));
                        break;
                    case "KeepAllTokens":
                    case "MaybePlaceholders":
                    case "PropagatePositions":
                    case "Strict":
                        var flag = value.Value is true;
                        if (argument.Key == "KeepAllTokens") keepAllTokens = flag;
                        options.Add(new OptionValue(argument.Key, flag ? "true" : "false"));
                        break;
                }
            }
            if (start.Count == 0) start.Add("start");

            return new GrammarClassModel(
                symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                new EquatableArray<ContainingType>(containing),
                syntax.Identifier.ValueText + syntax.TypeParameterList,
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                syntax.Modifiers.Any(SyntaxKind.PartialKeyword),
                path,
                inline,
                new EquatableArray<OptionValue>(options),
                new EquatableArray<string>(start),
                keepAllTokens,
                LocationInfo.From(syntax.Identifier.GetLocation()));
        }

        private static void Execute(SourceProductionContext context, GrammarClassModel model, ImmutableArray<GrammarFile> files)
        {
            var location = model.Location?.ToLocation();
            if (!model.IsPartial || model.ContainingTypes.Any(t => !t.IsPartial))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotPartial, location, model.ClassName));
                return;
            }

            if ((model.Path is null) == (model.InlineGrammar is null))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.GrammarSourceMissing, location, model.ClassName));
                return;
            }

            string grammar;
            GrammarFile? file = null;
            if (model.Path is not null)
            {
                file = FindFile(files, model.Path);
                if (file is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.GrammarFileNotFound, location, model.Path));
                    return;
                }
                grammar = file.Text;
            }
            else
            {
                grammar = model.InlineGrammar!;
            }

            GrammarModel? grammarModel = null;
            try
            {
                grammarModel = GrammarAnalyzer.Analyze(grammar, model.KeepAllTokens);
            }
            catch (GrammarSyntaxException e)
            {
                var errorLocation = file is not null ? GrammarLocation(file, e.Line, e.Column) : location;
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.GrammarNotAnalyzed, errorLocation, model.ClassName,
                    $"{e.Message} at line {e.Line}, column {e.Column}"));
            }

            var source = new SourceEmitter(model, grammar, grammarModel).Emit();
            context.AddSource(HintName(model), SourceText.From(source, Encoding.UTF8));
        }

        private static GrammarFile? FindFile(ImmutableArray<GrammarFile> files, string path)
        {
            static string Normalize(string p) => p.Replace('\\', '/');
            var wanted = Normalize(path);
            while (wanted.StartsWith("./", StringComparison.Ordinal)) wanted = wanted.Substring(2);

            return files.FirstOrDefault(f => string.Equals(Normalize(f.Path), wanted, StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault(f => Normalize(f.Path).EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static Location GrammarLocation(GrammarFile file, int line, int column)
        {
            var text = SourceText.From(file.Text);
            var lineIndex = Math.Max(0, Math.Min(line - 1, text.Lines.Count - 1));
            var textLine = text.Lines[lineIndex];
            var position = Math.Min(textLine.Start + Math.Max(0, column - 1), textLine.End);
            var span = new TextSpan(position, 0);
            return Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span));
        }

        private static string HintName(GrammarClassModel model)
        {
            var name = model.MetadataName.Replace("global::", "");
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
            }
            return sb + ".Lark.g.cs";
        }

        internal static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
    }
}
