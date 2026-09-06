using System.Numerics;
using Graphics2.Windowing;
using Graphics2.Windowing.Input;
using Silk.NET.Maths;

namespace Demo;

internal sealed class Camera3D
{
    private const float MoveSpeed = 2.0f;
    private const float LookSensitivity = 0.0025f;
    private const float MaxPitch = MathF.PI / 2.0f - 0.01f;

    public Vector3 Position = new(0.0f, 0.0f, -3.0f);
    public float Yaw;
    public float Pitch;

    public float Fov = MathF.PI / 3.0f;
    public float Near = 0.1f;
    public float Far = 100.0f;

    private float _aspect = 1.0f;

    public bool FpsMode { get; private set; }

    public void ToggleFpsMode(G2Window window)
    {
        FpsMode = !FpsMode;

        window.CursorMode = FpsMode
            ? G2CursorMode.Disabled
            : G2CursorMode.Normal;
    }

    public void Update(float deltaTime, InputSnapshot input, Vector2D<int> framebufferSize)
    {
        SetAspect(framebufferSize);

        if (!FpsMode)
        {
            return;
        }

        var mouseDelta = input.MousePosition - input.PreviousMousePosition;
        Yaw -= mouseDelta.X * LookSensitivity;
        Pitch -= mouseDelta.Y * LookSensitivity;
        Pitch = Math.Clamp(Pitch, -MaxPitch, MaxPitch);

        var forward = Forward;
        var right = Right;

        var move = Vector3.Zero;
        if (input.IsKeyDown(G2Key.W))
        {
            move += forward;
        }

        if (input.IsKeyDown(G2Key.S))
        {
            move -= forward;
        }

        if (input.IsKeyDown(G2Key.A))
        {
            move -= right;
        }

        if (input.IsKeyDown(G2Key.D))
        {
            move += right;
        }

        if (input.IsKeyDown(G2Key.Space))
        {
            move += Vector3.UnitY;
        }

        if (input.IsKeyDown(G2Key.C))
        {
            move -= Vector3.UnitY;
        }

        if (move != Vector3.Zero)
        {
            move = Vector3.Normalize(move);
            Position += move * MoveSpeed * deltaTime;
        }
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
        _aspect = framebufferSize.Y > 0 ? (float)framebufferSize.X / framebufferSize.Y : 1.0f;
    }

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw)
    );

    public Vector3 Right => new(
        -MathF.Cos(Yaw),
        0.0f,
        MathF.Sin(Yaw)
    );
}
