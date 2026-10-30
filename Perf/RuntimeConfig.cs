using Terraria;

namespace EngineOptimizer.Perf;

// v1.1: глобальные пресеты оптимизации. Один переключатель вместо десятка
// тумблеров конфига:
//   Balanced        — как в конфиге пользователя (ничего не переопределяется);
//   MaxFps          — всё агрессивное: boss-perf, параллельный gore,
//                     предзагрузка (бюджет минимум 5 мс), GC, приоритет;
//   DiagnosticsOnly — все патчи движка выключены, мод только измеряет
//                     (оверлей F6, live.json, last-session.txt).
// Пресет выбирается в конфиге (действует со входа в мир) или клавишей F9
// прямо в игре (действует до перезапуска, как F7/F8).
public enum OptimizerPreset
{
    Balanced,
    MaxFps,
    DiagnosticsOnly
}

public static class RuntimeConfig
{
    // Текущий пресет. При входе в мир берётся из конфига; F9 меняет на лету.
    public static OptimizerPreset Preset { get; private set; } = OptimizerPreset.Balanced;

    public static void SetPreset(OptimizerPreset preset)
    {
        Preset = preset;
    }

    public static OptimizerPreset Cycle()
    {
        Preset = Preset switch
        {
            OptimizerPreset.Balanced => OptimizerPreset.MaxFps,
            OptimizerPreset.MaxFps => OptimizerPreset.DiagnosticsOnly,
            _ => OptimizerPreset.Balanced
        };
        return Preset;
    }

    public static string PresetName => Preset switch
    {
        OptimizerPreset.MaxFps => "max fps",
        OptimizerPreset.DiagnosticsOnly => "diagnostics",
        _ => "balanced"
    };

    private static OptimizerConfig Cfg => OptimizerConfig.Instance;

    // ---- эффективные значения (конфиг + пресет) ----

    public static bool ReduceGcPauses =>
        Preset == OptimizerPreset.DiagnosticsOnly ? false : Cfg?.ReduceGcPauses != false;

    public static bool DeferAutosaveDuringBoss =>
        Preset == OptimizerPreset.DiagnosticsOnly
            ? false
            : Preset == OptimizerPreset.MaxFps
                ? true
                : Cfg?.DeferAutosaveDuringBoss != false;

    public static bool ParallelGore =>
        Preset == OptimizerPreset.DiagnosticsOnly
            ? false
            : Preset == OptimizerPreset.MaxFps
                ? true
                : Cfg?.ParallelGore == true;

    // Дождь — эксперимент (конфликт с HighFPSSupport): включается только
    // явным тумблером в конфиге, пресетом не форсируется.
    public static bool ParallelRain =>
        Preset == OptimizerPreset.DiagnosticsOnly ? false : Cfg?.ParallelRain == true;

    public static bool PreloadWorld =>
        Preset == OptimizerPreset.DiagnosticsOnly
            ? false
            : Preset == OptimizerPreset.MaxFps
                ? true
                : Cfg?.PreloadWorld == true;

    public static int PreloadBudgetMs =>
        Preset == OptimizerPreset.MaxFps
            ? System.Math.Max(5, Cfg?.PreloadBudgetMs ?? 3)
            : Cfg?.PreloadBudgetMs ?? 3;

    public static bool BossPerfMode =>
        Preset == OptimizerPreset.DiagnosticsOnly
            ? false
            : Preset == OptimizerPreset.MaxFps
                ? true
                : Cfg?.BossPerfMode == true;

    public static bool BoostProcessPriority =>
        Preset == OptimizerPreset.DiagnosticsOnly ? false : Cfg?.BoostProcessPriority != false;

    // Диагностика работает всегда — это ядро мода.
    public static bool ShowOverlayOnStart => Cfg?.ShowOverlayOnStart != false;
    public static bool SaveReportOnWorldExit => Cfg?.SaveReportOnWorldExit != false;
}
