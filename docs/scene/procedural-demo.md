# Procedural Demo Scene

## Purpose

The procedural demo scene provides a reproducible Forward+ and dynamic-shadow
stress workload without storing a large repeated model asset or scene file.

## Public API Surface

- `ProceduralDemoDefinition`: Scene JSON settings for procedural generation,
  punctual-light counts, and animation.
- `PrimitiveMeshFactory.GenerateUVSphere`: Writes a tessellated UV sphere.
- `PrimitiveMeshFactory.GenerateTorus`: Writes a tessellated torus.
- `PrimitiveMeshFactory.GenerateBox`: Writes a unit box with face normals.
- `PrimitiveMeshFactory.GeneratePlane`: Writes a subdivided plane.
- `OrbitingLightComponent`: Defines deterministic orbit animation for a light.
- `OscillatingModelComponent`: Defines deterministic motion for a dynamic
  shadow caster.
- `ProceduralDemoEntityComponent`: Prevents generated entities from expanding
  into authored model and light records when the compact scene is saved.

## Usage Example

Open `Content/scenes/shadow_stress.scene.json` through `File > Open Scene`.
The default descriptor creates 28 point lights, 8 spot lights, and one
directional light, all with shadows enabled. Eight point lights and four spot
lights move continuously; twelve box instances are dynamic shadow casters.
Point lights use intensities from 76 to 118 and ranges from 26 to 36 units.
Spotlights use intensity 150 and a 68-unit range.

```json
{
  "procedural_demo": {
    "enabled": true,
    "point_light_count": 28,
    "spot_light_count": 8,
    "animate_lights": true,
    "animated_point_light_count": 8,
    "animated_spot_light_count": 4,
    "animate_objects": true,
    "moving_object_count": 12
  }
}
```

## Performance Characteristics

The generated scene submits approximately 1,003,808 triangles:

- 96 shared 64x64 UV-sphere instances: 786,432 triangles.
- 48 shared 64x32 torus instances: 196,608 triangles.
- 64 shared box instances: 768 triangles.
- One 100x100 subdivided floor: 20,000 triangles.

Eight PBR materials cover rough dielectric, clear-coated, subsurface, and
metallic responses. Mesh and material resources are shared between instances.
Generated mesh files are cached under `Content/.cache/procedural-demo` and are
only rebuilt when missing. Scene loading selects raster rendering so the
workload measures Forward+, GPU culling, and cached shadow behavior.

Point and spot light counts are clamped to 128 and 64 respectively. Animated
counts are clamped to the corresponding total. Light and object animation only
changes ECS transforms; geometry and GPU mesh resources remain resident. The
mixed static/dynamic workload allows shadow-cache reuse while continuously
testing forced invalidation and budget carry-over.

## Cross-References

- [Scene Lights](lights.md)
- [PBR Pipeline](pbr-pipeline.md)
- [Render Graph](../renderer/render-graph.md)
- [engine-spec.md](../../engine-spec.md#4-asset-pipeline)
