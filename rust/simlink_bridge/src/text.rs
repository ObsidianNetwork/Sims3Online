//! Field-level encodings the bridge lines use, matched to the oracle's .NET behaviour: the hex free-text
//! form (`BridgeLineCodec.Text`/`DecodeText`), GUIDs (`Guid.ToString("N")`, `Guid.Parse`,
//! `Guid.ToByteArray`), invariant-culture number parsing (`NumberStyles.Integer`, `NumberStyles.Float |
//! AllowThousands`) and formatting (`float.ToString("R")`, `"F2"`). The gate-4 differential
//! (`fixtures/oracle_float_lines.txt`) pins the formatting against .NET itself.
#![forbid(unsafe_code)]

use core::fmt;

use crate::layout::MAX_LINE_BYTES;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TextError {
    OddHexLength,
    InvalidHexChar,
    /// Over `MAX_LINE_BYTES * 2` hex characters (the kernel's guard, `runtime_bridge.rs:750`).
    TooLong,
    InvalidNumber,
    InvalidGuid,
}

impl fmt::Display for TextError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::OddHexLength => "encoded bridge text has an odd number of hex characters",
            Self::InvalidHexChar => "invalid hex character in bridge text",
            Self::TooLong => "encoded bridge text is longer than one control frame allows",
            Self::InvalidNumber => "invalid number",
            Self::InvalidGuid => "invalid guid",
        })
    }
}

impl std::error::Error for TextError {}

// ---------------------------------------------------------------------------------------------
// Hex free text
// ---------------------------------------------------------------------------------------------

/// `BridgeLineCodec.Text`: UTF-8 bytes as upper-case hex.
pub fn encode_text(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut out = String::with_capacity(value.len() * 2);
    for b in value.bytes() {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0F) as usize] as char);
    }
    out
}

fn hex_value(c: u8) -> Result<u8, TextError> {
    match c {
        b'0'..=b'9' => Ok(c - b'0'),
        b'A'..=b'F' => Ok(c - b'A' + 10),
        b'a'..=b'f' => Ok(c - b'a' + 10),
        _ => Err(TextError::InvalidHexChar),
    }
}

/// Hex to bytes, both cases accepted, odd length rejected.
pub fn decode_hex_bytes(value: &str) -> Result<Vec<u8>, TextError> {
    let b = value.as_bytes();
    if !b.len().is_multiple_of(2) {
        return Err(TextError::OddHexLength);
    }
    b.chunks(2).map(|p| Ok((hex_value(p[0])? << 4) | hex_value(p[1])?)).collect()
}

/// `BridgeLineCodec.DecodeText`: hex to UTF-8 text, invalid sequences replaced (as `Encoding.UTF8.GetString`).
pub fn decode_text(value: &str) -> Result<String, TextError> {
    Ok(String::from_utf8_lossy(&decode_hex_bytes(value)?).into_owned())
}

/// The free-text argument form with the kernel's guard (`rust/sim96_kernel_cleanroom/src/runtime_bridge.rs:750`):
/// an odd hex length, or one over `MAX_LINE_BYTES * 2`, is rejected. Any argument that fits a control
/// frame is under the bound, so the guard never refuses what [`encode_text`] puts in a frame.
pub fn decode_text_arg(value: &str) -> Result<String, TextError> {
    if !value.len().is_multiple_of(2) {
        return Err(TextError::OddHexLength);
    }
    if value.len() > MAX_LINE_BYTES * 2 {
        return Err(TextError::TooLong);
    }
    decode_text(value)
}

/// The encoder side of the same guard: the hex of `value`, or `TooLong` when it could not ride one frame.
pub fn encode_text_arg(value: &str) -> Result<String, TextError> {
    if value.len() > MAX_LINE_BYTES {
        return Err(TextError::TooLong);
    }
    Ok(encode_text(value))
}

// ---------------------------------------------------------------------------------------------
// GUIDs
// ---------------------------------------------------------------------------------------------

/// A GUID held in its textual byte order (the 32 hex digits of the "N" form, left to right).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Guid(pub [u8; 16]);

impl Guid {
    pub const EMPTY: Guid = Guid([0u8; 16]);

    /// `BridgeLineCodec.TryParseWireCorrelationId`: exactly 32 hex digits.
    pub fn parse_wire(raw: &str) -> Option<Guid> {
        if raw.len() != 32 {
            return None;
        }
        let bytes = decode_hex_bytes(raw).ok()?;
        let mut g = [0u8; 16];
        g.copy_from_slice(&bytes);
        Some(Guid(g))
    }

    /// `Guid.Parse`: the N, D, B and P forms, surrounding white space trimmed. (The X form is not accepted;
    /// no bridge line carries it.)
    pub fn parse(raw: &str) -> Result<Guid, TextError> {
        let s = trim_dotnet(raw);
        let inner = if (s.starts_with('{') && s.ends_with('}')) || (s.starts_with('(') && s.ends_with(')')) {
            if s.len() != 38 {
                return Err(TextError::InvalidGuid);
            }
            &s[1..37]
        } else {
            s
        };
        let digits: String = match inner.len() {
            // The N form never has braces.
            32 if inner.len() == s.len() => inner.to_string(),
            36 => {
                let b = inner.as_bytes();
                if b[8] != b'-' || b[13] != b'-' || b[18] != b'-' || b[23] != b'-' {
                    return Err(TextError::InvalidGuid);
                }
                inner.chars().filter(|c| *c != '-').collect()
            }
            _ => return Err(TextError::InvalidGuid),
        };
        Guid::parse_wire(&digits).ok_or(TextError::InvalidGuid)
    }

    /// `Guid.ToString("N")`: lower-case hex.
    pub fn to_n(&self) -> String {
        encode_text_bytes_lower(&self.0)
    }

    /// `Guid.ToByteArray`: Data1 (4 bytes), Data2 and Data3 (2 each) little-endian, then the last 8 as written.
    pub fn to_dotnet_bytes(&self) -> [u8; 16] {
        let g = &self.0;
        [g[3], g[2], g[1], g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]]
    }

    /// The inverse of [`Guid::to_dotnet_bytes`] (`new Guid(byte[])`).
    pub fn from_dotnet_bytes(b: &[u8; 16]) -> Guid {
        Guid([b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]])
    }
}

fn encode_text_bytes_lower(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0F) as usize] as char);
    }
    out
}

// ---------------------------------------------------------------------------------------------
// Invariant-culture number parsing
// ---------------------------------------------------------------------------------------------

/// .NET's `AllowLeadingWhite`/`AllowTrailingWhite` set: U+0009..U+000D and U+0020.
fn is_dotnet_white(c: char) -> bool {
    matches!(c, '\u{09}'..='\u{0D}' | ' ')
}

fn trim_dotnet(s: &str) -> &str {
    s.trim_matches(is_dotnet_white)
}

/// Sign and digits of a `NumberStyles.Integer` field: white space, one optional sign, at least one digit.
fn split_integer(s: &str) -> Result<(bool, &str), TextError> {
    let s = trim_dotnet(s);
    let (neg, digits) = match s.as_bytes().first() {
        Some(b'-') => (true, &s[1..]),
        Some(b'+') => (false, &s[1..]),
        _ => (false, s),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return Err(TextError::InvalidNumber);
    }
    Ok((neg, digits))
}

/// `ulong.Parse(s, CultureInfo.InvariantCulture)`: a negative sign is accepted only for zero.
pub fn parse_u64(s: &str) -> Result<u64, TextError> {
    let (neg, digits) = split_integer(s)?;
    let v: u64 = digits.parse().map_err(|_| TextError::InvalidNumber)?;
    if neg && v != 0 {
        return Err(TextError::InvalidNumber);
    }
    Ok(v)
}

/// `long.Parse(s, CultureInfo.InvariantCulture)`.
pub fn parse_i64(s: &str) -> Result<i64, TextError> {
    let (neg, digits) = split_integer(s)?;
    let magnitude: u64 = digits.parse().map_err(|_| TextError::InvalidNumber)?;
    if neg {
        if magnitude == 1u64 << 63 {
            return Ok(i64::MIN);
        }
        i64::try_from(magnitude).map(|m| -m).map_err(|_| TextError::InvalidNumber)
    } else {
        i64::try_from(magnitude).map_err(|_| TextError::InvalidNumber)
    }
}

/// `int.Parse(s, CultureInfo.InvariantCulture)`.
pub fn parse_i32(s: &str) -> Result<i32, TextError> {
    i32::try_from(parse_i64(s)?).map_err(|_| TextError::InvalidNumber)
}

/// `uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture)`: white space and hex digits, no sign.
pub fn parse_hex_u32(s: &str) -> Result<u32, TextError> {
    let s = trim_dotnet(s);
    if s.is_empty() || !s.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(TextError::InvalidNumber);
    }
    let trimmed = s.trim_start_matches('0');
    if trimmed.len() > 8 {
        return Err(TextError::InvalidNumber);
    }
    if trimmed.is_empty() {
        return Ok(0);
    }
    u32::from_str_radix(trimmed, 16).map_err(|_| TextError::InvalidNumber)
}

/// `float.Parse(s, CultureInfo.InvariantCulture)` (`NumberStyles.Float | AllowThousands`): white space, a
/// sign, digits with ',' group separators before the point, a fraction, an exponent; `NaN`, `Infinity`,
/// `-Infinity` and the infinity sign. Rounding is IEEE round-to-nearest, as .NET Core 3.0+ does.
pub fn parse_f32(s: &str) -> Result<f32, TextError> {
    let t = trim_dotnet(s);
    let (neg, body) = match t.as_bytes().first() {
        Some(b'-') => (true, &t[1..]),
        Some(b'+') => (false, &t[1..]),
        _ => (false, t),
    };
    let signed = |v: f32| if neg { -v } else { v };
    if body.eq_ignore_ascii_case("infinity") || body == "\u{221E}" {
        return Ok(signed(f32::INFINITY));
    }
    if body.eq_ignore_ascii_case("nan") {
        return Ok(f32::NAN);
    }
    // Integral part (digits and group separators), optional fraction, optional exponent.
    let bytes = body.as_bytes();
    let mut i = 0;
    let mut normal = String::with_capacity(body.len() + 1);
    let mut int_digits = 0;
    while i < bytes.len() && (bytes[i].is_ascii_digit() || (bytes[i] == b',' && int_digits > 0)) {
        if bytes[i] != b',' {
            normal.push(bytes[i] as char);
            int_digits += 1;
        }
        i += 1;
    }
    let mut frac_digits = 0;
    if i < bytes.len() && bytes[i] == b'.' {
        normal.push('.');
        i += 1;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            normal.push(bytes[i] as char);
            frac_digits += 1;
            i += 1;
        }
    }
    if int_digits + frac_digits == 0 {
        return Err(TextError::InvalidNumber);
    }
    if i < bytes.len() && (bytes[i] == b'e' || bytes[i] == b'E') {
        let mut j = i + 1;
        let mut exp = String::from("e");
        if j < bytes.len() && (bytes[j] == b'+' || bytes[j] == b'-') {
            exp.push(bytes[j] as char);
            j += 1;
        }
        let start = j;
        while j < bytes.len() && bytes[j].is_ascii_digit() {
            exp.push(bytes[j] as char);
            j += 1;
        }
        if j == start {
            return Err(TextError::InvalidNumber);
        }
        normal.push_str(&exp);
        i = j;
    }
    if i != bytes.len() {
        return Err(TextError::InvalidNumber);
    }
    let v: f32 = normal.parse().map_err(|_| TextError::InvalidNumber)?;
    Ok(signed(v))
}

// ---------------------------------------------------------------------------------------------
// Invariant-culture float formatting
// ---------------------------------------------------------------------------------------------

/// .NET's `FormatGeneral` scientific threshold for a float's shortest form (`'R'` and default `ToString`).
const SINGLE_GENERAL_DIGITS: i32 = 9;

/// Shortest round-trip digits of `v` (finite, non-zero) and its scale (value = 0.d1d2... x 10^scale).
fn shortest_digits(v: f32) -> (String, i32) {
    let s = format!("{:e}", v.abs());
    let (mantissa, exp) = s.split_once('e').expect("exponent form");
    let digits: String = mantissa.chars().filter(|c| *c != '.').collect();
    let exp: i32 = exp.parse().expect("exponent");
    let digits = digits.trim_end_matches('0').to_string();
    (if digits.is_empty() { "0".to_string() } else { digits }, exp + 1)
}

/// `float.ToString("R", CultureInfo.InvariantCulture)` on .NET Core 3.0+.
pub fn format_r(v: f32) -> String {
    if v.is_nan() {
        return "NaN".to_string();
    }
    if v.is_infinite() {
        return if v > 0.0 { "Infinity".to_string() } else { "-Infinity".to_string() };
    }
    let mut out = String::new();
    if v.is_sign_negative() {
        out.push('-');
    }
    if v == 0.0 {
        out.push('0');
        return out;
    }
    let (digits, scale) = shortest_digits(v);
    let d = digits.as_bytes();
    let scientific = !(-3..=SINGLE_GENERAL_DIGITS).contains(&scale);
    let mut pos = if scientific { 1 } else { scale };
    let mut k = 0;
    if pos > 0 {
        while pos > 0 {
            out.push(if k < d.len() { d[k] as char } else { '0' });
            k += 1;
            pos -= 1;
        }
    } else {
        out.push('0');
    }
    if k < d.len() || pos < 0 {
        out.push('.');
        while pos < 0 {
            out.push('0');
            pos += 1;
        }
        while k < d.len() {
            out.push(d[k] as char);
            k += 1;
        }
    }
    if scientific {
        let e = scale - 1;
        out.push('E');
        out.push(if e < 0 { '-' } else { '+' });
        out.push_str(&format!("{:02}", e.abs()));
    }
    out
}

/// `float.ToString("F2", CultureInfo.InvariantCulture)` on .NET Core 3.0+: the exact value rounded to two
/// decimals, an exact tie to the even digit (Dragon4), the sign kept for a negative value that rounds to zero.
pub fn format_f2(v: f32) -> String {
    if v.is_nan() {
        return "NaN".to_string();
    }
    if v.is_infinite() {
        return if v > 0.0 { "Infinity".to_string() } else { "-Infinity".to_string() };
    }
    // A float's exact decimal expansion has at most 149 fractional digits; f64 holds it exactly.
    let exact = format!("{:.160}", f64::from(v).abs());
    let (int_part, frac) = exact.split_once('.').expect("fixed form");
    let mut digits: Vec<u8> = int_part.bytes().chain(frac.bytes().take(2)).map(|b| b - b'0').collect();
    let rest = &frac.as_bytes()[2..];
    let first = rest.first().map_or(0, |b| b - b'0');
    let beyond = rest.iter().skip(1).any(|b| *b != b'0');
    let last = *digits.last().expect("digits");
    let round_up = first > 5 || (first == 5 && (beyond || last % 2 == 1));
    if round_up {
        let mut i = digits.len();
        loop {
            if i == 0 {
                digits.insert(0, 1);
                break;
            }
            i -= 1;
            if digits[i] == 9 {
                digits[i] = 0;
            } else {
                digits[i] += 1;
                break;
            }
        }
    }
    let n = digits.len();
    let mut out = String::new();
    if v.is_sign_negative() {
        out.push('-');
    }
    let int_digits: String = digits[..n - 2].iter().map(|d| (b'0' + d) as char).collect();
    let int_digits = int_digits.trim_start_matches('0');
    out.push_str(if int_digits.is_empty() { "0" } else { int_digits });
    out.push('.');
    out.extend(digits[n - 2..].iter().map(|d| (b'0' + d) as char));
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn text_round_trips_and_guards() {
        assert_eq!(encode_text("Terrain.GoHere"), "5465727261696E2E476F48657265");
        assert_eq!(decode_text("5465727261696e2E476F48657265").unwrap(), "Terrain.GoHere");
        assert_eq!(decode_text("ABC"), Err(TextError::OddHexLength));
        assert_eq!(decode_text("ZZ"), Err(TextError::InvalidHexChar));
        let long = "A".repeat(MAX_LINE_BYTES * 2 + 2);
        assert_eq!(decode_text_arg(&long), Err(TextError::TooLong));
        assert_eq!(decode_text_arg("414"), Err(TextError::OddHexLength));
        let fits = "x".repeat(MAX_LINE_BYTES);
        assert_eq!(decode_text_arg(&encode_text_arg(&fits).unwrap()).unwrap(), fits);
        assert_eq!(encode_text_arg(&"x".repeat(MAX_LINE_BYTES + 1)), Err(TextError::TooLong));
    }

    #[test]
    fn guid_forms() {
        let n = "0123456789abcdef0123456789ABCDEF";
        let g = Guid::parse_wire(n).unwrap();
        assert_eq!(g.to_n(), n.to_lowercase());
        assert_eq!(Guid::parse("01234567-89ab-cdef-0123-456789abcdef").unwrap(), g);
        assert_eq!(Guid::parse("{01234567-89ab-cdef-0123-456789abcdef}").unwrap(), g);
        assert!(Guid::parse_wire("0123").is_none());
        assert_eq!(Guid::from_dotnet_bytes(&g.to_dotnet_bytes()), g);
        assert_eq!(&g.to_dotnet_bytes()[..4], &[0x67, 0x45, 0x23, 0x01]);
    }

    #[test]
    fn integer_parsing_follows_number_styles_integer() {
        assert_eq!(parse_u64(" 42 "), Ok(42));
        assert_eq!(parse_u64("+7"), Ok(7));
        assert_eq!(parse_u64("-0"), Ok(0));
        assert!(parse_u64("-1").is_err());
        assert!(parse_u64("18446744073709551616").is_err());
        assert_eq!(parse_i64("-9223372036854775808"), Ok(i64::MIN));
        assert!(parse_i32("2147483648").is_err());
        assert_eq!(parse_hex_u32("00000000DEADBEEF"), Ok(0xDEAD_BEEF));
        assert!(parse_hex_u32("1DEADBEEF").is_err());
    }

    #[test]
    fn float_parsing() {
        assert_eq!(parse_f32("1,000.5"), Ok(1000.5));
        assert_eq!(parse_f32(" -2.5e3 "), Ok(-2500.0));
        assert_eq!(parse_f32(".5"), Ok(0.5));
        assert!(parse_f32("Infinity").unwrap().is_infinite());
        assert!(parse_f32("nan").unwrap().is_nan());
        assert!(parse_f32("1e").is_err());
        assert!(parse_f32(",1").is_err());
    }

    #[test]
    fn float_formatting_shapes() {
        assert_eq!(format_r(0.0), "0");
        assert_eq!(format_r(-0.0), "-0");
        assert_eq!(format_r(1.5), "1.5");
        assert_eq!(format_r(0.1), "0.1");
        assert_eq!(format_r(0.0001), "0.0001");
        assert_eq!(format_r(0.00001), "1E-05");
        assert_eq!(format_f2(0.125), "0.12");
        assert_eq!(format_f2(0.375), "0.38");
        assert_eq!(format_f2(-0.001), "-0.00");
        assert_eq!(format_f2(99.999), "100.00");
    }
}
