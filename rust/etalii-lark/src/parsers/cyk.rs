//! The CYK parser, which converts the grammar to Chomsky normal form (Lark's `cyk`).

use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::Arc;

use indexmap::{IndexMap, IndexSet};

use crate::error::{Error, Result};
use crate::grammar::{Rule, Symbol};
use crate::parse_tree_builder::Callbacks;
use crate::token::Token;
use crate::tree::Node;

#[derive(Clone, Debug, PartialEq, Eq)]
enum Alias {
    /// The index of the original rule.
    Orig(usize),
    Split,
    Term,
}

#[derive(Clone, Debug)]
struct CnfRule {
    lhs: Symbol,
    rhs: Vec<Symbol>,
    weight: i64,
    alias: Alias,
    /// For the rules that replace unit rules: the rules that were skipped.
    skipped: Option<Vec<CnfRule>>,
}

impl PartialEq for CnfRule {
    fn eq(&self, other: &CnfRule) -> bool {
        self.lhs == other.lhs && self.rhs == other.rhs && self.skipped == other.skipped
    }
}

impl Eq for CnfRule {}

impl Hash for CnfRule {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.lhs.hash(state);
        self.rhs.hash(state);
    }
}

#[derive(Clone, Debug)]
enum Child {
    Node(RuleNode),
    Token(usize),
}

#[derive(Clone, Debug)]
struct RuleNode {
    rule: CnfRule,
    children: Vec<Child>,
    weight: i64,
}

pub(crate) struct CykParser {
    rules: Vec<Arc<Rule>>,
    callbacks: Arc<Callbacks>,
    terminal_rules: IndexMap<Symbol, Vec<CnfRule>>,
    nonterminal_rules: HashMap<(Symbol, Symbol), Vec<CnfRule>>,
}

fn distinct(rules: Vec<CnfRule>) -> Vec<CnfRule> {
    let set: IndexSet<CnfRule> = rules.into_iter().collect();
    set.into_iter().collect()
}

fn term(g: Vec<CnfRule>) -> Vec<CnfRule> {
    let mut all_t: IndexSet<Symbol> = IndexSet::new();
    for r in &g {
        for x in &r.rhs {
            if x.is_term {
                all_t.insert(x.clone());
            }
        }
    }
    let t_rules: IndexMap<Symbol, CnfRule> = all_t
        .into_iter()
        .map(|t| {
            let rule = CnfRule {
                lhs: Symbol::rule(format!("__T_{:?}", t)),
                rhs: vec![t.clone()],
                weight: 0,
                alias: Alias::Term,
                skipped: None,
            };
            (t, rule)
        })
        .collect();
    let mut new_rules = Vec::new();
    for rule in g {
        if rule.rhs.len() > 1 && rule.rhs.iter().any(|x| x.is_term) {
            let new_rhs = rule
                .rhs
                .iter()
                .map(|x| if x.is_term { t_rules[x].lhs.clone() } else { x.clone() })
                .collect();
            new_rules.push(CnfRule {
                lhs: rule.lhs.clone(),
                rhs: new_rhs,
                weight: rule.weight,
                alias: rule.alias.clone(),
                skipped: None,
            });
            new_rules.extend(t_rules.iter().filter(|(k, _)| rule.rhs.contains(k)).map(|(_, v)| v.clone()));
        } else {
            new_rules.push(rule);
        }
    }
    distinct(new_rules)
}

fn split(rule: &CnfRule) -> Vec<CnfRule> {
    let parts: Vec<String> = rule.rhs.iter().map(|x| format!("{:?}", x)).collect();
    let rule_str = format!("{:?}__{}", rule.lhs, parts.join("_"));
    let name = |i: usize| Symbol::rule(format!("__SP_{}_{}", rule_str, i));
    let n = rule.rhs.len();
    let mut result = vec![CnfRule {
        lhs: rule.lhs.clone(),
        rhs: vec![rule.rhs[0].clone(), name(1)],
        weight: rule.weight,
        alias: rule.alias.clone(),
        skipped: None,
    }];
    for i in 1..n - 2 {
        result.push(CnfRule {
            lhs: name(i),
            rhs: vec![rule.rhs[i].clone(), name(i + 1)],
            weight: 0,
            alias: Alias::Split,
            skipped: None,
        });
    }
    result.push(CnfRule {
        lhs: name(n - 2),
        rhs: rule.rhs[n - 2..].to_vec(),
        weight: 0,
        alias: Alias::Split,
        skipped: None,
    });
    result
}

fn bin(g: Vec<CnfRule>) -> Vec<CnfRule> {
    let mut new_rules = Vec::new();
    for rule in g {
        if rule.rhs.len() > 2 {
            new_rules.extend(split(&rule));
        } else {
            new_rules.push(rule);
        }
    }
    distinct(new_rules)
}

fn build_unit_skip_rule(unit_rule: &CnfRule, target_rule: &CnfRule) -> CnfRule {
    let mut skipped = Vec::new();
    if let Some(s) = &unit_rule.skipped {
        skipped.extend(s.iter().cloned());
    }
    skipped.push(target_rule.clone());
    if let Some(s) = &target_rule.skipped {
        skipped.extend(s.iter().cloned());
    }
    CnfRule {
        lhs: unit_rule.lhs.clone(),
        rhs: target_rule.rhs.clone(),
        weight: unit_rule.weight + target_rule.weight,
        alias: unit_rule.alias.clone(),
        skipped: Some(skipped),
    }
}

fn unit(mut g: Vec<CnfRule>) -> Vec<CnfRule> {
    loop {
        let nt = match g.iter().find(|r| r.rhs.len() == 1 && !r.rhs[0].is_term) {
            Some(r) => r.clone(),
            None => return g,
        };
        let mut new_rules: Vec<CnfRule> = g.iter().filter(|x| **x != nt).cloned().collect();
        new_rules.extend(g.iter().filter(|x| x.lhs == nt.rhs[0]).map(|r| build_unit_skip_rule(&nt, r)));
        g = distinct(new_rules);
    }
}

fn unroll_unit_skip_rule(
    lhs: &Symbol,
    orig_rhs: &[Symbol],
    skipped: &[CnfRule],
    children: Vec<Child>,
    weight: i64,
    alias: &Alias,
) -> RuleNode {
    if skipped.is_empty() {
        return RuleNode {
            rule: CnfRule {
                lhs: lhs.clone(),
                rhs: orig_rhs.to_vec(),
                weight,
                alias: alias.clone(),
                skipped: None,
            },
            children,
            weight,
        };
    }
    let weight = weight - skipped[0].weight;
    let inner = unroll_unit_skip_rule(
        &skipped[0].lhs,
        orig_rhs,
        &skipped[1..],
        children,
        skipped[0].weight,
        &skipped[0].alias,
    );
    RuleNode {
        rule: CnfRule {
            lhs: lhs.clone(),
            rhs: vec![skipped[0].lhs.clone()],
            weight,
            alias: alias.clone(),
            skipped: None,
        },
        children: vec![Child::Node(inner)],
        weight,
    }
}

/// Reverts a parse tree to the form of the original grammar.
fn revert_cnf(node: Child) -> Child {
    let rn = match node {
        Child::Token(t) => return Child::Token(t),
        Child::Node(rn) => rn,
    };
    // Reverts TERM rule.
    if rn.rule.lhs.name.starts_with("__T_") {
        return rn.children.into_iter().next().expect("a terminal");
    }
    let mut children = Vec::new();
    for child in rn.children.into_iter().map(revert_cnf) {
        // Reverts BIN rule.
        match child {
            Child::Node(c) if c.rule.lhs.name.starts_with("__SP_") => children.extend(c.children),
            other => children.push(other),
        }
    }
    // Reverts UNIT rule.
    if let Some(skipped) = &rn.rule.skipped {
        return Child::Node(unroll_unit_skip_rule(
            &rn.rule.lhs,
            &rn.rule.rhs,
            skipped,
            children,
            rn.rule.weight,
            &rn.rule.alias,
        ));
    }
    Child::Node(RuleNode {
        rule: rn.rule,
        children,
        weight: 0,
    })
}

impl CykParser {
    pub fn new(rules: Vec<Arc<Rule>>, callbacks: Arc<Callbacks>) -> Result<CykParser> {
        let cnf: Vec<CnfRule> = rules
            .iter()
            .enumerate()
            .map(|(i, r)| CnfRule {
                lhs: r.origin.clone(),
                rhs: r.expansion.clone(),
                weight: r.options.priority.unwrap_or(0),
                alias: Alias::Orig(i),
                skipped: None,
            })
            .collect();
        let cnf = unit(bin(term(distinct(cnf))));
        let mut terminal_rules: IndexMap<Symbol, Vec<CnfRule>> = IndexMap::new();
        let mut nonterminal_rules: HashMap<(Symbol, Symbol), Vec<CnfRule>> = HashMap::new();
        for r in cnf {
            if r.rhs.len() != 1 && r.rhs.len() != 2 {
                return Err(Error::Parse("CYK doesn't support empty rules".into()));
            }
            if r.rhs.len() == 1 && r.rhs[0].is_term {
                terminal_rules.entry(r.rhs[0].clone()).or_default().push(r);
            } else if r.rhs.len() == 2 && r.rhs.iter().all(|x| !x.is_term) {
                nonterminal_rules.entry((r.rhs[0].clone(), r.rhs[1].clone())).or_default().push(r);
            } else {
                return Err(Error::Parse(format!("Invalid CNF rule: {:?} -> {:?}", r.lhs, r.rhs)));
            }
        }
        Ok(CykParser {
            rules,
            callbacks,
            terminal_rules,
            nonterminal_rules,
        })
    }

    pub fn parse(&self, tokens: Vec<Token>, start: &str) -> Result<Node> {
        let start = Symbol::rule(start);
        let n = tokens.len();
        let mut table: HashMap<(usize, usize), IndexSet<CnfRule>> = HashMap::new();
        let mut trees: HashMap<(usize, usize), HashMap<Symbol, RuleNode>> = HashMap::new();

        // Populate base case with existing terminal production rules
        for (i, w) in tokens.iter().enumerate() {
            for (terminal, rules) in &self.terminal_rules {
                if *terminal.name != w.kind {
                    continue;
                }
                for rule in rules {
                    table.entry((i, i)).or_default().insert(rule.clone());
                    let cell = trees.entry((i, i)).or_default();
                    if cell.get(&rule.lhs).is_none_or(|e| rule.weight < e.weight) {
                        cell.insert(
                            rule.lhs.clone(),
                            RuleNode {
                                rule: rule.clone(),
                                children: vec![Child::Token(i)],
                                weight: rule.weight,
                            },
                        );
                    }
                }
            }
        }

        // Iterate over lengths of sub-sentences
        for l in 2..=n {
            // Iterate over sub-sentences with the given length
            for i in 0..n - l + 1 {
                // Choose partition of the sub-sentence in [1, l)
                for p in i + 1..i + l {
                    let span1 = (i, p - 1);
                    let span2 = (p, i + l - 1);
                    let c1: Vec<CnfRule> = table.get(&span1).map(|s| s.iter().cloned().collect()).unwrap_or_default();
                    let c2: Vec<CnfRule> = table.get(&span2).map(|s| s.iter().cloned().collect()).unwrap_or_default();
                    for r1 in &c1 {
                        for r2 in &c2 {
                            let rules = match self.nonterminal_rules.get(&(r1.lhs.clone(), r2.lhs.clone())) {
                                Some(r) => r,
                                None => continue,
                            };
                            for rule in rules {
                                let target = (i, i + l - 1);
                                table.entry(target).or_default().insert(rule.clone());
                                let r1_tree = trees[&span1][&r1.lhs].clone();
                                let r2_tree = trees[&span2][&r2.lhs].clone();
                                let total = rule.weight + r1_tree.weight + r2_tree.weight;
                                let cell = trees.entry(target).or_default();
                                if cell.get(&rule.lhs).is_none_or(|e| total < e.weight) {
                                    cell.insert(
                                        rule.lhs.clone(),
                                        RuleNode {
                                            rule: rule.clone(),
                                            children: vec![Child::Node(r1_tree), Child::Node(r2_tree)],
                                            weight: total,
                                        },
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        // Check if the parse succeeded.
        let last = (0, n.wrapping_sub(1));
        let ok = n > 0 && table.get(&last).is_some_and(|c| c.iter().any(|r| r.lhs == start));
        if !ok {
            return Err(Error::Parse("Parsing failed.".into()));
        }
        let parse = trees[&last][&start].clone();
        match revert_cnf(Child::Node(parse)) {
            Child::Node(rn) => Ok(self.to_node(rn, &tokens)),
            Child::Token(t) => Ok(Node::Token(tokens[t].clone())),
        }
    }

    /// Builds the tree of the original grammar, with the tree builders of its rules.
    fn to_node(&self, rn: RuleNode, tokens: &[Token]) -> Node {
        let index = match rn.rule.alias {
            Alias::Orig(i) => i,
            _ => unreachable!("reverted rules are original rules"),
        };
        let children = rn
            .children
            .into_iter()
            .map(|c| match c {
                Child::Node(n) => self.to_node(n, tokens),
                Child::Token(t) => Node::Token(tokens[t].clone()),
            })
            .collect();
        self.callbacks.build(&self.rules[index], children)
    }
}
