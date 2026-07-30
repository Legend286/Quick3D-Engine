// SPDX-License-Identifier: MIT

using Avalonia.Input;

namespace Engine.Editor;

internal sealed record AssetDragPayload(
    string AssetPath,
    int ModelPartIndex);

internal static class AssetDragData
{
    internal static readonly DataFormat<AssetDragPayload> Format =
        DataFormat.CreateInProcessFormat<AssetDragPayload>(
            "quick3d.asset");
}
