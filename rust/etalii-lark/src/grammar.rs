//! Grammar symbols and BNF rules (Lark's `lark.grammar`).

use std::fmt;
use std::hash::{Hash, Hasher};
use std::sync::Arc;

/// The default priority of terminals.
pub const TOKEN_DEFAULT_PRIORITY: i64 = 0;

/// A grammar symbol: a terminal or a rule (Lark's `Terminal` and `NonTerminal`).
///
/// Symbols are equal when their kind and name are equal; `filter_out` is not compared.
#[derive(Clone)]
pub struct Symbol {
    pub name: Arc<str>,
    pub is_term: bool,
    /// For terminals: whether the token is left out of the tree (anonymous strings and `_TERMINALS`).
    pub filter_out: bool,
}

impl Symbol {
    /// A terminal.
    pub fn terminal(name: impl Into<Arc<str>>, filter_out: bool) -> Symbol {
        Symbol {
            name: name.into(),
            is_term: true,
            filter_out,
        }
    }

    /// A rule (non-terminal).
    pub fn rule(name: impl Into<Arc<str>>) -> Symbol {
        Symbol {
            name: name.into(),
            is_term: false,
            filter_out: false,
        }
    }

    /// The name of the symbol.
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The symbol with its name passed through `f`.
    pub fn renamed(&self, f: impl Fn(&str) -> String) -> Symbol {
        Symbol {
            name: f(&self.name).into(),
            is_term: self.is_term,
            filter_out: self.filter_out,
        }
    }
}

impl PartialEq for Symbol {
    fn eq(&self, other: &Symbol) -> bool {
        self.is_term == other.is_term && self.name == other.name
    }
}

impl Eq for Symbol {}

impl Hash for Symbol {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.name.hash(state);
    }
}

impl PartialOrd for Symbol {
    fn partial_cmp(&self, other: &Symbol) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Symbol {
    fn cmp(&self, other: &Symbol) -> std::cmp::Ordering {
        (self.is_term, &self.name).cmp(&(other.is_term, &other.name))
    }
}

impl fmt::Debug for Symbol {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let kind = if self.is_term { "Terminal" } else { "NonTerminal" };
        write!(f, "{}({})", kind, crate::utils::repr(&self.name))
    }
}

/// How a rule is built into the tree.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RuleOptions {
    /// The `!` modifier: keep all tokens.
    pub keep_all_tokens: bool,
    /// The `?` modifier: inline the rule when it has a single child.
    pub expand1: bool,
    /// The priority (`rule.N:`).
    pub priority: Option<i64>,
    /// The template the rule was created from.
    pub template_source: Option<String>,
    /// For each position of the expansion before `[optional]` items were removed: whether it was removed.
    pub empty_indices: Vec<bool>,
}

/// A BNF production: `origin: expansion` (Lark's `Rule`).
///
/// Rules are equal when their origin and expansion are equal.
#[derive(Clone)]
pub struct Rule {
    pub origin: Symbol,
    pub expansion: Vec<Symbol>,
    /// The index of the expansion among the expansions of the same rule.
    pub order: usize,
    /// The alias (`-> name`).
    pub alias: Option<String>,
    pub options: RuleOptions,
    hash: u64,
}

impl Rule {
    pub fn new(origin: Symbol, expansion: Vec<Symbol>, order: usize, alias: Option<String>, options: RuleOptions) -> Rule {
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        origin.hash(&mut hasher);
        expansion.hash(&mut hasher);
        let hash = hasher.finish();
        Rule {
            origin,
            expansion,
            order,
            alias,
            options,
            hash,
        }
    }
}

impl PartialEq for Rule {
    fn eq(&self, other: &Rule) -> bool {
        self.hash == other.hash && self.origin == other.origin && self.expansion == other.expansion
    }
}

impl Eq for Rule {}

impl Hash for Rule {
    fn hash<H: Hasher>(&self, state: &mut H) {
        state.write_u64(self.hash);
    }
}

impl fmt::Display for Rule {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "<{} : ", self.origin.name)?;
        for (i, s) in self.expansion.iter().enumerate() {
            if i > 0 {
                f.write_str(" ")?;
            }
            f.write_str(&s.name)?;
        }
        f.write_str(">")
    }
}

impl fmt::Debug for Rule {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Rule({:?}, {:?}, {:?}, {:?})",
            self.origin, self.expansion, self.alias, self.options
        )
    }
}
