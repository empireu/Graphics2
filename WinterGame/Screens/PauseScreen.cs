using ImGuiNET;

namespace WinterGame.Screens;

internal sealed class PauseScreen(App app) : Screen
{
    public override bool PausesSession => true;

    public override bool ClosesOnEscape => true;

    public override void Draw()
    {
        if (BeginCenteredWindow("Paused"))
        {
            if (Button("Resume"))
            {
                app.Screens.Remove(this);
            }

            if (Button("Quit to menu"))
            {
                app.EndSession();
            }

            if (Button("Quit game"))
            {
                app.Quit();
            }
        }

        ImGui.End();
    }
}
