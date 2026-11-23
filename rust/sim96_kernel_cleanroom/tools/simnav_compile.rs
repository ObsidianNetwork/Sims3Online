#![forbid(unsafe_code)]
//! `simnav_compile`: compile an authored canonical `.simnav.json` (card 51 §E)
//! into its `SIMNAV01` binary, validated by the kernel's own loader.
//!
//! Usage: `simnav_compile <in.simnav.json> <out.simnav>`. Prints the card 51
//! §D `content_hash`. Exit 2 names the loader's rejection; exit 1 is an IO or
//! usage error. Authored fixtures only: nothing here reads game files.

use std::process::ExitCode;

use sim96_kernel_cleanroom::routing_astar::SimNav;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();
    let [_, input, output] = args.as_slice() else {
        eprintln!("usage: simnav_compile <in.simnav.json> <out.simnav>");
        return ExitCode::from(1);
    };
    let text = match std::fs::read_to_string(input) {
        Ok(text) => text,
        Err(err) => { eprintln!("read {input}: {err}"); return ExitCode::from(1); }
    };
    let bytes = match SimNav::from_canonical_json(&text)
        .map(|nav| nav.to_bytes())
        .and_then(|bytes| SimNav::load(&bytes).map(|nav| (nav, bytes))) {
        Ok((nav, bytes)) => {
            println!("content_hash={:016x}", nav.header.content_hash);
            bytes
        }
        Err(err) => { eprintln!("rejected: {err:?}"); return ExitCode::from(2); }
    };
    if let Err(err) = std::fs::write(output, bytes) {
        eprintln!("write {output}: {err}");
        return ExitCode::from(1);
    }
    ExitCode::SUCCESS
}
