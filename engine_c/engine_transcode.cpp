#include "engine_transcode.h"
#include "transcoder/basisu_transcoder.h"
#include "transcoder/basisu_transcoder_uastc.h"

void engine_transcoder_init(void) {
    basist::basisu_transcoder_init();
}

bool engine_transcode_uastc_to_astc(const void* pUastc_blocks, void* pAstc_blocks, uint32_t block_count) {
    if (!pUastc_blocks || !pAstc_blocks) return false;
    
    const basist::uastc_block* src = (const basist::uastc_block*)pUastc_blocks;
    uint8_t* dst = (uint8_t*)pAstc_blocks;
    
    for (uint32_t i = 0; i < block_count; i++) {
        if (!basist::transcode_uastc_to_astc(src[i], dst)) {
            return false;
        }
        dst += 16;
    }
    
    return true;
}
