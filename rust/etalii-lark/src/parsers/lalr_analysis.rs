//! Builds the LALR(1) parse table, with DeRemer and Pennello's lookahead computation (Lark's `lalr_analysis`).

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Arc;

use indexmap::{IndexMap, IndexSet};

use super::grammar_analysis::{end_symbol, GrammarAnalyzer, RulePtr};
use crate::error::{Error, Result};
use crate::grammar::{Rule, Symbol};

/// An action of the parse table.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Action {
    Shift(usize),
    Reduce(Arc<Rule>),
}

/// The LALR(1) parse table: for each state, the action for each terminal and the goto for each rule.
#[derive(Clone, Debug)]
pub struct ParseTable {
    pub states: Vec<IndexMap<String, Action>>,
    pub start_states: IndexMap<String, usize>,
    pub end_states: IndexMap<String, usize>,
}

struct ItemSet {
    kernel: Vec<RulePtr>,
    closure: IndexSet<RulePtr>,
    transitions: IndexMap<Symbol, usize>,
    lookaheads: IndexMap<Symbol, IndexSet<usize>>,
}

type Transition = (usize, Symbol);

pub(crate) fn compute_lalr(analyzer: &GrammarAnalyzer) -> Result<ParseTable> {
    let rules = &analyzer.lr0_rules;
    let is_satisfied = |rp: &RulePtr| rp.index == rules[rp.rule].expansion.len();
    let next = |rp: &RulePtr| rules[rp.rule].expansion[rp.index].clone();

    // LR(0) states
    let mut states: Vec<ItemSet> = Vec::new();
    let mut cache: HashMap<Vec<RulePtr>, usize> = HashMap::new();
    let mut start_ids = IndexMap::new();
    for (start, &rule) in &analyzer.lr0_root_rules {
        let kernel = vec![RulePtr { rule, index: 0 }];
        let closure = analyzer.expand_rule_lr0(&rules[rule].origin);
        let id = states.len();
        cache.insert(kernel.clone(), id);
        states.push(ItemSet {
            kernel,
            closure,
            transitions: IndexMap::new(),
            lookaheads: IndexMap::new(),
        });
        start_ids.insert(start.clone(), id);
    }
    let mut order: Vec<usize> = Vec::new();
    let mut visited: HashSet<usize> = start_ids.values().copied().collect();
    let mut queue: VecDeque<usize> = start_ids.values().copied().collect();
    let mut queued_seen: HashSet<usize> = HashSet::new();
    while let Some(state) = queue.pop_front() {
        let mut by_symbol: IndexMap<Symbol, Vec<RulePtr>> = IndexMap::new();
        for rp in states[state].closure.iter() {
            if !is_satisfied(rp) {
                by_symbol.entry(next(rp)).or_default().push(*rp);
            }
        }
        for (sym, rps) in by_symbol {
            let mut kernel: Vec<RulePtr> = rps
                .iter()
                .map(|rp| RulePtr {
                    rule: rp.rule,
                    index: rp.index + 1,
                })
                .collect();
            kernel.sort();
            kernel.dedup();
            let target = match cache.get(&kernel) {
                Some(&id) => id,
                None => {
                    let mut closure: IndexSet<RulePtr> = kernel.iter().copied().collect();
                    for rp in &kernel {
                        if !is_satisfied(rp) {
                            let n = next(rp);
                            if !n.is_term {
                                closure.extend(analyzer.expand_rule_lr0(&n));
                            }
                        }
                    }
                    let id = states.len();
                    states.push(ItemSet {
                        kernel: kernel.clone(),
                        closure,
                        transitions: IndexMap::new(),
                        lookaheads: IndexMap::new(),
                    });
                    cache.insert(kernel, id);
                    id
                }
            };
            states[state].transitions.insert(sym, target);
            if visited.insert(target) {
                queue.push_back(target);
            }
        }
        if queued_seen.insert(state) {
            order.push(state);
        }
    }

    // Reads relations
    let mut directly_reads: HashMap<Transition, HashSet<Symbol>> = HashMap::new();
    let mut reads: HashMap<Transition, IndexSet<Transition>> = HashMap::new();
    let mut nonterminal_transitions: Vec<Transition> = Vec::new();
    for &root in start_ids.values() {
        for rp in &states[root].kernel {
            directly_reads.insert((root, next(rp)), [end_symbol()].into_iter().collect());
        }
    }
    for &state in &order {
        let mut seen = HashSet::new();
        for rp in states[state].closure.iter() {
            if is_satisfied(rp) {
                continue;
            }
            let s = next(rp);
            if s.is_term || !analyzer.lr0_rules_by_origin.contains_key(&s) {
                continue;
            }
            if !seen.insert(s.clone()) {
                continue;
            }
            let nt = (state, s.clone());
            nonterminal_transitions.push(nt.clone());
            let next_state = states[state].transitions[&s];
            let mut dr = directly_reads.remove(&nt).unwrap_or_default();
            let mut r = reads.remove(&nt).unwrap_or_default();
            for rp2 in states[next_state].closure.iter() {
                if is_satisfied(rp2) {
                    continue;
                }
                let s2 = next(rp2);
                if s2.is_term || !analyzer.lr0_rules_by_origin.contains_key(&s2) {
                    dr.insert(s2.clone());
                }
                if analyzer.nullable.contains(&s2) {
                    r.insert((next_state, s2));
                }
            }
            directly_reads.insert(nt.clone(), dr);
            reads.insert(nt, r);
        }
    }

    // Includes and lookback
    let mut includes: HashMap<Transition, IndexSet<Transition>> = HashMap::new();
    let mut lookback: IndexMap<Transition, IndexSet<(usize, usize)>> = IndexMap::new();
    for nt in &nonterminal_transitions {
        let (state, nonterminal) = nt;
        let mut inc: Vec<Transition> = Vec::new();
        let mut lb = lookback.shift_remove(nt).unwrap_or_default();
        for rp in states[*state].closure.iter() {
            let rule = &rules[rp.rule];
            if &rule.origin != nonterminal {
                continue;
            }
            let mut state2 = *state;
            for i in rp.index..rule.expansion.len() {
                let s = &rule.expansion[i];
                let nt2 = (state2, s.clone());
                state2 = states[state2].transitions[s];
                if !reads.contains_key(&nt2) {
                    continue;
                }
                if rule.expansion[i + 1..].iter().all(|x| analyzer.nullable.contains(x)) {
                    inc.push(nt2);
                }
            }
            if rp.index == 0 {
                for rp2 in states[state2].closure.iter() {
                    if rp2.rule == rp.rule && is_satisfied(rp2) {
                        lb.insert((state2, rp2.rule));
                    }
                }
            }
        }
        lookback.insert(nt.clone(), lb);
        for nt2 in inc {
            includes.entry(nt2).or_default().insert(nt.clone());
        }
    }

    // Lookaheads
    let empty = IndexSet::new();
    let read_sets = digraph(
        &nonterminal_transitions,
        |x| reads.get(x).unwrap_or(&empty),
        |x| directly_reads.get(x).cloned().unwrap_or_default(),
    );
    let follow_sets = digraph(
        &nonterminal_transitions,
        |x| includes.get(x).unwrap_or(&empty),
        |x| read_sets[x].clone(),
    );
    for (nt, lookbacks) in &lookback {
        for &(state, rule) in lookbacks {
            for s in &follow_sets[nt] {
                states[state].lookaheads.entry(s.clone()).or_default().insert(rule);
            }
        }
    }

    // LALR(1) states
    let index_of: HashMap<usize, usize> = order.iter().enumerate().map(|(i, &s)| (s, i)).collect();
    let mut table: Vec<IndexMap<String, Action>> = Vec::with_capacity(order.len());
    let mut reduce_reduce = Vec::new();
    for &state in &order {
        let itemset = &states[state];
        let mut actions: IndexMap<Symbol, Action> = IndexMap::new();
        for (la, &next_state) in &itemset.transitions {
            actions.insert(la.clone(), Action::Shift(index_of[&next_state]));
        }
        for (la, rule_set) in &itemset.lookaheads {
            let mut candidates: Vec<usize> = rule_set.iter().copied().collect();
            if candidates.len() > 1 {
                let mut by_priority: Vec<(i64, usize)> = candidates.iter().map(|&r| (rules[r].options.priority.unwrap_or(0), r)).collect();
                by_priority.sort_by_key(|p| std::cmp::Reverse(p.0));
                if by_priority[0].0 > by_priority[1].0 {
                    candidates = vec![by_priority[0].1];
                } else {
                    reduce_reduce.push((state, la.clone(), candidates));
                    continue;
                }
            }
            let rule = &rules[candidates[0]];
            if actions.contains_key(la) {
                if analyzer.strict {
                    return Err(Error::grammar(format!(
                        "Shift/Reduce conflict for terminal {}. [strict-mode]\n * {}\n",
                        la.name, rule
                    )));
                }
            } else {
                actions.insert(la.clone(), Action::Reduce(rule.clone()));
            }
        }
        table.push(actions.into_iter().map(|(k, v)| (k.name.to_string(), v)).collect());
    }
    if !reduce_reduce.is_empty() {
        let msgs: Vec<String> = reduce_reduce
            .iter()
            .map(|(state, la, rs)| {
                let mut msg = format!(
                    "Reduce/Reduce collision in {:?} between the following rules: {}",
                    la,
                    rs.iter().map(|&r| format!("\n\t- {}", rules[r])).collect::<String>()
                );
                if analyzer.debug {
                    msg.push_str("\n    collision occurred in state: {");
                    for rp in states[*state].closure.iter() {
                        msg.push_str(&format!("\n\t{}", rule_ptr_repr(rules, rp)));
                    }
                    msg.push_str("\n    }");
                }
                msg
            })
            .collect();
        return Err(Error::grammar(msgs.join("\n\n")));
    }

    // The rules in the table are the LR(0) ones; the root rules never get reduced, so nothing else refers to them.
    let mut end_states = IndexMap::new();
    for &state in &order {
        for rp in states[state].closure.iter() {
            for start in analyzer.lr0_root_rules.keys() {
                if *rules[rp.rule].origin.name == format!("$root_{}", start) && is_satisfied(rp) {
                    end_states.insert(start.clone(), index_of[&state]);
                }
            }
        }
    }
    let start_states = start_ids.iter().map(|(k, v)| (k.clone(), index_of[v])).collect();
    Ok(ParseTable {
        states: table,
        start_states,
        end_states,
    })
}

fn rule_ptr_repr(rules: &[Arc<Rule>], rp: &RulePtr) -> String {
    let r = &rules[rp.rule];
    let before: Vec<&str> = r.expansion[..rp.index].iter().map(|s| s.name()).collect();
    let after: Vec<&str> = r.expansion[rp.index..].iter().map(|s| s.name()).collect();
    format!("<{} : {} * {}>", r.origin.name, before.join(" "), after.join(" "))
}

fn digraph<'a>(
    x: &[Transition],
    r: impl Fn(&Transition) -> &'a IndexSet<Transition>,
    g: impl Fn(&Transition) -> HashSet<Symbol>,
) -> HashMap<Transition, HashSet<Symbol>> {
    let mut f: HashMap<Transition, HashSet<Symbol>> = HashMap::new();
    let mut s: Vec<Transition> = Vec::new();
    let mut n: HashMap<Transition, isize> = x.iter().map(|k| (k.clone(), 0)).collect();
    for node in x {
        if n[node] == 0 {
            traverse(node, &mut s, &mut n, &r, &g, &mut f);
        }
    }
    f
}

fn traverse<'a>(
    x: &Transition,
    s: &mut Vec<Transition>,
    n: &mut HashMap<Transition, isize>,
    r: &impl Fn(&Transition) -> &'a IndexSet<Transition>,
    g: &impl Fn(&Transition) -> HashSet<Symbol>,
    f: &mut HashMap<Transition, HashSet<Symbol>>,
) {
    s.push(x.clone());
    let d = s.len() as isize;
    n.insert(x.clone(), d);
    f.insert(x.clone(), g(x));
    for y in r(x) {
        if n.get(y).copied().unwrap_or(0) == 0 {
            traverse(y, s, n, r, g, f);
        }
        let n_x = n[x];
        let n_y = n.get(y).copied().unwrap_or(0);
        if n_y > 0 && n_y < n_x {
            n.insert(x.clone(), n_y);
        }
        let fy: Vec<Symbol> = f.get(y).map(|s| s.iter().cloned().collect()).unwrap_or_default();
        f.get_mut(x).unwrap().extend(fy);
    }
    if n[x] == d {
        let f_x = f[x].clone();
        loop {
            let z = s.pop().unwrap();
            n.insert(z.clone(), -1);
            f.insert(z.clone(), f_x.clone());
            if &z == x {
                break;
            }
        }
    }
}
