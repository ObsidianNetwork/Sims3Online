//! `BridgeCrc32` (IEEE 802.3, reflected 0xEDB88320), the bulk-slot checksum
//! (`DedicatedServer/HostBridge/BridgeCrc32.cs`, linked into the mod as `SimsOnlineServerHostMod/BridgeCrc32.cs`).
#![forbid(unsafe_code)]

const fn build_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    let mut i = 0;
    while i < 256 {
        let mut entry = i as u32;
        let mut j = 0;
        while j < 8 {
            entry = if entry & 1 != 0 { (entry >> 1) ^ 0xEDB8_8320 } else { entry >> 1 };
            j += 1;
        }
        table[i] = entry;
        i += 1;
    }
    table
}

static TABLE: [u32; 256] = build_table();

pub fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for b in data {
        crc = (crc >> 8) ^ TABLE[((crc ^ u32::from(*b)) & 0xFF) as usize];
    }
    crc ^ 0xFFFF_FFFF
}

#[cfg(test)]
mod tests {
    use super::crc32;

    #[test]
    fn standard_check_values() {
        assert_eq!(crc32(b""), 0);
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }
}
