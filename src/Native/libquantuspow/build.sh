#!/usr/bin/env bash
set -euo pipefail
task_root="$(cd -- "$(dirname -- "$0")" && pwd)"
task_output="${1:?Usage: build.sh OUTPUT_DIRECTORY}"
mkdir -p -- "$task_output"
task_output="$(cd -- "$task_output" && pwd)"
command -v cargo >/dev/null || { echo 'Quantus requires Rust cargo (tested with Rust 1.90). Install Rust and rebuild.' >&2; exit 1; }
cargo build --manifest-path "$task_root/Cargo.toml" --target-dir "$task_root/target" --release --locked --offline
case "$(uname -s)" in
  Linux) cp -- "$task_root/target/release/libquantuspow.so" "$task_output/" ;;
  Darwin) cp -- "$task_root/target/release/libquantuspow.dylib" "$task_output/" ;;
  *) echo 'Use cargo build --release on Windows and copy quantuspow.dll into the publish directory.' >&2; exit 1 ;;
esac
