using WinterGame.World.Render;

namespace WinterGame.World;

/// <summary>
///     How sections are meshed from the store.
/// </summary>
public sealed class SectionMeshingDescription
{
    /// <summary>
    ///     The relaxation every section's mesh gets. Its iterations widen the window read around each section by one sample per iteration on every side.
    /// </summary>
    public SurfaceFairingSettings Fairing { get; init; } = new()
    {
        AllowedRange = 0.5f,
        StepSize = 0.5f,
        Iterations = 4
    };

    /// <summary>
    ///     Meshes in flight or waiting for the render thread at once. A slot holds its window and its result until the render thread has taken the result, so this also bounds the meshes waiting there.
    /// </summary>
    public int SlotCount { get; init; } = 8;
}
