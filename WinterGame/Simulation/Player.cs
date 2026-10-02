using System.Numerics;
using Graphics2.Windowing.Input;

namespace WinterGame.Simulation;

/// <summary>
///     The player as the game thread sees it: a position that moves once per tick.
///     Its rotation belongs to the render thread's camera and arrives with each tick's input.
/// </summary>
internal sealed class Player(Vector3 position)
{
    /// <summary>
    ///     Metres per second, and five times that with the left shift held.
    /// </summary>
    private const float MoveSpeed = 2.0f;

    public Vector3 Position = position;

    /// <summary>
    ///     The position before the last tick, which rendering interpolates from.
    /// </summary>
    public Vector3 PreviousPosition = position;

    public void Tick(TickInput input)
    {
        PreviousPosition = Position;

        var forward = Camera3D.GetForward(input.Yaw, input.Pitch);
        var right = Camera3D.GetRight(input.Yaw);
        var up = Vector3.Cross(right, forward);
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
            move += up;
        }

        if (input.IsKeyDown(G2Key.C))
        {
            move -= up;
        }

        if (move != Vector3.Zero)
        {
            move = Vector3.Normalize(move);
            var speed = input.IsKeyDown(G2Key.ShiftLeft) ? MoveSpeed * 5 : MoveSpeed;
            Position += move * speed * (float)GameSimulation.TickSeconds;
        }
    }
}
