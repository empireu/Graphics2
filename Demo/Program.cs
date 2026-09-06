using Demo;
using Graphics2.Applications;
using Graphics2.Core.Bootstrap;
using Graphics2.Renderer;
using Graphics2.Renderer.Text;
using Graphics2.Windowing;

var font = new Font(File.ReadAllBytes("c:\\users\\alioth\\desktop\\Inter-VariableFont_opsz,wght.ttf"), 100.0f,
    RasterizationMode.Alpha);

var i = font.GetGlyphIndex('a');

var rd = font.RasterizeGlyph(i);

return;

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