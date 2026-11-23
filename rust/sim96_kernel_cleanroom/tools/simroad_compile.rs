#![forbid(unsafe_code)]
//! Compile authored JSON only; this tool never reads game packages or saves.
use std::process::ExitCode;
use sim96_kernel_cleanroom::road_placement::compile_simroad_json;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();
    let [_, input, output] = args.as_slice() else {
        eprintln!("usage: simroad_compile <in.simroad.json> <out.simroad>");
        return ExitCode::from(1);
    };
    let text = match std::fs::read_to_string(input) {
        Ok(text) => text,
        Err(err) => { eprintln!("read {input}: {err}"); return ExitCode::from(1); }
    };
    let bytes = match compile_simroad_json(&text) {
        Ok(bytes) => bytes,
        Err(err) => { eprintln!("rejected: {err}"); return ExitCode::from(2); }
    };
    let hash = u64::from_le_bytes(bytes[48..56].try_into().unwrap());
    if let Err(err) = std::fs::write(output, bytes) {
        eprintln!("write {output}: {err}"); return ExitCode::from(1);
    }
    println!("content_hash={hash:016x}");
    ExitCode::SUCCESS
}
