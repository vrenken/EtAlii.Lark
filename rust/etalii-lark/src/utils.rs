//! Small helpers shared by the modules, mirroring `lark.utils`.

use std::collections::{HashSet, VecDeque};
use std::hash::Hash;
use std::sync::OnceLock;

use indexmap::IndexMap;
use regex_automata::meta;

/// Groups the items by key, keeping the order in which the keys first appear (Lark's `classify`).
pub(crate) fn classify<T, K: Hash + Eq, V>(
    items: impl IntoIterator<Item = T>,
    key: impl Fn(&T) -> K,
    value: impl Fn(T) -> V,
) -> IndexMap<K, Vec<V>> {
    let mut map: IndexMap<K, Vec<V>> = IndexMap::new();
    for item in items {
        let k = key(&item);
        map.entry(k).or_default().push(value(item));
    }
    map
}

/// Breadth-first search that visits every node once.
pub(crate) fn bfs<T: Hash + Eq + Clone>(initial: impl IntoIterator<Item = T>, mut expand: impl FnMut(&T) -> Vec<T>) -> Vec<T> {
    let mut visited = HashSet::new();
    let mut queue = VecDeque::new();
    for i in initial {
        if visited.insert(i.clone()) {
            queue.push_back(i);
        }
    }
    let mut result = Vec::new();
    while let Some(node) = queue.pop_front() {
        for next in expand(&node) {
            if visited.insert(next.clone()) {
                queue.push_back(next);
            }
        }
        result.push(node);
    }
    result
}

/// Splits `n` up into smaller factors and summands <= `max_factor` (Lark's `small_factors`).
pub(crate) fn small_factors(n: usize, max_factor: usize) -> Vec<(usize, usize)> {
    assert!(max_factor > 2);
    if n <= max_factor {
        return vec![(n, 0)];
    }
    for a in (2..=max_factor).rev() {
        let r = n / a;
        let b = n % a;
        if a + b <= max_factor {
            let mut result = small_factors(r, max_factor);
            result.push((a, b));
            return result;
        }
    }
    unreachable!("Failed to factorize {}", n)
}

/// Python's `str.isupper()`.
pub(crate) fn is_upper(s: &str) -> bool {
    let mut cased = false;
    for c in s.chars() {
        if c.is_lowercase() {
            return false;
        }
        if c.is_uppercase() {
            cased = true;
        }
    }
    cased
}

fn category_regex(cell: &'static OnceLock<meta::Regex>, pattern: &str) -> &'static meta::Regex {
    cell.get_or_init(|| meta::Regex::new(pattern).expect("valid category pattern"))
}

static ID_START: OnceLock<meta::Regex> = OnceLock::new();
static ID_CONTINUE: OnceLock<meta::Regex> = OnceLock::new();

/// Lark's `is_id_start`: every character is a letter, a mark, connector punctuation or `_`.
pub(crate) fn is_id_start(s: &str) -> bool {
    category_regex(&ID_START, r"\A[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Mn}\p{Mc}\p{Pc}_]*\z").is_match(s)
}

/// Lark's `is_id_continue`: like [`is_id_start`], plus decimal digits and letter numbers.
pub(crate) fn is_id_continue(s: &str) -> bool {
    category_regex(
        &ID_CONTINUE,
        r"\A[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Mn}\p{Mc}\p{Pc}\p{Nd}\p{Nl}_]*\z",
    )
    .is_match(s)
}

/// Python's `repr()` of a string.
pub(crate) fn repr(s: &str) -> String {
    let quote = if s.contains('\'') && !s.contains('"') { '"' } else { '\'' };
    let mut out = String::with_capacity(s.len() + 2);
    out.push(quote);
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if c == quote => {
                out.push('\\');
                out.push(c);
            }
            c if (c as u32) < 0x20 || c as u32 == 0x7f => out.push_str(&format!("\\x{:02x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push(quote);
    out
}

/// Python's `str.expandtabs()`.
pub(crate) fn expand_tabs(s: &str) -> String {
    if !s.contains('\t') {
        return s.to_string();
    }
    let mut out = String::new();
    let mut column = 0;
    for c in s.chars() {
        if c == '\t' {
            let spaces = 8 - column % 8;
            out.extend(std::iter::repeat_n(' ', spaces));
            column += spaces;
        } else {
            out.push(c);
            column = if c == '\n' || c == '\r' { 0 } else { column + 1 };
        }
    }
    out
}
