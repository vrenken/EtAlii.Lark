use std::fmt;

/// A piece of the text, produced by the lexer: Lark's `Token`.
///
/// Positions follow Rust conventions: `start_pos` and `end_pos` are byte offsets into the text, so
/// `&text[start_pos..end_pos]` is the matched text. `line` and `column` start at 1, and columns count characters
/// (Unicode scalar values), as Lark does.
///
/// Like Lark's tokens, two tokens are equal when their type and value are equal; positions are not compared.
#[derive(Clone, Default)]
pub struct Token {
    /// The name of the terminal (Lark's `token.type`).
    pub kind: String,
    /// The matched text.
    pub value: String,
    /// The byte offset of the token in the text.
    pub start_pos: Option<usize>,
    /// The line of the token (starting with 1).
    pub line: Option<usize>,
    /// The column of the token (starting with 1), in characters.
    pub column: Option<usize>,
    /// The line where the token ends.
    pub end_line: Option<usize>,
    /// The column after the end of the token.
    pub end_column: Option<usize>,
    /// The byte offset where the token ends.
    pub end_pos: Option<usize>,
}

impl Token {
    /// Creates a token without a position.
    pub fn new(kind: impl Into<String>, value: impl Into<String>) -> Token {
        Token {
            kind: kind.into(),
            value: value.into(),
            ..Default::default()
        }
    }

    /// Creates a token with the position of another token (Lark's `Token.new_borrow_pos`).
    pub fn new_borrow_pos(kind: impl Into<String>, value: impl Into<String>, borrow: &Token) -> Token {
        Token {
            kind: kind.into(),
            value: value.into(),
            start_pos: borrow.start_pos,
            line: borrow.line,
            column: borrow.column,
            end_line: borrow.end_line,
            end_column: borrow.end_column,
            end_pos: borrow.end_pos,
        }
    }

    /// Returns a copy with another type and/or value, at the same position (Lark's `Token.update`).
    pub fn update(&self, kind: Option<&str>, value: Option<&str>) -> Token {
        Token::new_borrow_pos(kind.unwrap_or(&self.kind), value.unwrap_or(&self.value), self)
    }

    /// The value of the token.
    pub fn as_str(&self) -> &str {
        &self.value
    }

    /// The length of the value, in bytes.
    pub fn len(&self) -> usize {
        self.value.len()
    }

    /// Whether the value is empty.
    pub fn is_empty(&self) -> bool {
        self.value.is_empty()
    }
}

impl PartialEq for Token {
    fn eq(&self, other: &Token) -> bool {
        self.kind == other.kind && self.value == other.value
    }
}

impl Eq for Token {}

impl std::hash::Hash for Token {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.value.hash(state);
    }
}

impl PartialEq<str> for Token {
    fn eq(&self, other: &str) -> bool {
        self.value == other
    }
}

impl PartialEq<&str> for Token {
    fn eq(&self, other: &&str) -> bool {
        self.value == *other
    }
}

impl AsRef<str> for Token {
    fn as_ref(&self) -> &str {
        &self.value
    }
}

impl fmt::Display for Token {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.value)
    }
}

impl fmt::Debug for Token {
    /// Lark's `repr`: `Token('NAME', 'value')`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Token({}, {})", crate::utils::repr(&self.kind), crate::utils::repr(&self.value))
    }
}
