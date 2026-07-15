/// Entity handles serialized in CS2 demo network data use 14 entry-index bits.
///
/// This is intentionally distinct from the native runtime `CEntityHandle`
/// representation used by server plugins.
pub(crate) const DEMO_NETWORK_EHANDLE_INDEX_MASK: u32 = 0x3FFF;
pub(crate) const DEMO_NETWORK_EHANDLE_INVALID_INDEX: i32 = DEMO_NETWORK_EHANDLE_INDEX_MASK as i32;

#[inline]
pub(crate) const fn demo_network_ehandle_index(handle: u32) -> i32 {
    (handle & DEMO_NETWORK_EHANDLE_INDEX_MASK) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preserves_demo_entity_indices_above_the_legacy_11_bit_limit() {
        let entity_index = 3_164_u32;
        let handle = (920 << 14) | entity_index;

        assert_eq!(demo_network_ehandle_index(handle), entity_index as i32);
        assert_ne!(demo_network_ehandle_index(handle), (handle & 0x7FF) as i32);
    }

    #[test]
    fn strips_bit_14_as_serial_data_instead_of_treating_it_as_an_index_bit() {
        let entity_index = 196_u32;
        let handle = 0x4000 | entity_index;

        assert_eq!(demo_network_ehandle_index(handle), entity_index as i32);
        assert_ne!(demo_network_ehandle_index(handle), (handle & 0x7FFF) as i32);
    }

    #[test]
    fn preserves_the_highest_valid_index_and_decodes_the_invalid_handle() {
        assert_eq!(demo_network_ehandle_index(0x3FFE), 0x3FFE);
        assert_eq!(demo_network_ehandle_index(0x00FF_FFFF), DEMO_NETWORK_EHANDLE_INVALID_INDEX);
    }
}
