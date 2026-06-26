/* SPDX-License-Identifier: MIT */
#ifndef ENGINE_ECS_H
#define ENGINE_ECS_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef ENGINE_API
#  ifdef _WIN32
#    define ENGINE_API __declspec(dllimport)
#  else
#    define ENGINE_API __attribute__((visibility("default")))
#  endif
#endif

typedef struct ecs_world_t ecs_world_t;
typedef uint64_t ecs_entity_t;

ENGINE_API ecs_world_t* engine_ecs_init(void);
ENGINE_API void          engine_ecs_shutdown(ecs_world_t* world);
ENGINE_API ecs_entity_t engine_ecs_create_entity(ecs_world_t* world);
ENGINE_API ecs_entity_t engine_ecs_register_component(ecs_world_t* world, const char* name, size_t size, size_t alignment);
ENGINE_API void          engine_ecs_set_component(ecs_world_t* world, ecs_entity_t entity, ecs_entity_t component_id, const void* data, size_t size);
ENGINE_API int32_t       engine_ecs_get_component(ecs_world_t* world, ecs_entity_t entity, ecs_entity_t component_id, void* out_data, size_t size);

#ifdef __cplusplus
}
#endif

#endif /* ENGINE_ECS_H */
