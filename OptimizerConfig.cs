using System.ComponentModel;
using EngineOptimizer.Perf;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace EngineOptimizer;

// Клиентские настройки мода. Лейблы/тултипы берутся из
// Localization/ru-RU_Mods.EngineOptimizer.hjson (и en-US).
public sealed class OptimizerConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    // v1.1: глобальный пресет (см. Perf/RuntimeConfig.cs). Со входа в мир
    // задаёт поведение всех оптимизаций сразу; клавиша F9 меняет на лету.
    [DefaultValue(OptimizerPreset.Balanced)]
    public OptimizerPreset Preset { get; set; } = OptimizerPreset.Balanced;

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

    // v1.2: по умолчанию выключен — на части систем (драйверы Windows,
    // конфликт с курсорными модами) аппаратный курсор мерцает чёрным
    // кубом и даёт лаги. Включайте осознанно; конфликтующие моды мод
    // определит сам и выключит его принудительно.
    [DefaultValue(false)]
    public bool SmoothHardwareCursor { get; set; } = false;

    [DefaultValue(true)]
    public bool BossPerfMode { get; set; } = true;

    [DefaultValue(true)]
    public bool BoostProcessPriority { get; set; } = true;

    public static OptimizerConfig Instance => ModContent.GetInstance<OptimizerConfig>();
}
