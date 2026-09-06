using Demo;
using Graphics2.Applications;
using Graphics2.Core.Bootstrap;
using Graphics2.Windowing;

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