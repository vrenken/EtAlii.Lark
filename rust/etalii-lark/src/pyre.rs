//! Lark's regular expressions are written in the dialect of Python's `re` module. This module parses that dialect
//! and gives the three things Lark needs from it: the width of a pattern (mirroring `sre_parse`'s `getwidth`), a
//! translation to the syntax of the `regex`/`fancy-regex` crates that keeps Python's semantics, and matching.

use std::fmt::Write as _;
use std::sync::{Arc, OnceLock};

use regex_automata::meta;
use regex_automata::{Anchored, Input};

/// Python's `MAXWIDTH` (`1 << 64`), the width of an unbounded pattern.
pub(crate) const MAX_WIDTH: u128 = 1 << 64;
/// Python's `MAXREPEAT`, the upper bound of `*`, `+` and `{n,}`.
const MAX_REPEAT: u128 = 4_294_967_295;

/// An error in a regular expression, with Python's wording where possible.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegexError(pub String);

impl std::fmt::Display for RegexError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for RegexError {}

/// The flags of Python's `re` that change how a pattern is interpreted.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub struct Flags {
    pub ignore_case: bool,
    pub multiline: bool,
    pub dot_all: bool,
    pub verbose: bool,
}

impl Flags {
    /// Parses flags written as letters (`"imsx"`); other letters (`a`, `L`, `u`) are accepted and ignored.
    pub fn from_letters(letters: &str) -> Flags {
        let mut flags = Flags::default();
        for c in letters.chars() {
            flags.set(c, true);
        }
        flags
    }

    fn set(&mut self, c: char, on: bool) {
        match c {
            'i' => self.ignore_case = on,
            'm' => self.multiline = on,
            's' => self.dot_all = on,
            'x' => self.verbose = on,
            _ => {}
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Category {
    Digit,
    Space,
    Word,
}

#[derive(Debug, Clone)]
enum ClassItem {
    Char(char),
    Range(char, char),
    Category(Category, bool),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Look {
    Ahead,
    NotAhead,
    Behind,
    NotBehind,
}

#[derive(Debug, Clone)]
enum Node {
    Empty,
    Literal(char, bool),
    Any(bool),
    Class(Vec<ClassItem>, bool, bool),
    Category(Category, bool),
    /// `^`, multiline or not.
    LineStart(bool),
    /// `$`, multiline or not.
    LineEnd(bool),
    TextStart,
    TextEnd,
    WordBoundary(bool),
    Group(Option<usize>, Box<Node>),
    Atomic(Box<Node>),
    Look(Look, Box<Node>),
    Concat(Vec<Node>),
    Alt(Vec<Node>),
    Repeat {
        node: Box<Node>,
        min: u32,
        max: Option<u32>,
        greedy: bool,
        possessive: bool,
    },
    BackRef(usize, bool),
    Conditional {
        group: usize,
        yes: Box<Node>,
        no: Option<Box<Node>>,
    },
}

/// A parsed regular expression.
#[derive(Debug, Clone)]
pub(crate) struct Ast {
    node: Node,
    group_widths: Vec<(u128, u128)>,
}

struct Parser<'a> {
    chars: Vec<char>,
    pos: usize,
    pattern: &'a str,
    group_count: usize,
    group_names: Vec<(String, usize)>,
    open_groups: Vec<usize>,
    group_widths: Vec<(u128, u128)>,
}

fn is_octal(c: char) -> bool {
    ('0'..='7').contains(&c)
}

impl<'a> Parser<'a> {
    fn error(&self, message: &str) -> RegexError {
        RegexError(format!("{} at position {}", message, self.pos))
    }

    fn peek(&self) -> Option<char> {
        self.chars.get(self.pos).copied()
    }

    fn peek_at(&self, offset: usize) -> Option<char> {
        self.chars.get(self.pos + offset).copied()
    }

    fn starts_with(&self, s: &str) -> bool {
        s.chars().enumerate().all(|(i, c)| self.chars.get(self.pos + i) == Some(&c))
    }

    fn skip_verbose(&mut self, flags: &Flags) {
        if !flags.verbose {
            return;
        }
        while let Some(c) = self.peek() {
            if c.is_whitespace() {
                self.pos += 1;
            } else if c == '#' {
                while let Some(c) = self.peek() {
                    if c == '\n' {
                        break;
                    }
                    self.pos += 1;
                }
            } else {
                break;
            }
        }
    }

    fn parse_alternation(&mut self, flags: &mut Flags, top: bool) -> Result<Node, RegexError> {
        let mut branches = vec![self.parse_sequence(flags, top)?];
        while self.peek() == Some('|') {
            self.pos += 1;
            branches.push(self.parse_sequence(flags, false)?);
        }
        Ok(if branches.len() == 1 {
            branches.pop().unwrap()
        } else {
            Node::Alt(branches)
        })
    }

    fn parse_sequence(&mut self, flags: &mut Flags, mut at_start: bool) -> Result<Node, RegexError> {
        let mut items: Vec<Node> = Vec::new();
        loop {
            self.skip_verbose(flags);
            let c = match self.peek() {
                None | Some('|') | Some(')') => break,
                Some(c) => c,
            };
            let item = match c {
                '*' | '+' | '?' => return Err(self.error("nothing to repeat")),
                '{' if self.repeat_ahead().is_some() => return Err(self.error("nothing to repeat")),
                '(' => {
                    if self.starts_with("(?") && self.global_flags_ahead() {
                        if !at_start || !items.is_empty() {
                            return Err(self.error("global flags not at the start of the expression"));
                        }
                        self.parse_global_flags(flags)?;
                        continue;
                    }
                    self.parse_group(flags)?
                }
                '[' => self.parse_class(flags)?,
                '.' => {
                    self.pos += 1;
                    Node::Any(flags.dot_all)
                }
                '^' => {
                    self.pos += 1;
                    Node::LineStart(flags.multiline)
                }
                '$' => {
                    self.pos += 1;
                    Node::LineEnd(flags.multiline)
                }
                '\\' => self.parse_escape(flags)?,
                c => {
                    self.pos += 1;
                    Node::Literal(c, flags.ignore_case)
                }
            };
            at_start = false;
            let item = self.parse_repeats(item, flags)?;
            items.push(item);
        }
        Ok(match items.len() {
            0 => Node::Empty,
            1 => items.pop().unwrap(),
            _ => Node::Concat(items),
        })
    }

    /// Returns the (min, max, length) of a `{m,n}` quantifier at the current position, if there is one.
    fn repeat_ahead(&self) -> Option<(u32, Option<u32>, usize)> {
        let mut i = self.pos + 1;
        let mut lo = String::new();
        while let Some(c) = self.chars.get(i) {
            if c.is_ascii_digit() {
                lo.push(*c);
                i += 1;
            } else {
                break;
            }
        }
        let mut hi: Option<String> = None;
        if self.chars.get(i) == Some(&',') {
            i += 1;
            let mut h = String::new();
            while let Some(c) = self.chars.get(i) {
                if c.is_ascii_digit() {
                    h.push(*c);
                    i += 1;
                } else {
                    break;
                }
            }
            hi = Some(h);
        }
        if self.chars.get(i) != Some(&'}') {
            return None;
        }
        if lo.is_empty() && hi.is_none() {
            return None;
        }
        let min: u32 = if lo.is_empty() { 0 } else { lo.parse().ok()? };
        let max = match hi {
            None => Some(min),
            Some(h) if h.is_empty() => None,
            Some(h) => Some(h.parse().ok()?),
        };
        Some((min, max, i + 1 - self.pos))
    }

    fn parse_repeats(&mut self, mut item: Node, flags: &Flags) -> Result<Node, RegexError> {
        let mut repeated = false;
        loop {
            self.skip_verbose(flags);
            let (min, max) = match self.peek() {
                Some('*') => {
                    self.pos += 1;
                    (0, None)
                }
                Some('+') => {
                    self.pos += 1;
                    (1, None)
                }
                Some('?') => {
                    self.pos += 1;
                    (0, Some(1))
                }
                Some('{') => match self.repeat_ahead() {
                    Some((min, max, len)) => {
                        if let Some(max) = max {
                            if max < min {
                                return Err(self.error("min repeat greater than max repeat"));
                            }
                        }
                        self.pos += len;
                        (min, max)
                    }
                    None => return Ok(item),
                },
                _ => return Ok(item),
            };
            if repeated {
                return Err(self.error("multiple repeat"));
            }
            if matches!(
                item,
                Node::LineStart(_) | Node::LineEnd(_) | Node::TextStart | Node::TextEnd | Node::WordBoundary(_)
            ) {
                return Err(self.error("nothing to repeat"));
            }
            let mut greedy = true;
            let mut possessive = false;
            match self.peek() {
                Some('?') => {
                    self.pos += 1;
                    greedy = false;
                }
                Some('+') => {
                    self.pos += 1;
                    possessive = true;
                }
                _ => {}
            }
            item = Node::Repeat {
                node: Box::new(item),
                min,
                max,
                greedy,
                possessive,
            };
            repeated = true;
        }
    }

    fn global_flags_ahead(&self) -> bool {
        let mut i = self.pos + 2;
        let mut any = false;
        while let Some(c) = self.chars.get(i) {
            if "aiLmsux".contains(*c) {
                any = true;
                i += 1;
            } else {
                break;
            }
        }
        any && self.chars.get(i) == Some(&')')
    }

    fn parse_global_flags(&mut self, flags: &mut Flags) -> Result<(), RegexError> {
        self.pos += 2;
        while let Some(c) = self.peek() {
            if c == ')' {
                self.pos += 1;
                return Ok(());
            }
            flags.set(c, true);
            self.pos += 1;
        }
        Err(self.error("missing ), unterminated subpattern"))
    }

    fn read_name(&mut self, terminator: char) -> Result<String, RegexError> {
        let start = self.pos;
        while let Some(c) = self.peek() {
            if c == terminator {
                let name: String = self.chars[start..self.pos].iter().collect();
                self.pos += 1;
                if name.is_empty() {
                    return Err(self.error("missing group name"));
                }
                let mut chars = name.chars();
                let first = chars.next().unwrap();
                if !(first == '_' || first.is_alphabetic()) || !chars.all(|c| c == '_' || c.is_alphanumeric()) {
                    return Err(self.error(&format!("bad character in group name '{}'", name)));
                }
                return Ok(name);
            }
            self.pos += 1;
        }
        Err(self.error("missing group name terminator"))
    }

    fn group_ref(&self, name: &str) -> Result<usize, RegexError> {
        if let Ok(n) = name.parse::<usize>() {
            return Ok(n);
        }
        self.group_names
            .iter()
            .find(|(n, _)| n == name)
            .map(|(_, i)| *i)
            .ok_or_else(|| self.error(&format!("unknown group name '{}'", name)))
    }

    fn parse_body(&mut self, flags: &Flags) -> Result<Node, RegexError> {
        let mut inner = *flags;
        let body = self.parse_alternation(&mut inner, false)?;
        if self.peek() != Some(')') {
            return Err(self.error("missing ), unterminated subpattern"));
        }
        self.pos += 1;
        Ok(body)
    }

    fn parse_group(&mut self, flags: &Flags) -> Result<Node, RegexError> {
        self.pos += 1;
        if self.peek() != Some('?') {
            return self.parse_capture(None, flags);
        }
        self.pos += 1;
        match self.peek() {
            Some(':') => {
                self.pos += 1;
                self.parse_body(flags)
            }
            Some('>') => {
                self.pos += 1;
                Ok(Node::Atomic(Box::new(self.parse_body(flags)?)))
            }
            Some('=') => {
                self.pos += 1;
                Ok(Node::Look(Look::Ahead, Box::new(self.parse_body(flags)?)))
            }
            Some('!') => {
                self.pos += 1;
                Ok(Node::Look(Look::NotAhead, Box::new(self.parse_body(flags)?)))
            }
            Some('<') => {
                self.pos += 1;
                let look = match self.peek() {
                    Some('=') => Look::Behind,
                    Some('!') => Look::NotBehind,
                    _ => return Err(self.error("unknown extension ?<")),
                };
                self.pos += 1;
                let body = self.parse_body(flags)?;
                let (lo, hi) = width_of(&body, &self.group_widths);
                if lo != hi {
                    return Err(self.error("look-behind requires fixed-width pattern"));
                }
                Ok(Node::Look(look, Box::new(body)))
            }
            Some('#') => {
                while let Some(c) = self.peek() {
                    self.pos += 1;
                    if c == ')' {
                        return Ok(Node::Empty);
                    }
                }
                Err(self.error("missing ), unterminated comment"))
            }
            Some('P') => {
                self.pos += 1;
                match self.peek() {
                    Some('<') => {
                        self.pos += 1;
                        let name = self.read_name('>')?;
                        if self.group_names.iter().any(|(n, _)| *n == name) {
                            return Err(self.error(&format!("redefinition of group name '{}'", name)));
                        }
                        self.parse_capture(Some(name), flags)
                    }
                    Some('=') => {
                        self.pos += 1;
                        let name = self.read_name(')')?;
                        let group = self.group_ref(&name)?;
                        if self.open_groups.contains(&group) {
                            return Err(self.error("cannot refer to an open group"));
                        }
                        Ok(Node::BackRef(group, flags.ignore_case))
                    }
                    _ => Err(self.error("unknown extension ?P")),
                }
            }
            Some('(') => {
                self.pos += 1;
                let name = self.read_name(')')?;
                let group = self.group_ref(&name)?;
                if group == 0 || group > self.group_count {
                    return Err(self.error("invalid group reference"));
                }
                let mut inner = *flags;
                let yes = self.parse_sequence(&mut inner, false)?;
                let no = if self.peek() == Some('|') {
                    self.pos += 1;
                    let no = self.parse_sequence(&mut inner, false)?;
                    if self.peek() == Some('|') {
                        return Err(self.error("conditional backref with more than two branches"));
                    }
                    Some(Box::new(no))
                } else {
                    None
                };
                if self.peek() != Some(')') {
                    return Err(self.error("missing ), unterminated subpattern"));
                }
                self.pos += 1;
                Ok(Node::Conditional {
                    group,
                    yes: Box::new(yes),
                    no,
                })
            }
            Some(c) if "aiLmsux-".contains(c) => {
                // Scoped flags: (?imsx-imsx:...)
                let mut inner = *flags;
                let mut on = true;
                while let Some(c) = self.peek() {
                    match c {
                        '-' => {
                            if !on {
                                return Err(self.error("bad inline flags"));
                            }
                            on = false;
                        }
                        ':' => break,
                        c if "aiLmsux".contains(c) => {
                            if !on && !"imsx".contains(c) {
                                return Err(self.error("bad inline flags: cannot turn off flags 'a', 'u' and 'L'"));
                            }
                            inner.set(c, on);
                        }
                        ')' => return Err(self.error("global flags not at the start of the expression")),
                        _ => return Err(self.error("unknown flag")),
                    }
                    self.pos += 1;
                }
                if self.peek() != Some(':') {
                    return Err(self.error("missing -, : or )"));
                }
                self.pos += 1;
                let body = self.parse_body(&inner)?;
                Ok(Node::Concat(vec![body]))
            }
            _ => Err(self.error("unknown extension")),
        }
    }

    fn parse_capture(&mut self, name: Option<String>, flags: &Flags) -> Result<Node, RegexError> {
        self.group_count += 1;
        let index = self.group_count;
        if let Some(name) = name {
            self.group_names.push((name, index));
        }
        self.open_groups.push(index);
        self.group_widths.push((0, 0));
        let body = self.parse_body(flags)?;
        self.open_groups.pop();
        self.group_widths[index - 1] = width_of(&body, &self.group_widths);
        Ok(Node::Group(Some(index), Box::new(body)))
    }

    fn parse_hex(&mut self, digits: usize) -> Result<char, RegexError> {
        let start = self.pos;
        let text: String = self
            .chars
            .get(start..start + digits)
            .map(|s| s.iter().collect())
            .unwrap_or_default();
        if text.len() != digits || !text.chars().all(|c| c.is_ascii_hexdigit()) {
            return Err(self.error("incomplete escape"));
        }
        self.pos += digits;
        let value = u32::from_str_radix(&text, 16).unwrap();
        char::from_u32(value).ok_or_else(|| self.error("bad escape (invalid code point)"))
    }

    /// Escapes that mean the same inside and outside a class. Returns None for the others.
    fn parse_common_escape(&mut self, c: char) -> Result<Option<char>, RegexError> {
        Ok(Some(match c {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'f' => '\x0c',
            'v' => '\x0b',
            'a' => '\x07',
            'x' => return self.parse_hex(2).map(Some),
            'u' => return self.parse_hex(4).map(Some),
            'U' => return self.parse_hex(8).map(Some),
            'N' => return Err(self.error("named Unicode escapes (\\N{...}) are not supported")),
            c if c.is_ascii_alphanumeric() => return Ok(None),
            c => c,
        }))
    }

    fn parse_escape(&mut self, flags: &Flags) -> Result<Node, RegexError> {
        self.pos += 1;
        let c = self.peek().ok_or_else(|| self.error("bad escape (end of pattern)"))?;
        self.pos += 1;
        let node = match c {
            'A' => Node::TextStart,
            'Z' => Node::TextEnd,
            'b' => Node::WordBoundary(true),
            'B' => Node::WordBoundary(false),
            'd' => Node::Category(Category::Digit, false),
            'D' => Node::Category(Category::Digit, true),
            's' => Node::Category(Category::Space, false),
            'S' => Node::Category(Category::Space, true),
            'w' => Node::Category(Category::Word, false),
            'W' => Node::Category(Category::Word, true),
            '0' => {
                let mut value = 0u32;
                let mut n = 0;
                while n < 2 && self.peek().is_some_and(is_octal) {
                    value = value * 8 + self.peek().unwrap().to_digit(8).unwrap();
                    self.pos += 1;
                    n += 1;
                }
                Node::Literal(char::from_u32(value).unwrap(), flags.ignore_case)
            }
            '1'..='9' => {
                // An octal escape of three digits, or a group reference of one or two digits.
                if self.peek().is_some_and(is_octal) && is_octal(c) && self.peek_at(1).is_some_and(is_octal) {
                    let digits: String = [c, self.peek().unwrap(), self.peek_at(1).unwrap()].iter().collect();
                    let value = u32::from_str_radix(&digits, 8).unwrap();
                    if value > 0o377 {
                        return Err(self.error("octal escape value outside of range 0-0o377"));
                    }
                    self.pos += 2;
                    Node::Literal(char::from_u32(value).unwrap(), flags.ignore_case)
                } else {
                    let mut group = c.to_digit(10).unwrap() as usize;
                    if self.peek().is_some_and(|d| d.is_ascii_digit()) {
                        group = group * 10 + self.peek().unwrap().to_digit(10).unwrap() as usize;
                        self.pos += 1;
                    }
                    if group > self.group_count {
                        return Err(self.error("invalid group reference"));
                    }
                    if self.open_groups.contains(&group) {
                        return Err(self.error("cannot refer to an open group"));
                    }
                    Node::BackRef(group, flags.ignore_case)
                }
            }
            c => match self.parse_common_escape(c)? {
                Some(ch) => Node::Literal(ch, flags.ignore_case),
                None => return Err(self.error(&format!("bad escape \\{}", c))),
            },
        };
        Ok(node)
    }

    fn parse_class_char(&mut self) -> Result<ClassItem, RegexError> {
        let c = self.peek().ok_or_else(|| self.error("unterminated character set"))?;
        self.pos += 1;
        if c != '\\' {
            return Ok(ClassItem::Char(c));
        }
        let e = self.peek().ok_or_else(|| self.error("unterminated character set"))?;
        self.pos += 1;
        Ok(match e {
            'd' => ClassItem::Category(Category::Digit, false),
            'D' => ClassItem::Category(Category::Digit, true),
            's' => ClassItem::Category(Category::Space, false),
            'S' => ClassItem::Category(Category::Space, true),
            'w' => ClassItem::Category(Category::Word, false),
            'W' => ClassItem::Category(Category::Word, true),
            'b' => ClassItem::Char('\x08'),
            c if is_octal(c) => {
                let mut value = c.to_digit(8).unwrap();
                let mut n = 1;
                while n < 3 && self.peek().is_some_and(is_octal) {
                    value = value * 8 + self.peek().unwrap().to_digit(8).unwrap();
                    self.pos += 1;
                    n += 1;
                }
                if value > 0o377 {
                    return Err(self.error("octal escape value outside of range 0-0o377"));
                }
                ClassItem::Char(char::from_u32(value).unwrap())
            }
            c => match self.parse_common_escape(c)? {
                Some(ch) => ClassItem::Char(ch),
                None => return Err(self.error(&format!("bad escape \\{}", c))),
            },
        })
    }

    fn parse_class(&mut self, flags: &Flags) -> Result<Node, RegexError> {
        self.pos += 1;
        let negated = if self.peek() == Some('^') {
            self.pos += 1;
            true
        } else {
            false
        };
        let mut items = Vec::new();
        let mut first = true;
        loop {
            let c = self.peek().ok_or_else(|| self.error("unterminated character set"))?;
            if c == ']' && !first {
                self.pos += 1;
                break;
            }
            first = false;
            let item = self.parse_class_char()?;
            if self.peek() == Some('-') && self.peek_at(1).is_some_and(|c| c != ']') {
                self.pos += 1;
                let end = self.parse_class_char()?;
                match (item, end) {
                    (ClassItem::Char(lo), ClassItem::Char(hi)) => {
                        if hi < lo {
                            return Err(self.error("bad character range"));
                        }
                        items.push(ClassItem::Range(lo, hi));
                    }
                    _ => return Err(self.error("bad character range")),
                }
            } else {
                items.push(item);
            }
        }
        Ok(Node::Class(items, negated, flags.ignore_case))
    }
}

fn add(a: u128, b: u128) -> u128 {
    a.saturating_add(b)
}

/// `sre_parse`'s `getwidth`.
fn width_of(node: &Node, groups: &[(u128, u128)]) -> (u128, u128) {
    let (lo, hi) = match node {
        Node::Empty | Node::LineStart(_) | Node::LineEnd(_) | Node::TextStart | Node::TextEnd | Node::WordBoundary(_) | Node::Look(..) => {
            (0, 0)
        }
        Node::Literal(..) | Node::Any(_) | Node::Class(..) | Node::Category(..) => (1, 1),
        Node::Group(_, body) | Node::Atomic(body) => width_of(body, groups),
        Node::Concat(items) => items.iter().fold((0, 0), |(lo, hi), item| {
            let (l, h) = width_of(item, groups);
            (add(lo, l), add(hi, h))
        }),
        Node::Alt(branches) => {
            let mut lo = MAX_WIDTH;
            let mut hi = 0;
            for b in branches {
                let (l, h) = width_of(b, groups);
                lo = lo.min(l);
                hi = hi.max(h);
            }
            (lo, hi)
        }
        Node::Repeat { node, min, max, .. } => {
            let (i, j) = width_of(node, groups);
            let lo = i.saturating_mul(*min as u128);
            let max = max.map(|m| m as u128).unwrap_or(MAX_REPEAT);
            let hi = if max == MAX_REPEAT && j > 0 {
                MAX_WIDTH
            } else {
                j.saturating_mul(max)
            };
            (lo, hi)
        }
        Node::BackRef(group, _) => groups.get(group - 1).copied().unwrap_or((0, 0)),
        Node::Conditional { yes, no, .. } => {
            let (i, j) = width_of(yes, groups);
            match no {
                Some(no) => {
                    let (l, h) = width_of(no, groups);
                    (i.min(l), j.max(h))
                }
                None => (0, j),
            }
        }
    };
    (lo.min(MAX_WIDTH), hi.min(MAX_WIDTH))
}

const WORD_CLASS: &str = r"\p{L}\p{N}_";
const SPACE_CLASS: &str = r"\s\x{1c}-\x{1f}";
const DIGIT_CLASS: &str = r"\p{Nd}";

fn category_class(category: Category) -> &'static str {
    match category {
        Category::Digit => DIGIT_CLASS,
        Category::Space => SPACE_CLASS,
        Category::Word => WORD_CLASS,
    }
}

fn push_char(out: &mut String, c: char) {
    if c.is_ascii_alphanumeric() || (!c.is_ascii() && !c.is_whitespace() && !c.is_control()) {
        out.push(c);
    } else {
        let _ = write!(out, r"\x{{{:x}}}", c as u32);
    }
}

fn case_group(out: &mut String, ignore_case: bool, f: impl FnOnce(&mut String)) {
    if ignore_case {
        out.push_str("(?i:");
        f(out);
        out.push(')');
    } else {
        f(out);
    }
}

/// Emits the node in the syntax of `fancy-regex` (which is that of `regex` plus look-around and back-references).
/// Flags are never left to the target engine: each construct is emitted with the meaning it has in Python.
fn emit(node: &Node, out: &mut String) {
    match node {
        Node::Empty => {}
        Node::Literal(c, ic) => case_group(out, *ic && c.to_lowercase().ne(c.to_uppercase()), |out| push_char(out, *c)),
        Node::Any(dot_all) => out.push_str(if *dot_all { r"(?s:.)" } else { r"[^\n]" }),
        Node::Class(items, negated, ic) => case_group(out, *ic, |out| {
            out.push('[');
            if *negated {
                out.push('^');
            }
            for item in items {
                match item {
                    ClassItem::Char(c) => push_char(out, *c),
                    ClassItem::Range(a, b) => {
                        push_char(out, *a);
                        out.push('-');
                        push_char(out, *b);
                    }
                    ClassItem::Category(cat, false) => out.push_str(category_class(*cat)),
                    ClassItem::Category(cat, true) => {
                        out.push_str("[^");
                        out.push_str(category_class(*cat));
                        out.push(']');
                    }
                }
            }
            if items.is_empty() {
                // Python doesn't allow an empty set, but keep the target valid anyway.
                out.push_str(if *negated { r"\x{0}-\x{10ffff}" } else { r"\x{0}" });
            }
            out.push(']');
        }),
        Node::Category(cat, negated) => {
            out.push('[');
            if *negated {
                out.push('^');
            }
            out.push_str(category_class(*cat));
            out.push(']');
        }
        Node::LineStart(multiline) => out.push_str(if *multiline { "(?m:^)" } else { r"\A" }),
        Node::LineEnd(multiline) => out.push_str(if *multiline { "(?m:$)" } else { r"(?:\z|(?=\n\z))" }),
        Node::TextStart => out.push_str(r"\A"),
        Node::TextEnd => out.push_str(r"\z"),
        Node::WordBoundary(true) => {
            let _ = write!(out, "(?:(?<=[{w}])(?![{w}])|(?<![{w}])(?=[{w}]))", w = WORD_CLASS);
        }
        Node::WordBoundary(false) => {
            let _ = write!(out, "(?:(?<=[{w}])(?=[{w}])|(?<![{w}])(?![{w}]))", w = WORD_CLASS);
        }
        Node::Group(_, body) => {
            out.push('(');
            emit(body, out);
            out.push(')');
        }
        Node::Atomic(body) => {
            out.push_str("(?>");
            emit(body, out);
            out.push(')');
        }
        Node::Look(look, body) => {
            out.push_str(match look {
                Look::Ahead => "(?=",
                Look::NotAhead => "(?!",
                Look::Behind => "(?<=",
                Look::NotBehind => "(?<!",
            });
            emit(body, out);
            out.push(')');
        }
        Node::Concat(items) => {
            for item in items {
                // Wrap alternations so that they don't swallow their neighbours.
                if matches!(item, Node::Alt(_)) {
                    out.push_str("(?:");
                    emit(item, out);
                    out.push(')');
                } else {
                    emit(item, out);
                }
            }
        }
        Node::Alt(branches) => {
            for (i, b) in branches.iter().enumerate() {
                if i > 0 {
                    out.push('|');
                }
                emit(b, out);
            }
        }
        Node::Repeat {
            node,
            min,
            max,
            greedy,
            possessive,
        } => {
            if *possessive {
                out.push_str("(?>");
            }
            out.push_str("(?:");
            emit(node, out);
            out.push(')');
            match (min, max) {
                (0, None) => out.push('*'),
                (1, None) => out.push('+'),
                (0, Some(1)) => out.push('?'),
                (m, None) => {
                    let _ = write!(out, "{{{},}}", m);
                }
                (m, Some(n)) if m == n => {
                    let _ = write!(out, "{{{}}}", m);
                }
                (m, Some(n)) => {
                    let _ = write!(out, "{{{},{}}}", m, n);
                }
            }
            if !greedy {
                out.push('?');
            }
            if *possessive {
                out.push(')');
            }
        }
        Node::BackRef(group, ic) => case_group(out, *ic, |out| {
            let _ = write!(out, r"\{}", group);
        }),
        Node::Conditional { group, yes, no } => {
            let _ = write!(out, "(?({})", group);
            emit(yes, out);
            out.push('|');
            if let Some(no) = no {
                emit(no, out);
            }
            out.push(')');
        }
    }
}

/// Whether the node needs a backtracking engine (look-around, back-references, atomic groups).
fn needs_backtracking(node: &Node) -> bool {
    match node {
        Node::LineEnd(false) | Node::WordBoundary(_) | Node::Atomic(_) | Node::Look(..) | Node::BackRef(..) | Node::Conditional { .. } => {
            true
        }
        Node::Repeat { node, possessive, .. } => *possessive || needs_backtracking(node),
        Node::Group(_, body) => needs_backtracking(body),
        Node::Concat(items) | Node::Alt(items) => items.iter().any(needs_backtracking),
        _ => false,
    }
}

impl Ast {
    /// Parses a pattern in Python's `re` dialect.
    pub(crate) fn parse(pattern: &str, flags: Flags) -> Result<Ast, RegexError> {
        let mut parser = Parser {
            chars: pattern.chars().collect(),
            pos: 0,
            pattern,
            group_count: 0,
            group_names: Vec::new(),
            open_groups: Vec::new(),
            group_widths: Vec::new(),
        };
        let mut flags = flags;
        let node = parser.parse_alternation(&mut flags, true)?;
        if parser.pos < parser.chars.len() {
            return Err(parser.error("unbalanced parenthesis"));
        }
        let _ = parser.pattern;
        // Global flags found in the pattern apply to all of it, which only matters for the parts parsed before them;
        // Python only allows them at the start, so nothing was.
        Ok(Ast {
            node,
            group_widths: parser.group_widths,
        })
    }

    /// The minimum and maximum number of characters the pattern matches, as Python computes them.
    pub(crate) fn width(&self) -> (u128, u128) {
        width_of(&self.node, &self.group_widths)
    }

    pub(crate) fn to_rust(&self) -> String {
        let mut out = String::new();
        emit(&self.node, &mut out);
        out
    }

    pub(crate) fn needs_backtracking(&self) -> bool {
        needs_backtracking(&self.node)
    }
}

/// Python's `re.escape`.
pub fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len() * 2);
    for c in value.chars() {
        if "()[]{}?*+-|^$\\.&~# \t\n\r\x0b\x0c".contains(c) {
            out.push('\\');
            match c {
                '\t' => out.push('t'),
                '\n' => out.push('n'),
                '\r' => out.push('r'),
                '\x0b' => out.push('v'),
                '\x0c' => out.push('f'),
                c => out.push(c),
            }
        } else {
            out.push(c);
        }
    }
    out
}

/// The width of a pattern, as Python's `sre_parse` computes it.
pub(crate) fn width(pattern: &str, flags: Flags) -> Result<(u128, u128), RegexError> {
    Ok(Ast::parse(pattern, flags)?.width())
}

#[derive(Debug)]
enum Engine {
    Automata(meta::Regex),
    Fancy(fancy_regex::Regex),
}

impl Engine {
    fn build(translated: &str, backtracking: bool, full: bool) -> Result<Engine, RegexError> {
        let end = if full { r"\z" } else { "" };
        if backtracking {
            fancy_regex::RegexBuilder::new(&format!(r"\G(?:{}){}", translated, end))
                .backtrack_limit(10_000_000)
                .build()
                .map(Engine::Fancy)
                .map_err(|e| RegexError(e.to_string()))
        } else {
            meta::Regex::builder()
                .configure(meta::Regex::config().match_kind(regex_automata::MatchKind::LeftmostFirst))
                .build(&format!("(?:{}){}", translated, end))
                .map(Engine::Automata)
                .map_err(|e| RegexError(e.to_string()))
        }
    }

    fn match_at(&self, text: &str, pos: usize) -> Option<usize> {
        match self {
            Engine::Automata(re) => {
                let input = Input::new(text).range(pos..).anchored(Anchored::Yes);
                re.search(&input).map(|m| m.end())
            }
            Engine::Fancy(re) => match re.find_from_pos(text, pos) {
                Ok(Some(m)) if m.start() == pos => Some(m.end()),
                _ => None,
            },
        }
    }
}

#[derive(Debug)]
struct Inner {
    pattern: String,
    translated: String,
    backtracking: bool,
    engine: Engine,
    full: OnceLock<Option<Engine>>,
}

/// A compiled regular expression with Python's semantics.
#[derive(Debug, Clone)]
pub struct Regex {
    inner: Arc<Inner>,
}

impl Regex {
    /// Compiles a pattern in Python's `re` dialect.
    pub fn new(pattern: &str, flags: Flags) -> Result<Regex, RegexError> {
        let ast = Ast::parse(pattern, flags)?;
        let translated = ast.to_rust();
        let backtracking = ast.needs_backtracking();
        let engine = Engine::build(&translated, backtracking, false)?;
        Ok(Regex {
            inner: Arc::new(Inner {
                pattern: pattern.to_string(),
                translated,
                backtracking,
                engine,
                full: OnceLock::new(),
            }),
        })
    }

    /// The pattern, as written in Python's dialect.
    pub fn as_str(&self) -> &str {
        &self.inner.pattern
    }

    /// Python's `pattern.match(text, pos)`: the end of the match that starts at `pos`, if any. Assertions see the
    /// text before `pos`, as in Python.
    pub fn match_at(&self, text: &str, pos: usize) -> Option<usize> {
        self.inner.engine.match_at(text, pos)
    }

    /// Python's `re.search(pattern, text, pos)`: the first match at or after `pos`.
    pub fn search_from(&self, text: &str, pos: usize) -> Option<(usize, usize)> {
        let mut start = pos;
        loop {
            if let Some(end) = self.match_at(text, start) {
                return Some((start, end));
            }
            if start >= text.len() {
                return None;
            }
            start += text[start..].chars().next().map(|c| c.len_utf8()).unwrap_or(1);
        }
    }

    /// Python's `fullmatch`: whether the whole text matches, trying every way the pattern can match.
    pub fn full_match(&self, text: &str) -> bool {
        self.full_match_range(text, 0, text.len())
    }

    /// Whether `text[start..end]` matches as a whole (Python's `fullmatch(text, start, end)`).
    pub fn full_match_range(&self, text: &str, start: usize, end: usize) -> bool {
        let full = self
            .inner
            .full
            .get_or_init(|| Engine::build(&self.inner.translated, self.inner.backtracking, true).ok());
        match full {
            Some(engine) => engine.match_at(&text[..end], start).is_some(),
            None => false,
        }
    }
}

const SAMPLE_LIMIT: usize = 256;

fn class_contains(items: &[ClassItem], c: char) -> bool {
    items.iter().any(|item| match item {
        ClassItem::Char(x) => *x == c,
        ClassItem::Range(a, b) => *a <= c && c <= *b,
        ClassItem::Category(cat, negated) => category_contains(*cat, c) != *negated,
    })
}

fn category_contains(cat: Category, c: char) -> bool {
    match cat {
        Category::Digit => c.is_ascii_digit() || (!c.is_ascii() && c.is_numeric()),
        Category::Space => c.is_whitespace() || ('\x1c'..='\x1f').contains(&c),
        Category::Word => c == '_' || c.is_alphanumeric(),
    }
}

const SAMPLE_ALPHABET: &str = "aAzZ09_ -+.,;:!?\"'()[]{}<>/\\|@#$%^&*=~`\t\néß";

fn sample_chars(node: &Node) -> Vec<char> {
    match node {
        Node::Literal(c, ic) => {
            let mut v = vec![*c];
            if *ic {
                v.extend(c.to_uppercase().chain(c.to_lowercase()).filter(|x| x != c));
            }
            v
        }
        Node::Any(dot_all) => {
            let mut v: Vec<char> = SAMPLE_ALPHABET.chars().collect();
            if !dot_all {
                v.retain(|c| *c != '\n');
            }
            v
        }
        Node::Class(items, negated, _) => {
            let mut v = Vec::new();
            if !negated {
                for item in items {
                    match item {
                        ClassItem::Char(c) => v.push(*c),
                        ClassItem::Range(a, b) => {
                            v.push(*a);
                            v.push(*b);
                            if let Some(mid) = char::from_u32((*a as u32 + *b as u32) / 2) {
                                v.push(mid);
                            }
                        }
                        ClassItem::Category(..) => {
                            v.extend(SAMPLE_ALPHABET.chars().filter(|c| class_contains(std::slice::from_ref(item), *c)))
                        }
                    }
                }
            } else {
                v.extend(SAMPLE_ALPHABET.chars().filter(|c| !class_contains(items, *c)));
            }
            v.dedup();
            v
        }
        Node::Category(cat, negated) => SAMPLE_ALPHABET
            .chars()
            .filter(|c| category_contains(*cat, *c) != *negated)
            .collect(),
        _ => Vec::new(),
    }
}

fn samples_of(node: &Node, groups: &mut Vec<Vec<String>>) -> Vec<String> {
    let mut out: Vec<String> = match node {
        Node::Literal(..) | Node::Any(_) | Node::Class(..) | Node::Category(..) => {
            sample_chars(node).into_iter().map(String::from).collect()
        }
        Node::Group(index, body) => {
            let s = samples_of(body, groups);
            if let Some(i) = index {
                if groups.len() < *i {
                    groups.resize(*i, Vec::new());
                }
                groups[i - 1] = s.clone();
            }
            s
        }
        Node::Atomic(body) => samples_of(body, groups),
        Node::BackRef(i, _) => groups.get(i - 1).cloned().unwrap_or_else(|| vec![String::new()]),
        Node::Concat(items) => {
            let mut acc = vec![String::new()];
            for item in items {
                let next = samples_of(item, groups);
                if next.is_empty() {
                    continue;
                }
                let mut combined = Vec::new();
                for a in &acc {
                    for b in &next {
                        if combined.len() >= SAMPLE_LIMIT {
                            break;
                        }
                        combined.push(format!("{}{}", a, b));
                    }
                }
                acc = combined;
            }
            acc
        }
        Node::Alt(branches) => branches.iter().flat_map(|b| samples_of(b, groups)).collect(),
        Node::Repeat { node, min, max, .. } => {
            let inner = samples_of(node, groups);
            let mut out = Vec::new();
            let hi = max.unwrap_or(min + 3).min(min + 3);
            for n in *min..=hi {
                if n == 0 {
                    out.push(String::new());
                    continue;
                }
                for s in &inner {
                    out.push(s.repeat(n as usize));
                }
                if inner.len() > 1 && n > 1 {
                    // Mix the first two options too.
                    let mut mixed = inner[0].repeat(n as usize - 1);
                    mixed.push_str(&inner[1]);
                    out.push(mixed);
                }
            }
            out
        }
        Node::Conditional { yes, no, .. } => {
            let mut v = samples_of(yes, groups);
            if let Some(no) = no {
                v.extend(samples_of(no, groups));
            }
            v
        }
        _ => vec![String::new()],
    };
    out.truncate(SAMPLE_LIMIT);
    out
}

impl Ast {
    /// Strings that (likely) match the pattern, for detecting collisions between terminals.
    pub(crate) fn samples(&self) -> Vec<String> {
        let mut groups = Vec::new();
        let mut v = samples_of(&self.node, &mut groups);
        let mut seen = std::collections::HashSet::new();
        v.retain(|s| seen.insert(s.clone()));
        v
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn w(p: &str) -> (u128, u128) {
        width(p, Flags::default()).unwrap()
    }

    #[test]
    fn widths_match_python() {
        assert_eq!(w("abc"), (3, 3));
        assert_eq!(w("a*"), (0, MAX_WIDTH));
        assert_eq!(w("a{2,5}"), (2, 5));
        assert_eq!(w("(a|bc)d?"), (1, 3));
        assert_eq!(w("(?=x)a"), (1, 1));
        assert_eq!(w(r"(a)\1"), (2, 2));
        assert_eq!(w(r"(?:)*"), (0, 0));
        assert_eq!(w(r"\d+(\.\d+)?"), (1, MAX_WIDTH));
    }

    #[test]
    fn matches_like_python() {
        let re = Regex::new(r"[^\W\d]\w*", Flags::default()).unwrap();
        assert_eq!(re.match_at("  héllo1 ", 2), Some(9));
        let re = Regex::new(r"a$", Flags::default()).unwrap();
        assert_eq!(re.match_at("a\n", 0), Some(1));
        assert_eq!(re.match_at("a\nb", 0), None);
        let re = Regex::new(r"(?i)select", Flags::default()).unwrap();
        assert_eq!(re.match_at("SeLeCt", 0), Some(6));
        let re = Regex::new(r"(?<!\\)(\\\\)*?", Flags::default()).unwrap();
        assert_eq!(re.match_at("ab", 1), Some(1));
    }

    #[test]
    fn rejects_what_python_rejects() {
        for p in ["a**", "(", "[a", "*a", r"\q", "a{3,2}", "(?<=a+)b", "x(?i)y"] {
            assert!(Ast::parse(p, Flags::default()).is_err(), "{}", p);
        }
        for p in ["a{", "{", "a{,3}", "[]a]", "[a-]", r"[\]]", "(?i:a)", r"\0", r"\101"] {
            assert!(Ast::parse(p, Flags::default()).is_ok(), "{}", p);
        }
    }
}
