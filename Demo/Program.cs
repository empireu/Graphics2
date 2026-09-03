using Graphics2.Applications;
using Graphics2.Core.Bootstrap;
using Graphics2.Windowing;
using Silk.NET.Maths;

var description = new ApplicationDescription
{
    Window = new G2WindowDescription
    {
        WindowTitle = "App"
    },
    Instance = new VkInstanceDescription
    {
        UseValidationLayers = true,
        ValidationShowVerboseSeverity = true
    }
};

using var app = new App(description);

app.Run();

class App(ApplicationDescription description) : Application(description)
{
    private int _frames;

    protected override void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");
    }

    protected override void OnFrame(double deltaTime)
    {
        if (++_frames >= 30)
        {
            Window.Window.Close();
        }
    }
}
