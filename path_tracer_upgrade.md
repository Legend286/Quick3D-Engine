# Q3DE Hardware Ray Traced Path Tracer Specification

> **Goal:** Build a modern hardware ray traced path tracer that serves as the physically-correct reference renderer for Q3DE while sharing the exact same material system as the raster renderer.

---

# Core Principles

- Hardware ray tracing only (DXR / Vulkan RT)
- Shared material system with the raster renderer
- Physically based light transport
- Strict energy conservation
- Modular BSDF architecture
- Layered material evaluation
- Production-quality caustics
- Ground truth for validating raster rendering

---

# Core Integrator

## Features

- Unidirectional Path Tracing
- Multiple Importance Sampling (MIS)
- Next Event Estimation (NEE)
- Russian Roulette
- Progressive accumulation
- Low discrepancy sampling (Sobol + Owen scrambling)
- Importance sampled BSDFs
- Importance sampled HDR environment maps
- Importance sampled area lights
- Mesh light sampling

---

# Hardware Ray Tracing

Use the GPU API acceleration structures exclusively.

Do not implement custom BVHs.

Support

- TLAS
- BLAS
- Hardware traversal
- Hardware triangle intersection
- Instancing

---

# Shared Material System

The path tracer and raster renderer must evaluate the same material graph.

The raster renderer provides a real-time approximation.

The path tracer provides the physically correct reference implementation.

Every new material feature should automatically become available to both renderers whenever possible.

---

# BSDF Framework

## Dielectric

- GGX
- Smith masking-shadowing
- Fresnel
- Multiple-scattering energy compensation

Examples

- Plastic
- Wood
- Concrete
- Stone
- Ceramic

---

## Conductors

- GGX
- Complex IOR
- Measured conductor values
- Rough metals

Examples

- Gold
- Copper
- Aluminium
- Iron
- Silver

---

## Transmission

- Thin transmission
- Thick transmission
- Rough transmission
- Beer-Lambert absorption

Examples

- Glass
- Water
- Crystal

---

## Anisotropy

Support

- Brushed aluminium
- Satin
- Machined metals

---

## Clearcoat

Thin dielectric coating

Features

- Independent roughness
- Energy conserving
- Proper Fresnel attenuation

Examples

- Car paint
- Lacquer
- Varnish

---

## Sheen

Support

- Cloth
- Velvet
- Fabric fibres

---

# Layered Material System

Materials in Q3DE are evaluated as physical layers. The system evaluates the material stack physically, strictly maintaining energy conservation at every step.

## Stochastic BSDF Evaluation

To remain performant and physically accurate without evaluating multiple BSDF lobes per hit, the path tracer utilizes **Stochastic BSDF Blending**. 

Instead of interpolating parameters (like blending metallic values, which breaks PBR) or summing multiple heavy BSDF evaluations, the path tracer treats Layer Masks as a **probability function**. 

When a ray hits a surface:
1. The 3D Noise (or texture mask) is evaluated.
2. A random number determines which layer (Base or Top) is evaluated for that specific bounce.
3. This converges to the correct physical blend over multiple samples, strictly conserving energy and preserving sharp transitions (like dirt patches over shiny metal).

## Energy Conservation

- **Subsurface Scattering (SSS)**: SSS evaluates deep inside the material, acting as the base diffuse lobe. When SSS is enabled, the surface diffuse reflection is attenuated by `(1.0 - subsurface_weight)` to conserve energy, ensuring specular highlights remain intact even on highly translucent materials.
- **Clearcoat**: The Clearcoat layer is evaluated over the chosen base/top layer. Light that reflects off the clearcoat lobe is subtracted from the transmitted light reaching the base layers, respecting Fresnel reflection principles.

## Masking System

1. **3D Noise Masking**: Procedural value/simplex noise in world or object space allows rapid, textureless masking of materials (e.g. rust or dirt deposits).
2. **Texture Masks** (Future): UV-based blending maps.
3. **Vertex Painting** (Future): Per-vertex blend weights.

Benefits:
- Strict Energy conservation
- Physically correct reflections
- Stable material transitions
- No white metallic fringes
- Shared implementation architecture between raster and path tracing

---

# Supported Layer Types

- Dielectric
- Conductor
- Clearcoat
- Transmission
- Sheen

Future

- Thin film
- Hair
- Volume layers

---

# Material Examples

Plastic

Clearcoat

↓

Plastic

---

Varnished Wood

Clearcoat

↓

Wood

---

Car Paint

Clearcoat

↓

Pigmented Paint

↓

Metal Flakes (future)

---

Wet Rock

Water Layer

↓

Stone

---

Gold Ring

Gold Conductor

---

Glass

Transmission

---

Fabric

Dielectric

↓

Sheen

---

# Material Blending

Blend complete BSDF evaluations rather than interpolating parameters.

Support

- Texture masks
- Vertex painting
- Terrain blending

Maintain

- Energy conservation
- Stable transitions
- Correct Fresnel
- Physically plausible appearance

---

# Lighting

Support

- Directional
- Point
- Spot
- Rectangle Area
- Disk Area
- Sphere Area
- Tube Area
- Mesh Lights
- HDR Environment Maps
- Nishita Atmosphere

---

# Environment Lighting

- Importance sampled HDRI
- Nishita atmosphere integration
- Sun sampling
- Sky sampling

---

# Caustics

## Primary Technique

Vertex Connection and Merging (VCM)

VCM combines

- Bidirectional Path Tracing
- Vertex Connection
- Photon Merging

Goals

- Robust specular caustics
- Fast convergence
- Accurate focused light
- Difficult light path handling

Target scenarios

- Glass spheres
- Diamonds
- Crystal
- Water
- Swimming pools
- Lenses
- Wine glasses
- Jewellery

---

# Path Guiding

Implement path guiding after the core integrator is complete.

Purpose

Learn where radiance arrives from and bias future ray directions accordingly.

Goals

- Reduce variance
- Improve indoor convergence
- Better indirect lighting
- Faster difficult scene rendering
- Complement MIS and VCM

The guiding system should remain unbiased while adapting over successive samples.

Path guiding should work alongside

- MIS
- NEE
- VCM

rather than replacing them.

---

# Random Walk Subsurface Scattering

Already implemented.

Integrate with

- Layered materials
- Energy conservation
- Shared BSDF framework

Target materials

- Skin
- Wax
- Marble
- Jade
- Milk

---

# Camera

Support

- Thin lens
- Depth of field
- Motion blur
- Physical exposure

---

# Denoising

Render outputs

- Beauty
- Albedo
- Normals
- Motion vectors
- Depth

Support

- Intel Open Image Denoise

Future

- NVIDIA NRD
- NVIDIA OptiX

---

# Debug Views

Geometry

- Normals
- Tangents
- UVs

Materials

- Layer index
- Layer contribution
- Roughness
- Fresnel
- IOR

Lighting

- Direct
- Indirect
- Diffuse
- Specular
- Transmission

Path Tracing

- Bounce count
- Path throughput
- Ray type
- Samples per pixel
- MIS weights
- Path guiding visualization
- VCM contribution visualization

---

# Deferred / Raster Validation

The path tracer should be capable of validating

- Direct lighting
- Indirect lighting
- Material response
- Layered materials
- Shadows
- Reflections
- Refractions
- Atmospheric rendering
- Subsurface scattering

Pixel-perfect agreement is not required, but material appearance should closely match.

---

# Future Research

Not part of the initial implementation.

- Thin-film interference
- Polarisation
- Measured BRDFs
- Hair rendering
- Volumetric rendering
- Clouds
- Smoke
- Fire

---

# Long-Term Vision

The Q3DE path tracer should become the engine's physically-correct reference renderer.

It should provide

- Hardware accelerated path tracing
- Shared layered materials
- Energy-conserving BSDF evaluation
- State-of-the-art caustics using Vertex Connection and Merging
- Adaptive path guiding for rapid convergence
- Consistent material appearance with the raster renderer
- A modular architecture for future rendering research