using Terraria.ModLoader;

namespace EngineOptimizer;

// Точка входа мода. Вся работа — в Perf/*: ProfilerSystem вешает хуки движка,
// NpcAiProfiler считает стоимость NPC.AI, Telemetry пишет диагностику.
public sealed class EngineOptimizer : Mod
{
    public override void Load()
    {
        Logger.Info("Engine Optimizer loaded. F6 toggles the profiler overlay.");
    }
}
