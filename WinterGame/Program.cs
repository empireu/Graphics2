using Graphics2.Applications;
using Graphics2.Core.Bootstrap;
using Graphics2.Windowing;
using WinterGame;

var description = new ApplicationDescription
{
    Window = new G2WindowDescription
    {
        WindowTitle = "App"
    },
    Instance = new VkInstanceDescription
    {
        UseValidationLayers = true,
        ValidationFeaturesFlags = VkValidationFeatureFlags.Synchronization,
        ValidationShowVerboseSeverity = true
    }
};

using var app = new App(description);

app.Run();