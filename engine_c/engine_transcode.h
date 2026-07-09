#pragma once
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

// Initializes the transcoder lookup tables (must be called once)
void engine_transcoder_init(void);

// Transcodes UASTC blocks to ASTC 4x4 blocks.
// block_count: Number of 16-byte blocks.
// pUastc_blocks: Pointer to block_count * 16 bytes of UASTC data.
// pAstc_blocks: Pointer to block_count * 16 bytes of output ASTC data.
bool engine_transcode_uastc_to_astc(const void* pUastc_blocks, void* pAstc_blocks, uint32_t block_count);

#ifdef __cplusplus
}
#endif
