using System.Numerics;
using ImGuiNET;

namespace WinterGame.Screens;

/// <summary>
///     Hides the session until its first build and the terrain materials' bake have finished, then closes itself.
/// </summary>
internal sealed class LoadingScreen(App app) : Screen
{
    public override bool HidesSession => true;

    public override void Update(double deltaTime)
    {
        if (app.Session is { IsLoaded: true } && app.Materials.IsUploaded)
        {
            app.Screens.Remove(this);
        }
    }

    public override void Draw()
    {
        if (BeginCenteredWindow("Loading"))
        {
            ImGui.Text("Building the world");
            ImGui.ProgressBar(app.Session?.LoadProgress ?? 0.0f, new Vector2(ButtonWidth, 0.0f));

            if (Button("Cancel"))
            {
                app.EndSession();
            }
        }

        ImGui.End();
    }
}
