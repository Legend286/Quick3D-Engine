/* SPDX-License-Identifier: MIT */
#include "engine_ecs.h"
#include <flecs.h>
#include <string.h>
#include <stdbool.h>

ENGINE_API ecs_world_t* engine_ecs_init(void) {
    return ecs_init();
}

ENGINE_API void engine_ecs_shutdown(ecs_world_t* world) {
    if (world) {
        ecs_fini(world);
    }
}

ENGINE_API ecs_entity_t engine_ecs_create_entity(ecs_world_t* world) {
    if (!world) return 0;
    return ecs_new(world);
}

ENGINE_API ecs_entity_t engine_ecs_register_component(ecs_world_t* world, const char* name, size_t size, size_t alignment) {
    if (!world || !name) return 0;
    
    // Look up or create the entity for the component name
    ecs_entity_t ent = ecs_entity_init(world, &(ecs_entity_desc_t){
        .name = name
    });
    
    // Initialize the component
    return ecs_component_init(world, &(ecs_component_desc_t){
        .entity = ent,
        .type = {
            .size = size,
            .alignment = alignment
        }
    });
}

ENGINE_API void engine_ecs_set_component(ecs_world_t* world, ecs_entity_t entity, ecs_entity_t component_id, const void* data, size_t size) {
    if (!world || !entity || !component_id || !data) return;
    ecs_set_id(world, entity, component_id, size, data);
}

ENGINE_API int32_t engine_ecs_get_component(ecs_world_t* world, ecs_entity_t entity, ecs_entity_t component_id, void* out_data, size_t size) {
    if (!world || !entity || !component_id || !out_data) return 0;
    const void* ptr = ecs_get_id(world, entity, component_id);
    if (ptr) {
        memcpy(out_data, ptr, size);
        return 1;
    }
    return 0;
}
