// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using Engine.RHI;
using Engine.Scene.Components;

namespace Engine.Renderer;

internal static class ViewportCameraProjection
{
    public static CameraData Build(
        Camera camera,
        Transform transform,
        Vector3 localForward,
        float aspect,
        float projectionBlend,
        float orthographicSize)
    {
        BuildMatrices(
            camera,
            transform,
            localForward,
            aspect,
            projectionBlend,
            orthographicSize,
            out Matrix4x4 view,
            out Matrix4x4 projection,
            out Vector3 forward);
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4.Invert(
            viewProjection,
            out Matrix4x4 inverseViewProjection);
        return new CameraData
        {
            ViewProj = viewProjection,
            InvViewProj = inverseViewProjection,
            CameraPosition = new Vector4(
                transform.Position,
                1.0f),
            CameraForward = new Vector4(
                forward,
                Math.Clamp(projectionBlend, 0.0f, 1.0f))
        };
    }

    public static void BuildMatrices(
        Camera camera,
        Transform transform,
        Vector3 localForward,
        float aspect,
        float projectionBlend,
        float orthographicSize,
        out Matrix4x4 view,
        out Matrix4x4 projection,
        out Vector3 forward)
    {
        forward = Vector3.Transform(
            localForward,
            transform.Rotation);
        view = Matrix4x4.CreateLookAt(
            transform.Position,
            transform.Position + forward,
            Vector3.UnitY);
        Matrix4x4 perspective =
            Matrix4x4.CreatePerspectiveFieldOfView(
                camera.FieldOfView,
                aspect,
                camera.NearClip,
                camera.FarClip);
        Matrix4x4 orthographic =
            Matrix4x4.CreateOrthographic(
                MathF.Max(0.01f, orthographicSize) * aspect,
                MathF.Max(0.01f, orthographicSize),
                camera.NearClip,
                camera.FarClip);
        projection = Matrix4x4.Lerp(
            perspective,
            orthographic,
            Math.Clamp(projectionBlend, 0.0f, 1.0f));
    }
}
