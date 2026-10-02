using System.Numerics;
using WinterGame.Utilities;

namespace WinterGame.Simulation;

internal sealed class GameSimulation(Vector3 spawnPosition)
{
    public const int TicksPerSecond = 30;
    public const double TickSeconds = 1.0 / TicksPerSecond;

    private readonly Player _player = new(spawnPosition);

    public void Tick(TickInput input)
    {
        WgAssert.DebugIsOnGameThread();

        _player.Tick(input);
    }

    public GameSnapshot Extract()
    {
        WgAssert.DebugIsOnGameThread();

        return new GameSnapshot(_player.PreviousPosition, _player.Position);
    }
}
