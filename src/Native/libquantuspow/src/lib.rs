//! C ABI over pinned upstream Poseidon2. Header layout follows qp-header at
//! chain 482c5b9e02bec0adc70797eade0a67f60baf2619, NOT generic SCALE header hashing.
use qp_poseidon_core::{Goldilocks, hash_squeeze_twice, hash_to_bytes};
use qp_poseidon_core::serialization::{bytes_to_digest_lossy, bytes_to_felts};

#[no_mangle]
pub extern "C" fn quantus_pow_abi_version() -> u32 { 1 }

/// Caller supplies 32, 64 and 64 accessible bytes respectively.
#[no_mangle]
pub unsafe extern "C" fn quantus_qpow_hash(block: *const u8, nonce: *const u8, output: *mut u8) -> i32 {
    if block.is_null() || nonce.is_null() || output.is_null() { return -1; }
    let mut input = [0u8;96];
    input[..32].copy_from_slice(std::slice::from_raw_parts(block,32));
    input[32..].copy_from_slice(std::slice::from_raw_parts(nonce,64));
    let hash = hash_squeeze_twice(&input);
    std::ptr::copy_nonoverlapping(hash.as_ptr(),output,64);
    0
}

/// Fields: parent || state || extrinsics || zk_root (128 bytes).
/// Digest is the complete SCALE Vec<DigestItem> encoding, including count.
#[no_mangle]
pub unsafe extern "C" fn quantus_header_hash(fields: *const u8, number: u32,
    digest: *const u8, digest_len: u32, output: *mut u8) -> i32 {
    if fields.is_null() || digest.is_null() || output.is_null() || digest_len > 111 { return -1; }
    let fields = std::slice::from_raw_parts(fields,128);
    let mut felts = Vec::with_capacity(45);
    felts.extend(bytes_to_digest_lossy(fields[..32].try_into().unwrap()));
    felts.push(Goldilocks::new(number as u64));
    for field in fields[32..].chunks_exact(32) {
        felts.extend(bytes_to_digest_lossy(field.try_into().unwrap()));
    }
    let mut padded = [0u8;110];
    let len = (digest_len as usize).min(110);
    padded[..len].copy_from_slice(std::slice::from_raw_parts(digest,len));
    felts.extend(bytes_to_felts(&padded));
    let hash = hash_to_bytes(&felts);
    std::ptr::copy_nonoverlapping(hash.as_ptr(),output,32);
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ffi_matches_upstream_for_distinct_inputs() {
        for seed in 0..64u8 {
            let input:Vec<u8>=(0..96).map(|i|(i as u8).wrapping_mul(17).wrapping_add(seed)).collect();
            let mut output=[0u8;64];
            unsafe { assert_eq!(quantus_qpow_hash(input.as_ptr(),input[32..].as_ptr(),output.as_mut_ptr()),0); }
            assert_eq!(output,hash_squeeze_twice(&input));
        }
    }
    #[test]
    fn nulls_are_rejected() {
        unsafe { assert_eq!(quantus_qpow_hash(std::ptr::null(),std::ptr::null(),std::ptr::null_mut()),-1); }
    }

    #[test]
    fn header_matches_explicit_consensus_layout() {
        // Independent layout: parent[4], number, three roots[12], digest[28].
        // Include noncanonical root limbs to verify the upstream reduction rule.
        let fields = [255u8;128];
        let number = 1025u32;
        let digest: Vec<u8> = (0..111).map(|i| i as u8).collect();
        let mut expected = Vec::new();
        for _ in 0..4 { expected.push(Goldilocks::from_u64(u64::MAX)); }
        expected.push(Goldilocks::new(number as u64));
        for _ in 0..12 { expected.push(Goldilocks::from_u64(u64::MAX)); }
        let mut window = digest[..110].to_vec();
        window.push(1); // Injective terminator after the fixed 110-byte window.
        window.push(0);
        for chunk in window.chunks_exact(4) {
            expected.push(Goldilocks::new(u32::from_le_bytes(chunk.try_into().unwrap()) as u64));
        }
        assert_eq!(expected.len(),45);
        let mut output=[0u8;32];
        unsafe {
            assert_eq!(quantus_header_hash(fields.as_ptr(),number,digest.as_ptr(),111,output.as_mut_ptr()),0);
            assert_eq!(output,hash_to_bytes(&expected));
            assert_eq!(quantus_header_hash(fields.as_ptr(),number,digest.as_ptr(),112,output.as_mut_ptr()),-1);
        }
    }
}
