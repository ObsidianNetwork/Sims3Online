#![forbid(unsafe_code)]
//! Codec errors for the public line protocol v1 (card 57 §C-§F).
//!
//! These are the front door's own decode failures. They are distinct from the refusal
//! codes carried on the wire ([`crate::messages::RefusalCode`]): a codec error means the
//! line is not well-formed v1, and the front door answers it with a named refusal
//! (`bad_line`, `line_too_long`, `bulk_malformed`), never a bare reset (card 57 §C.2, §F.2).

/// Why a v1 line could not be decoded, or why a value could not be encoded onto one.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WireError {
    /// The bytes are not valid UTF-8 (§C.1.1).
    NotUtf8,
    /// A line did not end in LF (§C.1.1).
    MissingLineFeed,
    /// A field contained `|`, CR or LF (§C.1.1).
    FieldSeparator,
    /// The line, LF excluded, is longer than `PUBLIC_LINE_MAX_BYTES` (§C.1.2).
    LineTooLong {
        /// The offending length in bytes.
        len: usize,
    },
    /// A lowercase-hex scalar field had an odd length or a non-hex byte (§C.1.3).
    BadHex,
    /// A decimal integer field did not parse into its width (§C.1.3).
    BadInt,
    /// A boolean field was neither `0` nor `1` (§C.1.3).
    BadBool,
    /// An identifier (`Verb`, `Kind`, or a refusal `<code>`) is not `[A-Za-z][A-Za-z0-9_]*`
    /// or, for a code, longer than 64 bytes (§D.1, §D.2).
    BadIdentifier,
    /// A line had too few fields for its frame (§D.1, §D.2, §F).
    TooFewFields {
        /// The frame's short name, e.g. `CMD`.
        frame: &'static str,
        /// How many fields were present.
        got: usize,
        /// The minimum the frame needs.
        min: usize,
    },
    /// A composite (record `(...)` or list `[...]`) was not well-formed (§C.1.4).
    BadComposite,
    /// A `BULK` chunk was rejected: a gap, a repeat, a changed `count` or an unknown
    /// `bulk_id`, or the base64 was malformed (§F.2).
    BulkMalformed,
}

impl core::fmt::Display for WireError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::NotUtf8 => f.write_str("line is not valid UTF-8"),
            Self::MissingLineFeed => f.write_str("line does not end in LF"),
            Self::FieldSeparator => f.write_str("a field contains '|', CR or LF"),
            Self::LineTooLong { len } => {
                write!(f, "line of {len} bytes exceeds the public line cap")
            }
            Self::BadHex => f.write_str("hex scalar is not lowercase hex of even length"),
            Self::BadInt => f.write_str("integer field is not decimal of the right width"),
            Self::BadBool => f.write_str("boolean field is not 0 or 1"),
            Self::BadIdentifier => f.write_str("identifier is not a PascalCase-ish token"),
            Self::TooFewFields { frame, got, min } => {
                write!(f, "{frame} frame has {got} fields, needs at least {min}")
            }
            Self::BadComposite => f.write_str("composite field is not a well-formed record or list"),
            Self::BulkMalformed => f.write_str("bulk chunk is malformed"),
        }
    }
}

impl std::error::Error for WireError {}
