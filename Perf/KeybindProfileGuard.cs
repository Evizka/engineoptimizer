using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace EngineOptimizer.Perf;

// tML добавляет кейбинды модов в профиль управления в PlayerInput.UpdateInput,
// но при прямом запуске в мир DoUpdate выходит раньше, и профиль остаётся
// ванильным. Моды, читающие свой кейбинд в OnEnterWorld (так делает
// Ore Excavator 0.8.9), падали с KeyNotFoundException -> Main engine crash.
// Перед спавном молча добавляем недостающие пустые (не назначенные) записи:
// существующие привязки не меняются. Если tML изменит внутренности —
// шов тихо отключается.
public static class KeybindProfileGuard
{
    public static Action<string> Log;

    private static FieldInfo _keybindsField;
    private static bool _resolved;
    private static object _lastProfile;
    private static int _lastKeybindCount = -1;
    private static bool _satisfied;
    private static bool _repairLogged;

    public static void Reset()
    {
        _resolved = false;
        _keybindsField = null;
        _lastProfile = null;
        _lastKeybindCount = -1;
        _satisfied = false;
        _repairLogged = false;
    }

    public static void Ensure()
    {
        if (!_resolved)
        {
            Resolve();
            if (!_resolved)
                return;
        }

        try
        {
            var profile = PlayerInput.CurrentProfile;
            if (profile == null)
                return;

            var keybinds = _keybindsField.GetValue(null) as IDictionary;
            if (keybinds == null)
                return;

            if (_satisfied && ReferenceEquals(profile, _lastProfile) && keybinds.Count == _lastKeybindCount)
                return;

            int added = 0;
            foreach (string name in keybinds.Keys)
            {
                if (name == null || name.Length == 0)
                    continue;

                foreach (var configuration in profile.InputModes.Values)
                {
                    if (!configuration.KeyStatus.ContainsKey(name))
                    {
                        configuration.KeyStatus[name] = new List<string>();
                        added++;
                    }
                }
            }

            _lastProfile = profile;
            _lastKeybindCount = keybinds.Count;
            _satisfied = true;

            if (added > 0 && !_repairLogged)
            {
                _repairLogged = true;
                Log?.Invoke($"Input profile repaired before world entry: {added} missing mod keybind entr{(added == 1 ? "y" : "ies")} added (unbound).");
            }
        }
        catch (Exception e)
        {
            _resolved = false;
            _satisfied = true;
            Log?.Invoke("Keybind guard disabled: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void Resolve()
    {
        _resolved = true;
        _keybindsField = typeof(ModKeybind).Assembly
            .GetType("Terraria.ModLoader.KeybindLoader")
            ?.GetField("modKeybinds", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (_keybindsField == null)
            Log?.Invoke("Keybind guard unavailable on this tModLoader build.");
    }
}
