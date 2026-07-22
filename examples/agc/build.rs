//! Embedded-mode build (A6): with `--features embedded`, compile yaAGC's engine sources from
//! the Virtual AGC checkout named by `$VIRTUALAGC` (default `/opt/src/virtualagc`) together
//! with the C shim. Without the feature this script does nothing — plain builds never need
//! the checkout.

fn main() {
    println!("cargo:rerun-if-changed=csrc/vagc_shim.c");
    println!("cargo:rerun-if-env-changed=VIRTUALAGC");
    if std::env::var_os("CARGO_FEATURE_EMBEDDED").is_none() {
        return;
    }
    let tree = std::env::var("VIRTUALAGC").unwrap_or_else(|_| "/opt/src/virtualagc".into());
    let yaagc = std::path::Path::new(&tree).join("yaAGC");
    for f in ["agc_engine.c", "agc_engine_init.c"] {
        assert!(
            yaagc.join(f).exists(),
            "--features embedded needs a Virtual AGC checkout: {} not found (set VIRTUALAGC=)",
            yaagc.join(f).display()
        );
    }
    cc::Build::new()
        .file(yaagc.join("agc_engine.c"))
        .file(yaagc.join("agc_engine_init.c"))
        .file("csrc/vagc_shim.c")
        .include(&yaagc)
        .warnings(false)
        .compile("vagc");
}
