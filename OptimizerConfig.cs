using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace EngineOptimizer;

// Клиентские настройки мода. Лейблы/тултипы берутся из
// Localization/ru-RU_Mods.EngineOptimizer.hjson (и en-US).
public sealed class OptimizerConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    [DefaultValue(true)]
    public bool ShowOverlayOnStart { get; set; } = true;

    [DefaultValue(true)]
    public bool SaveReportOnWorldExit { get; set; } = true;

    [DefaultValue(true)]
    public bool ReduceGcPauses { get; set; } = true;

    [DefaultValue(true)]
    public bool DeferAutosaveDuringBoss { get; set; } = true;

    [DefaultValue(true)]
    public bool ParallelGore { get; set; } = true;

    [DefaultValue(false)]
    public bool ParallelRain { get; set; }

    [DefaultValue(true)]
    public bool PreloadWorld { get; set; } = true;

    [DefaultValue(3)]
    [Range(0, 30)]
    public int PreloadBudgetMs { get; set; } = 3;

    [DefaultValue(true)]
    public bool SmoothHardwareCursor { get; set; } = true;

    [DefaultValue(true)]
    public bool BossPerfMode { get; set; } = true;

    [DefaultValue(true)]
    public bool BoostProcessPriority { get; set; } = true;

    public static OptimizerConfig Instance => ModContent.GetInstance<OptimizerConfig>();
}
