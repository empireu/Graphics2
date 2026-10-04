using System.Numerics;
using ImGuiNET;

namespace WinterGame.Screens;

/// <summary>
///     A UI state kept in a <see cref="ScreenStack"/>, open over the session or over nothing in the main menu.
///     Only the top screen is updated and drawn, and while any screen is open the session gets no input.
/// </summary>
internal abstract class Screen
{
    protected const float ButtonWidth = 220.0f;

    /// <summary>
    ///     Whether the session under the screen is kept off the screen, which means neither the world nor its debug panels are drawn.
    /// </summary>
    public virtual bool HidesSession => false;

    /// <summary>
    ///     Whether the session under the screen stops ticking, so the world and its clock stand still.
    /// </summary>
    public virtual bool PausesSession => false;

    /// <summary>
    ///     Whether Escape closes the screen.
    /// </summary>
    public virtual bool ClosesOnEscape => false;

    public virtual void Update(double deltaTime)
    {
    }

    /// <summary>
    ///     Draws the screen with ImGui and acts on its widgets.
    /// </summary>
    public abstract void Draw();

    /// <summary>
    ///     Begins a fixed window centred on the viewport. <see cref="ImGui.End"/> has to follow it whatever it returns.
    /// </summary>
    protected static bool BeginCenteredWindow(string title)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        return ImGui.Begin(
            title,
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoSavedSettings
        );
    }

    protected static bool Button(string label)
    {
        return ImGui.Button(label, new Vector2(ButtonWidth, 0.0f));
    }
}
