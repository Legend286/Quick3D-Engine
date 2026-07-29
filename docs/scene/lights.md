# Scene Lights

**Purpose**: Describes how authored scene lights are represented in JSON, how
the editor creates them in the ECS world, and how the renderer turns them into
the shared GPU light buffer used by both raster and path tracing.

## Public API Surface
- `Engine.Scene.LightNode`: Scene-JSON light record with `directional`,
  `point`, and `spot` variants.
- `Engine.RHI.DirectionalLightComponent`: ECS component used for authored
  directional lights in the editor world.
- `Engine.RHI.PointLightComponent`: ECS component for point lights with
  range, source radius, and shadow toggle.
- `Engine.RHI.SpotLightComponent`: ECS component for spot lights with
  direction, cone angles, source radius, and shadow toggle.
- `Engine.Scene.LightMath`: Shared light-direction helpers for transform-driven
  spot lights.
- `Engine.Editor.ViewModels.ViewportPanelViewModel.AddPointLight()`: Creates a
  default point light in the current scene.
- `Engine.Editor.ViewModels.ViewportPanelViewModel.AddSpotLight()`: Creates a
  default spot light in the current scene.

## Usage Example
```csharp
var lightEnt = viewportVm.AddSpotLight();
if (lightEnt != 0)
{
    inspectorVm.SetSelectedEntity(lightEnt);
}
```

## Performance Characteristics
- Light authoring is editor-only and does not affect frame cost directly.
- The renderer now treats ECS light components plus `Transform` as the live
  authoring state, so moving a point or spot light updates both raster and path
  tracing without waiting for scene serialization.
- Spot lights use the entity rotation to define their world-space direction,
  with the local light axis fixed to negative Y. Inspector direction edits are
  converted back into that transform rotation so gizmos, saves, and rendering
  stay aligned.
- Scenes without authored lights now spawn a default directional-light entity
  in the ECS world, so the fallback sun is visible in the hierarchy and
  editable from the inspector instead of existing only as a shader fallback.
- The path tracer now uses `source_radius` for point and spot lights when
  building direct-light shadow rays, so punctual lights can produce finite-size
  penumbra instead of always behaving like infinitesimal emitters.
- `source_radius` and `cast_shadows` are stored now so future solid-angle shadow
  work can reuse the same scene schema instead of adding another migration.

## Cross-References
- [PBR Pipeline](pbr-pipeline.md)
- [Render Graph](../renderer/render-graph.md)
- [Raster Rendering Plan](../renderer/raster-rendering-plan.md)
- [engine-spec.md](../../engine-spec.md#4-asset-pipeline)
