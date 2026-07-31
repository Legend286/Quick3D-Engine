// SPDX-License-Identifier: MIT
// The renderer-plugin contracts (RendererPluginContext,
// RendererPluginPlan, IRendererPlanPlugin) live in
// engine_cs/Engine.RenderGraph now so plugins can implement them
// without taking a hard assembly dependency on Engine.Renderer.
// This file is intentionally empty — kept as a tombstone so any
// stale Type or namespace string referencing
// Engine.Renderer.RendererPluginContext still resolves via a
// re-export if a future migration needs it.
