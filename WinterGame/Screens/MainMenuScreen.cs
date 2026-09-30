using ImGuiNET;

namespace WinterGame.Screens;

internal sealed class MainMenuScreen(App app) : Screen
{
    public override void Draw()
    {
        if (BeginCenteredWindow("Main menu"))
        {
            if (Button("Play"))
            {
                app.StartSession();
            }

            if (Button("Quit"))
            {
                app.Quit();
            }
        }

        ImGui.End();
    }
}
