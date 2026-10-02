using System.Numerics;
using Graphics2.Windowing;
using Graphics2.Windowing.Input;
using Silk.NET.Maths;

namespace WinterGame;

internal sealed class Camera3D
{
    public float LookSensitivity = 0.0025f;
    public float MaxPitch = MathF.PI / 2.0f - 0.01f;

    /// <summary>
    ///     Set every frame from the game's snapshot. The camera owns only its rotation.
    /// </summary>
    public Vector3 Position = new(0.0f, 0.0f, -3.0f);
    public float Yaw;
    public float Pitch;

    public float Fov = MathF.PI / 3.0f;
    public float Near = 0.1f;
    public float Far = 100.0f;

    private float _aspect = 1.0f;

    public bool FpsMode { get; private set; }

    private bool _switchedMode;
    
    public void ToggleFpsMode(G2Window window)
    {
        FpsMode = !FpsMode;

        window.CursorMode = FpsMode
            ? G2CursorMode.Disabled
            : G2CursorMode.Normal;

        _switchedMode = true;
    }

    /// <summary>
    ///     Turns the camera by the mouse every frame while it is in FPS mode.
    /// </summary>
    public void Update(InputSnapshot input, Vector2D<int> framebufferSize)
    {
        SetAspect(framebufferSize);

        if (!FpsMode)
        {
            return;
        }

        if (!_switchedMode)
        {
            var mouseDelta = input.MousePosition - input.PreviousMousePosition;
            Yaw -= mouseDelta.X * LookSensitivity;
            Pitch -= mouseDelta.Y * LookSensitivity;
            Pitch = Math.Clamp(Pitch, -MaxPitch, MaxPitch);
        }

        _switchedMode = false;
    }

    public Matrix4x4 ViewProj
    {
        get
        {
            var target = Position + Forward;
            var view = Matrix4x4.CreateLookAt(Position, target, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(Fov, _aspect, Near, Far);
            projection.M22 = -projection.M22;
            return view * projection;
        }
    }

    public void SetAspect(Vector2D<int> framebufferSize)
    {
        _aspect = framebufferSize.Y > 0 
            ? (float)framebufferSize.X / framebufferSize.Y
            : 1.0f;
    }

    public Vector3 Forward => GetForward(Yaw, Pitch);
    public Vector3 Right => GetRight(Yaw);
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public static Vector3 GetForward(float yaw, float pitch) => new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
    public static Vector3 GetRight(float yaw) => new(-MathF.Cos(yaw), 0.0f, MathF.Sin(yaw));
}
