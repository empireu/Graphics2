using Graphics2.Applications;
using Graphics2.Core.Render;
using Graphics2.Windowing.Input;
using Silk.NET.Maths;

namespace Demo;

internal sealed class App(ApplicationDescription description) : Application(description)
{
    private WorldLayer? _layer;
    
    protected override void BeforeStart()
    {
        _layer = new WorldLayer(this);
    }
    
    protected override void OnResize(Vector2D<int> framebufferSize)
    {
       _layer!.OnResize(framebufferSize);
    }

    protected override void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        _layer!.OnFrame(deltaTime, frame, input);
    }

    protected override void OnDispose()
    {
        _layer?.Dispose();
    }
}