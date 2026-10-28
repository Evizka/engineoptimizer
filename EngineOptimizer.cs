using Terraria.ModLoader;

namespace EngineOptimizer;

// Точка входа мода. Вся работа — в Perf/*: ProfilerSystem вешает хуки движка,
// SmoothCursorSystem отвечает за аппаратный курсор, Telemetry пишет диагностику.
public sealed class EngineOptimizer : Mod
{
    public override void Load()
    {
        Logger.Info("Engine Optimizer loaded. F6 toggles the profiler overlay.");
    }
}
