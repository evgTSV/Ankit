//! Generates the C header of `ankit_rslib` from the `extern "C"` items of
//! `src/lib.rs`, with the settings in `cbindgen.toml`.
//!
//! Usage: `cargo run -p header-gen -- <output.h>`. The plain cbindgen CLI
//! gives the same result: `cbindgen --config cbindgen.toml --output <output.h>`.

use std::env;
use std::path::Path;

use anyhow::Context;
use anyhow::Result;

fn main() -> Result<()> {
    let output = env::args()
        .nth(1)
        .context("usage: header-gen <output.h>")?;
    // The ankit-rslib crate is the parent of this one
    let crate_dir = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .context("header-gen has no parent directory")?;
    let config = cbindgen::Config::from_file(crate_dir.join("cbindgen.toml"))
        .map_err(anyhow::Error::msg)?;

    let written = cbindgen::generate_with_config(crate_dir, config)?.write_to_file(&output);
    if written {
        println!("Wrote {output}");
    }
    Ok(())
}
