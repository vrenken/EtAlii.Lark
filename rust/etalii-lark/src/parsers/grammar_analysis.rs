//! FIRST/FOLLOW/NULLABLE sets and LR(0) items (Lark's `grammar_analysis`).

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use indexmap::{IndexMap, IndexSet};

use crate::error::{Error, Result};
use crate::grammar::{Rule, RuleOptions, Symbol};
use crate::utils::bfs;

/// A rule with a dot in its expansion: an LR(0) item. `rule` indexes [`GrammarAnalyzer::rules`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub(crate) struct RulePtr {
    pub rule: usize,
    pub index: usize,
}

pub(crate) struct GrammarAnalyzer {
    /// The rules of the grammar, followed by the root rules (`$root_start: start $END`).
    pub rules: Vec<Arc<Rule>>,
    pub rules_by_origin: IndexMap<Symbol, Vec<usize>>,
    /// The rules without `$END` in the root rules, for the LR(0) automaton.
    pub lr0_rules: Vec<Arc<Rule>>,
    pub lr0_rules_by_origin: IndexMap<Symbol, Vec<usize>>,
    pub lr0_root_rules: IndexMap<String, usize>,
    #[allow(dead_code)] // Kept for parity with Lark's GrammarAnalyzer.
    pub first: HashMap<Symbol, HashSet<Symbol>>,
    #[allow(dead_code)]
    pub follow: HashMap<Symbol, HashSet<Symbol>>,
    pub nullable: HashSet<Symbol>,
    pub debug: bool,
    pub strict: bool,
}

fn root(start: &str) -> Symbol {
    Symbol::rule(format!("$root_{}", start))
}

pub(crate) fn end_symbol() -> Symbol {
    Symbol::terminal("$END", false)
}

impl GrammarAnalyzer {
    pub fn new(rules: &[Arc<Rule>], start: &[String], debug: bool, strict: bool) -> Result<GrammarAnalyzer> {
        let mut all: Vec<Arc<Rule>> = rules.to_vec();
        for s in start {
            all.push(Arc::new(Rule::new(
                root(s),
                vec![Symbol::rule(s.as_str()), end_symbol()],
                0,
                None,
                RuleOptions::default(),
            )));
        }
        let mut seen = HashSet::new();
        let mut duplicates = Vec::new();
        for r in &all {
            if !seen.insert(r.as_ref()) {
                duplicates.push(r.to_string());
            }
        }
        if !duplicates.is_empty() {
            return Err(Error::grammar(format!("Rules defined twice: {}", duplicates.join(", "))));
        }
        let rules_by_origin = by_origin(&all);
        for r in &all {
            for sym in &r.expansion {
                if !sym.is_term && !rules_by_origin.contains_key(sym) {
                    return Err(Error::grammar(format!("Using an undefined rule: {:?}", sym)));
                }
            }
        }

        let mut lr0: Vec<Arc<Rule>> = rules.to_vec();
        let mut lr0_root_rules = IndexMap::new();
        for s in start {
            lr0_root_rules.insert(s.clone(), lr0.len());
            lr0.push(Arc::new(Rule::new(
                root(s),
                vec![Symbol::rule(s.as_str())],
                0,
                None,
                RuleOptions::default(),
            )));
        }
        let lr0_rules_by_origin = by_origin(&lr0);
        let (first, follow, nullable) = calculate_sets(&all);
        Ok(GrammarAnalyzer {
            rules: all,
            rules_by_origin,
            lr0_rules: lr0,
            lr0_rules_by_origin,
            lr0_root_rules,
            first,
            follow,
            nullable,
            debug,
            strict,
        })
    }

    /// All the initial items reachable from the rule (Lark's `expand_rule`), for the LR(0) rules.
    pub fn expand_rule_lr0(&self, source: &Symbol) -> IndexSet<RulePtr> {
        expand_rule(source, &self.lr0_rules, &self.lr0_rules_by_origin)
    }
}

fn by_origin(rules: &[Arc<Rule>]) -> IndexMap<Symbol, Vec<usize>> {
    let mut map: IndexMap<Symbol, Vec<usize>> = IndexMap::new();
    for (i, r) in rules.iter().enumerate() {
        map.entry(r.origin.clone()).or_default().push(i);
    }
    map
}

pub(crate) fn expand_rule(source: &Symbol, rules: &[Arc<Rule>], by_origin: &IndexMap<Symbol, Vec<usize>>) -> IndexSet<RulePtr> {
    let mut init_ptrs = IndexSet::new();
    bfs(vec![source.clone()], |rule| {
        let mut next = Vec::new();
        for &r in by_origin.get(rule).map(|v| v.as_slice()).unwrap_or(&[]) {
            init_ptrs.insert(RulePtr { rule: r, index: 0 });
            if let Some(first) = rules[r].expansion.first() {
                if !first.is_term {
                    next.push(first.clone());
                }
            }
        }
        next
    });
    init_ptrs
}

type Sets = (HashMap<Symbol, HashSet<Symbol>>, HashMap<Symbol, HashSet<Symbol>>, HashSet<Symbol>);

/// Calculates the FIRST, FOLLOW and NULLABLE sets.
pub(crate) fn calculate_sets(rules: &[Arc<Rule>]) -> Sets {
    let mut first: HashMap<Symbol, HashSet<Symbol>> = HashMap::new();
    let mut follow: HashMap<Symbol, HashSet<Symbol>> = HashMap::new();
    for r in rules {
        for sym in r.expansion.iter().chain(std::iter::once(&r.origin)) {
            first.entry(sym.clone()).or_insert_with(|| {
                if sym.is_term {
                    [sym.clone()].into_iter().collect()
                } else {
                    HashSet::new()
                }
            });
            follow.entry(sym.clone()).or_default();
        }
    }
    let mut nullable: HashSet<Symbol> = HashSet::new();

    let mut changed = true;
    while changed {
        changed = false;
        for rule in rules {
            if rule.expansion.iter().all(|s| nullable.contains(s)) && nullable.insert(rule.origin.clone()) {
                changed = true;
            }
            for (i, sym) in rule.expansion.iter().enumerate() {
                if rule.expansion[..i].iter().all(|s| nullable.contains(s)) {
                    let add: Vec<Symbol> = first[sym].iter().cloned().collect();
                    let target = first.get_mut(&rule.origin).unwrap();
                    for s in add {
                        changed |= target.insert(s);
                    }
                } else {
                    break;
                }
            }
        }
    }

    changed = true;
    while changed {
        changed = false;
        for rule in rules {
            let exp = &rule.expansion;
            for (i, sym) in exp.iter().enumerate() {
                if i == exp.len() - 1 || exp[i + 1..].iter().all(|s| nullable.contains(s)) {
                    let add: Vec<Symbol> = follow[&rule.origin].iter().cloned().collect();
                    let target = follow.get_mut(sym).unwrap();
                    for s in add {
                        changed |= target.insert(s);
                    }
                }
                for j in i + 1..exp.len() {
                    if exp[i + 1..j].iter().all(|s| nullable.contains(s)) {
                        let add: Vec<Symbol> = first[&exp[j]].iter().cloned().collect();
                        let target = follow.get_mut(sym).unwrap();
                        for s in add {
                            changed |= target.insert(s);
                        }
                    }
                }
            }
        }
    }
    (first, follow, nullable)
}
