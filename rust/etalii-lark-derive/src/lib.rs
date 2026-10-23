#![doc = include_str!("../README.md")]

use proc_macro::TokenStream;
use proc_macro2::Span;
use quote::{quote, ToTokens};
use syn::parse::{Parse, ParseStream};
use syn::punctuated::Punctuated;
use syn::{parse_macro_input, Attribute, Error, Item, ItemMod, LitBool, LitStr, Token, Type};

use etalii_lark::codegen::Generator;
use etalii_lark::{LexerKind, ParserKind};

/// Generates a typed parser for a Lark grammar into the module it is put on.
///
/// - `file = "path"`: the grammar file, relative to the crate's `Cargo.toml`; or `grammar = "..."`, the grammar text.
/// - `parser = "earley" | "lalr" | "cyk"`, `lexer = "auto" | "basic" | "contextual" | "dynamic" | "dynamic_complete"`.
/// - `start = "rule"` or `start = ["a", "b"]`; `maybe_placeholders = bool`; `keep_all_tokens = bool`.
///
/// Add `#[lark_result(Type, "name", ...)]` attributes after it to generate a typed visitor whose methods return
/// `Type` for those rules, aliases and terminals.
#[proc_macro_attribute]
pub fn lark_grammar(args: TokenStream, item: TokenStream) -> TokenStream {
    let item = parse_macro_input!(item as ItemMod);
    match expand(args.into(), item) {
        Ok(tokens) => tokens.into(),
        Err(e) => e.to_compile_error().into(),
    }
}

/// Maps rules, aliases and terminals to a result type of the typed visitor: `#[lark_result(f64, "add", "NUMBER")]`.
/// It only has a meaning after `#[lark_grammar]`, which reads it.
#[proc_macro_attribute]
pub fn lark_result(args: TokenStream, item: TokenStream) -> TokenStream {
    // #[lark_grammar] reads the attribute, and leaves it without arguments so that its import counts as used.
    if args.is_empty() {
        return item;
    }
    let mut tokens: proc_macro2::TokenStream = Error::new(
        Span::call_site(),
        "#[lark_result] must come after #[lark_grammar] on the same module",
    )
    .to_compile_error();
    tokens.extend(proc_macro2::TokenStream::from(item));
    tokens.into()
}

struct ResultArgs {
    ty: Type,
    names: Vec<LitStr>,
}

impl Parse for ResultArgs {
    fn parse(input: ParseStream) -> syn::Result<Self> {
        let ty: Type = input.parse()?;
        let mut names = Vec::new();
        while !input.is_empty() {
            input.parse::<Token![,]>()?;
            if input.is_empty() {
                break;
            }
            names.push(input.parse::<LitStr>()?);
        }
        if names.is_empty() {
            return Err(input.error("expected the names of rules, aliases or terminals after the type"));
        }
        Ok(ResultArgs { ty, names })
    }
}

fn is_lark_result(attr: &Attribute) -> bool {
    attr.path().segments.last().is_some_and(|s| s.ident == "lark_result")
}

fn expand(args: proc_macro2::TokenStream, mut module: ItemMod) -> syn::Result<proc_macro2::TokenStream> {
    let mut file: Option<LitStr> = None;
    let mut grammar: Option<LitStr> = None;
    let mut generator_options = Vec::<Box<dyn FnOnce(Generator) -> Generator>>::new();
    let parser = syn::meta::parser(|meta| {
        let name = meta.path.get_ident().map(|i| i.to_string()).unwrap_or_default();
        match name.as_str() {
            "file" => file = Some(meta.value()?.parse()?),
            "grammar" => grammar = Some(meta.value()?.parse()?),
            "parser" => {
                let value: LitStr = meta.value()?.parse()?;
                let kind = match value.value().as_str() {
                    "earley" => ParserKind::Earley,
                    "lalr" => ParserKind::Lalr,
                    "cyk" => ParserKind::Cyk,
                    _ => return Err(Error::new(value.span(), "expected \"earley\", \"lalr\" or \"cyk\"")),
                };
                generator_options.push(Box::new(move |g| g.parser(kind)));
            }
            "lexer" => {
                let value: LitStr = meta.value()?.parse()?;
                let kind = match value.value().as_str() {
                    "auto" => LexerKind::Auto,
                    "basic" => LexerKind::Basic,
                    "contextual" => LexerKind::Contextual,
                    "dynamic" => LexerKind::Dynamic,
                    "dynamic_complete" => LexerKind::DynamicComplete,
                    _ => {
                        return Err(Error::new(
                            value.span(),
                            "expected \"auto\", \"basic\", \"contextual\", \"dynamic\" or \"dynamic_complete\"",
                        ))
                    }
                };
                generator_options.push(Box::new(move |g| g.lexer(kind)));
            }
            "start" => {
                let value = meta.value()?;
                let start: Vec<String> = if value.peek(syn::token::Bracket) {
                    let content;
                    syn::bracketed!(content in value);
                    Punctuated::<LitStr, Token![,]>::parse_terminated(&content)?
                        .iter()
                        .map(LitStr::value)
                        .collect()
                } else {
                    vec![value.parse::<LitStr>()?.value()]
                };
                if start.is_empty() {
                    return Err(meta.error("expected at least one start rule"));
                }
                generator_options.push(Box::new(move |g| g.start(start)));
            }
            "maybe_placeholders" => {
                let value: LitBool = meta.value()?.parse()?;
                let value = value.value;
                generator_options.push(Box::new(move |g| g.maybe_placeholders(value)));
            }
            "keep_all_tokens" => {
                let value: LitBool = meta.value()?.parse()?;
                let value = value.value;
                generator_options.push(Box::new(move |g| g.keep_all_tokens(value)));
            }
            _ => {
                return Err(
                    meta.error("unknown option; expected file, grammar, parser, lexer, start, maybe_placeholders or keep_all_tokens")
                )
            }
        }
        Ok(())
    });
    syn::parse::Parser::parse2(parser, args)?;

    let Some((_, items)) = module.content.as_mut() else {
        return Err(Error::new_spanned(
            &module,
            "#[lark_grammar] needs a module with a body: `mod name {}`",
        ));
    };

    let (mut generator, source_span) = match (file, grammar) {
        (Some(file), None) => {
            let dir = std::env::var("CARGO_MANIFEST_DIR").map_err(|_| Error::new(file.span(), "CARGO_MANIFEST_DIR is not set"))?;
            let path = std::path::Path::new(&dir).join(file.value());
            let generator = Generator::open(&path).map_err(|e| Error::new(file.span(), format!("can't read {}: {e}", path.display())))?;
            (generator, file.span())
        }
        (None, Some(grammar)) => (Generator::new(grammar.value()), grammar.span()),
        _ => {
            return Err(Error::new(
                Span::call_site(),
                "expected either `file = \"...\"` or `grammar = \"...\"`",
            ))
        }
    };
    for option in generator_options {
        generator = option(generator);
    }

    let mut result_spans = Vec::new();
    let mut kept = Vec::new();
    for attr in std::mem::take(&mut module.attrs) {
        if !is_lark_result(&attr) {
            kept.push(attr);
            continue;
        }
        let args: ResultArgs = attr.parse_args()?;
        let ty = args.ty.to_token_stream().to_string();
        generator = generator.result(ty, args.names.iter().map(LitStr::value));
        result_spans.push(attr.span_of_args());
        let path = attr.path();
        kept.push(syn::parse_quote!(#[#path]));
    }
    module.attrs = kept;

    let code = generator.generate().map_err(|e| {
        let span = e.mapping.and_then(|i| result_spans.get(i).copied()).unwrap_or(source_span);
        Error::new(span, e.message)
    })?;
    let generated: proc_macro2::TokenStream = code
        .parse()
        .map_err(|e| Error::new(Span::call_site(), format!("the generated code doesn't parse: {e}")))?;
    items.push(Item::Verbatim(generated));
    Ok(quote!(#module))
}

trait SpanOfArgs {
    fn span_of_args(&self) -> Span;
}

impl SpanOfArgs for Attribute {
    fn span_of_args(&self) -> Span {
        match &self.meta {
            syn::Meta::List(list) => list.delimiter.span().join(),
            _ => self.pound_token.span,
        }
    }
}
