using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL2;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI.Gamepad;

namespace EngineOptimizer.Perf;

// Аппаратный курсор: рендерит ванильный курсор (со всеми эффектами —
// толщина, цвет, override) в render target 128×128, читает пиксели назад
// и переносит в системный курсор SDL. Курсор больше не рисуется каждый
// кадр в основном проходе — вместо этого раз в 50 мс или при смене
// внешнего вида. Кеш: сигнатура состояния (масштаб, цвета, smart и т.д.)
// + хеш пикселей: пока не изменились, SDL-курсор не пересоздаётся.
public sealed class SmoothCursorSystem : ModSystem
{
    private const int TextureSize = 128;
    private const float Pad = 12f;
    private const double MinRefreshMs = 50.0;

    private static EngineOptimizer _mod;

    private static RenderTarget2D _target;
    private static Color[] _pixels;
    private static RenderTargetBinding[] _bindings;
    private static Viewport _savedViewport;

    private static IntPtr _cursor;
    private static IntPtr _defaultCursor; // v0.8.10: системная стрелка для отката
    private static int _hotX;
    private static int _hotY;
    private static ulong _pixelHash;
    private static ulong _signature;
    private static long _lastRefresh;

    private static int _interfaceOverride = -1;
    private static int _interfaceOverrideFrame = -1;
    private static int _frame;
    private static bool _rendering;
    private static bool _active;
    private static bool _failed;
    private static int _failures;
    private static long _retryAt;
    private static long _lastErrorLog;

    // v0.8.10: аварийный выключатель (F7). Если аппаратный курсор
    // спрятался (пустая картинка, конфликт с другим модом) — без мыши
    // в меню не попасть; клавиша возвращает ванильный курсор немедленно.
    public static bool UserDisabled;

    static SmoothCursorSystem()
    {
        _pixelHash = ulong.MaxValue;
        _signature = ulong.MaxValue;
        _interfaceOverride = -1;
        _interfaceOverrideFrame = -1;
    }

    public static bool Active => _active;

    // v0.8.10: расширенный статус для оверлея и телеметрии: учитывает
    // аварийное выключение (F7), меню и конфиг.
    public static string Status
    {
        get
        {
            if (Main.dedServ)
                return "-";
            if (ConflictReason != null)
                return "conflict";
            if (UserDisabled)
                return "off";
            if (_failed)
                return "failed";
            if (!ConfigEnabled())
                return "off";
            if (Main.gameMenu)
                return "menu";
            return _active ? "hw" : "vanilla";
        }
    }

    public override void Load()
    {
        _mod = Mod as EngineOptimizer;

        On_Main.Draw += OnDraw;
        On_Main.DoUpdate += OnDoUpdate;
        On_Main.DrawCursor += OnDrawCursor;
        On_Main.DrawThickCursor += OnDrawThickCursor;
        On_Main.DrawInterface_36_Cursor += OnDrawInterfaceCursor;
    }

    public override void Unload()
    {
        On_Main.Draw -= OnDraw;
        On_Main.DoUpdate -= OnDoUpdate;
        On_Main.DrawCursor -= OnDrawCursor;
        On_Main.DrawThickCursor -= OnDrawThickCursor;
        On_Main.DrawInterface_36_Cursor -= OnDrawInterfaceCursor;

        Deactivate();
        DisposeTarget();

        _mod = null;
        _failed = false;
        _failures = 0;
        _retryAt = 0;
        _lastErrorLog = 0;
        _conflictAnnounced = false;
        _offAnnounced = false;
        _recreateCount = 0;
        _recreateWindowStart = 0;
    }

    public override void OnWorldLoad()
    {
        // После смены мира даём плавному курсору шанс даже после сбоев.
        if (!_failed && _failures > 0)
        {
            _failures = 0;
            _retryAt = 0;
        }
    }

    private static void OnDoUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        orig(self, ref gameTime);

        if (_active && Main.instance != null && !Main.instance.IsMouseVisible)
            Main.instance.IsMouseVisible = true;
    }

    private static void OnDraw(On_Main.orig_Draw orig, Main self, GameTime gameTime)
    {
        _frame++;

        if (Main.IsGraphicsDeviceAvailable && !Main.dedServ)
        {
            long start = PerfData.Now;
            try
            {
                Update();
            }
            catch (Exception e)
            {
                Fail("cursor update failed", e);
            }
            finally
            {
                PerfData.AddCursor(PerfData.MsSince(start));
            }
        }

        orig(self, gameTime);
    }

    // Пока активен аппаратный курсор — ванильная отрисовка подавляется.
    private static void OnDrawCursor(On_Main.orig_DrawCursor orig, Vector2 bonus, bool smart)
    {
        if (!_active || _rendering)
            return;

        orig(bonus, smart);
    }

    private static Vector2 OnDrawThickCursor(On_Main.orig_DrawThickCursor orig, bool smart)
    {
        if (!_active || _rendering)
            return Vector2.Zero;

        return orig(smart);
    }

    private static void OnDrawInterfaceCursor(On_Main.orig_DrawInterface_36_Cursor orig)
    {
        if (!_active || _rendering)
        {
            orig();
            return;
        }

        // Запоминаем override (курсоры интерфейса: рука, меч и т.д.),
        // чтобы следующий рендер взял нужную текстуру.
        _interfaceOverride = Main.cursorOverride;
        _interfaceOverrideFrame = _frame;
    }

    private static void Update()
    {
        if (!ShouldRun())
        {
            if (_active)
                Deactivate();
            return;
        }

        if (_failures > 0)
        {
            if (_failed)
                return;
            if (PerfData.Now < _retryAt)
                return;
        }

        if (Main.instance == null || Main.graphics == null || Main.graphics.GraphicsDevice == null || Main.spriteBatch == null)
            return;

        EnsureTarget();

        int over = _frame - _interfaceOverrideFrame <= 2 ? _interfaceOverride : -1;
        if (over >= 0 && over >= TextureAssets.Cursors.Length)
            over = -1;

        int hotX = 0;
        int hotY = 0;
        ulong signature = ComputeSignature(over, ref hotX, ref hotY);

        // Ничего не изменилось: достаточно убедиться, что системный курсор наш.
        if (_active && signature == _signature && _cursor != IntPtr.Zero)
        {
            if (SDL.SDL_GetCursor() != _cursor)
                SDL.SDL_SetCursor(_cursor);
            return;
        }

        // v0.8.10: троттлинг и для повторных попыток (раньше — только когда
        // активны): пустой рендер не гоняется каждый кадр.
        if (PerfData.Now - _lastRefresh < (long)(Stopwatch.Frequency * MinRefreshMs / 1000.0))
            return;

        RenderCursor(over);

        _target.GetData(_pixels);

        // v0.8.10: защита от «исчезнувшего» курсора: в аппаратный курсор
        // отдаём только заведомо видимую картинку (>= 8 непрозрачных
        // пикселей). Почти пустой target (пара случайных пикселей от
        // чужой отрисовки) раньше мог пройти проверку и сделать курсор
        // невидимым, подавив заодно и ванильную отрисовку.
        if (CountVisiblePixels(128) < 8)
        {
            _signature = signature;
            _lastRefresh = PerfData.Now;
            return;
        }

        if (LooksOpaque())
        {
            Fail("cursor render produced an opaque target", null);
            return;
        }

        ulong hash = HashPixels();

        if (_cursor == IntPtr.Zero || hash != _pixelHash || hotX != _hotX || hotY != _hotY)
        {
            IntPtr created = CreateCursor(_pixels, TextureSize, TextureSize, hotX, hotY);
            if (created == IntPtr.Zero)
            {
                Fail("SDL could not create the cursor: " + SDL.SDL_GetError(), null);
                return;
            }

            // v1.2: если картинка курсора меняется слишком часто (чужой мод
            // подменяет курсор каждый кадр) — это мерцание и лишние лаги:
            // выключаемся насовсем, дальше ванильный курсор.
            long nowTicks = PerfData.Now;
            if (nowTicks - _recreateWindowStart > Stopwatch.Frequency * 10)
            {
                _recreateWindowStart = nowTicks;
                _recreateCount = 0;
            }
            if (++_recreateCount > 6)
            {
                SDL.SDL_FreeCursor(created);
                Fail("cursor image changes too often (a cursor mod is fighting for the cursor)", null);
                return;
            }

            SDL.SDL_SetCursor(created);
            if (_cursor != IntPtr.Zero)
                SDL.SDL_FreeCursor(_cursor);
            _cursor = created;
            _pixelHash = hash;
            _hotX = hotX;
            _hotY = hotY;
        }
        else
        {
            SDL.SDL_SetCursor(_cursor);
        }

        _signature = signature;
        _lastRefresh = PerfData.Now;
        _active = true;

        if (Main.instance != null && !Main.instance.IsMouseVisible)
            Main.instance.IsMouseVisible = true;
    }

    private static bool ShouldRun()
    {
        if (UserDisabled || _failed || Main.dedServ)
            return false;

        if (!ConfigEnabled())
            return false;

        if (Main.gameMenu || Main.alreadyGrabbingSunOrMoon)
            return false;

        if (PlayerInput.SettingsForUI.ShowGamepadCursor)
            return false;

        return true;
    }

    // Сигнатура всего, что влияет на внешний вид курсора: пока она
    // неизменна, пиксели можно не перечитывать.
    private static ulong ComputeSignature(int over, ref int hotX, ref int hotY)
    {
        float ui = Math.Max(0.1f, Main.UIScale);

        int pad = Math.Max(0, (int)Math.Round(Pad * ui));
        hotX = pad;
        hotY = pad;

        ulong hash = 14695981039346656037UL;
        Mix(ref hash, (uint)(over + 2));
        Mix(ref hash, Main.SmartCursorIsUsed ? 1u : 0u);
        Mix(ref hash, Main.ThickMouse ? 1u : 0u);
        Mix(ref hash, Quantize(Main.cursorColor));
        Mix(ref hash, Quantize(Main.MouseBorderColor));
        Mix(ref hash, (uint)Math.Round(Main.cursorScale * 10.0));
        Mix(ref hash, (uint)Math.Round(ui * 100.0));
        Mix(ref hash, Main.LocalPlayer?.hasRainbowCursor == true ? 1u : 0u);
        Mix(ref hash, UILinkPointNavigator.Available ? 1u : 0u);
        Mix(ref hash, PlayerInput.InBuildingMode ? 1u : 0u);

        if (Main.LocalPlayer != null && Main.LocalPlayer.hasRainbowCursor)
        {
            float hue = Main.GlobalTimeWrappedHourly * 0.25f % 1f;
            Mix(ref hash, (uint)Math.Round(hue * 64.0));
        }

        return hash;
    }

    private static void Mix(ref ulong hash, uint value)
    {
        hash = (hash ^ value) * 1099511628211UL;
    }

    private static uint Quantize(Color color)
    {
        return (uint)((color.R >> 4) << 12 | (color.G >> 4) << 8 | (color.B >> 4) << 4 | color.A >> 4);
    }

    private static void RenderCursor(int over)
    {
        var device = Main.graphics.GraphicsDevice;

        int count = device.GetRenderTargetsNoAllocEXT(null);
        if (_bindings == null || _bindings.Length != count)
            _bindings = count != 0 ? new RenderTargetBinding[count] : Array.Empty<RenderTargetBinding>();
        if (count > 0)
            device.GetRenderTargetsNoAllocEXT(_bindings);

        _savedViewport = device.Viewport;

        device.SetRenderTarget(_target);
        device.Clear(Color.Transparent);

        var at = new Vector2(Pad);
        var begun = false;
        try
        {
            Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, Main.SamplerStateForCursor,
                DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            begun = true;

            if (over == -1)
            {
                var bonus = DrawThickCursor(at, Main.SmartCursorIsUsed);
                DrawColoredCursor(at + bonus, Main.SmartCursorIsUsed);
            }
            else
            {
                DrawCursorOverride(at, over);
            }
        }
        finally
        {
            if (begun)
            {
                try
                {
                    Main.spriteBatch.End();
                }
                catch
                {
                }
            }

            _rendering = false;

            if (count <= 0)
                device.SetRenderTarget(null);
            else
                device.SetRenderTargets(_bindings);

            device.Viewport = _savedViewport;
        }
    }

    private static Vector2 DrawThickCursor(Vector2 at, bool smart)
    {
        if (!Main.ThickMouse)
            return Vector2.Zero;

        var color = Main.MouseBorderColor;
        var texture = TextureAssets.Cursors[smart ? 11 : 12].Value;
        var origin = new Vector2(2f);
        var scale = Main.cursorScale * 1.1f;

        for (int i = 0; i < 4; i++)
        {
            var offset = i switch
            {
                0 => new Vector2(0f, 1f),
                1 => new Vector2(1f, 0f),
                2 => new Vector2(0f, -1f),
                _ => new Vector2(-1f, 0f),
            };
            offset = offset + Vector2.One * 2f;
            Main.spriteBatch.Draw(texture, at + offset, null, color, 0f, origin, scale, SpriteEffects.None, 0f);
        }

        return new Vector2(2f);
    }

    private static void DrawColoredCursor(Vector2 at, bool smart)
    {
        var color = Main.cursorColor;
        if (!Main.gameMenu && Main.LocalPlayer != null && Main.LocalPlayer.hasRainbowCursor)
            color = Main.hslToRgb(Main.GlobalTimeWrappedHourly * 0.25f % 1f, 1f, 0.5f, 255);

        var texture = TextureAssets.Cursors[smart ? 1 : 0].Value;

        Main.spriteBatch.Draw(texture, at + Vector2.One, null,
            new Color((int)(color.R * 0.2f), (int)(color.G * 0.2f), (int)(color.B * 0.2f), (int)(color.A * 0.5f)),
            0f, Vector2.Zero, Main.cursorScale * 1.1f, SpriteEffects.None, 0f);
        Main.spriteBatch.Draw(texture, at, null, color, 0f, Vector2.Zero, Main.cursorScale, SpriteEffects.None, 0f);
    }

    private static void DrawCursorOverride(Vector2 at, int over)
    {
        var shadow = true;
        var color = Main.cursorColor;
        var originFactor = Vector2.Zero;
        var scale = 1f;

        if (over == 2)
        {
            shadow = false;
            color = Color.White;
            scale = 0.7f;
            originFactor = new Vector2(0.1f);
        }
        else if (over == 3 || over == 6 || over == 7 || over == 8 || over == 9 || over == 10)
        {
            shadow = false;
            color = Color.White;
        }

        var texture = TextureAssets.Cursors[over].Value;
        var origin = originFactor * new Vector2(texture.Width, texture.Height);

        if (shadow)
        {
            var shadowColor = new Color((int)(color.R * 0.2f), (int)(color.G * 0.2f), (int)(color.B * 0.2f), (int)(color.A * 0.5f));
            Main.spriteBatch.Draw(texture, at + Vector2.One, null, shadowColor, 0f, origin,
                Main.cursorScale * 1.1f * scale, SpriteEffects.None, 0f);
        }

        Main.spriteBatch.Draw(texture, at, null, color, 0f, origin, Main.cursorScale * scale, SpriteEffects.None, 0f);
    }

    private static void EnsureTarget()
    {
        if (_target != null && !_target.IsDisposed)
            return;

        DisposeTarget();
        _target = new RenderTarget2D(Main.graphics.GraphicsDevice, TextureSize, TextureSize, false, SurfaceFormat.Color, DepthFormat.None);
        _pixels = new Color[TextureSize * TextureSize];
        _pixelHash = ulong.MaxValue;
        _signature = ulong.MaxValue;
    }

    // v0.8.10: сколько пикселей непрозрачнее minAlpha — порог видимости.
    private static int CountVisiblePixels(int minAlpha)
    {
        var pixels = _pixels;
        int count = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A >= minAlpha)
                count++;
        }
        return count;
    }

    private static bool LooksOpaque()
    {
        var pixels = _pixels;
        int opaque = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A >= 250)
                opaque++;
        }
        return opaque > pixels.Length * 3 / 5;
    }

    private static ulong HashPixels()
    {
        ulong hash = 14695981039346656037UL;
        var words = MemoryMarshal.Cast<Color, uint>(_pixels.AsSpan());
        for (int i = 0; i < words.Length; i++)
        {
            hash = (hash ^ words[i]) * 1099511628211UL;
        }
        return hash;
    }

    private static IntPtr CreateCursor(Color[] pixels, int width, int height, int hotX, int hotY)
    {
        var handle = default(GCHandle);
        IntPtr surface = IntPtr.Zero;

        // v1.2: чёрный куб вокруг курсора на части драйверов Windows:
        // пиксели с A=0, но RGB!=0 (остатки блендинга SpriteBatch) некоторые
        // драйверы рисуют как непрозрачный чёрный. Зануляем их.
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A == 0)
                pixels[i] = default(Color);
        }

        handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            surface = SDL.SDL_CreateRGBSurfaceFrom(handle.AddrOfPinnedObject(), width, height, 32, width * 4,
                255u, 65280u, 16711680u, 0xFF000000u);
            if (surface == IntPtr.Zero)
                return IntPtr.Zero;

            return SDL.SDL_CreateColorCursor(surface, hotX, hotY);
        }
        finally
        {
            if (surface != IntPtr.Zero)
                SDL.SDL_FreeSurface(surface);

            if (handle.IsAllocated)
                handle.Free();
        }
    }

    // v0.8.10: полное выключение по клавише (F7) — ванильный курсор.
    public static void Disable()
    {
        Deactivate();
        _failed = false;
        _failures = 0;
        _retryAt = 0;
        _lastErrorLog = 0;
    }

    // v0.8.10: флаг сбоя для оверлея (F6).
    public static bool Failed => _failed;

    private static bool ConfigEnabled()
    {
        if (ConflictReason != null)
            return false;

        try
        {
            return RuntimeConfig.SmoothHardwareCursor; // v1.1: пресет
        }
        catch
        {
            return false;
        }
    }

    // v1.2: однократное сообщение о конфликте (вызывается при входе в мир).
    public static void AnnounceConflictOnce()
    {
        if (ConflictReason == null || _conflictAnnounced || Main.dedServ)
            return;

        _conflictAnnounced = true;
        _mod?.Logger.Warn("Hardware cursor is off: conflicting mod detected (" + ConflictReason + "). Vanilla cursor is used.");
        try
        {
            Main.NewText("Engine Optimizer: hardware cursor OFF - " + ConflictReason + " detected (cursor mod conflict)", 255, 140, 60);
        }
        catch
        {
        }
    }

    private static void Deactivate()
    {
        _active = false;
        _signature = ulong.MaxValue;
        _pixelHash = ulong.MaxValue;
        _lastRefresh = 0;
        _interfaceOverride = -1;
        _interfaceOverrideFrame = -1;

        if (_cursor != IntPtr.Zero)
        {
            try
            {
                // v0.8.10: возвращаем стандартную системную стрелку, а не
                // NULL: SDL_SetCursor(NULL) на Windows может оставить
                // «никакой» курсор, а SDL_FreeCursor по активному курсору
                // вешает висячий указатель. Стрелку кешируем на всё время
                // работы процесса (один общий системный курсор).
                if (_defaultCursor == IntPtr.Zero)
                    _defaultCursor = SDL.SDL_CreateSystemCursor(SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_ARROW);
                if (_defaultCursor != IntPtr.Zero)
                    SDL.SDL_SetCursor(_defaultCursor);
            }
            catch
            {
            }

            try
            {
                SDL.SDL_FreeCursor(_cursor);
            }
            catch
            {
            }
        }

        _cursor = IntPtr.Zero;

        if (Main.instance != null)
        {
            try
            {
                Main.instance.IsMouseVisible = false;
            }
            catch
            {
            }
        }
    }

    private static void DisposeTarget()
    {
        if (_target != null)
        {
            try
            {
                _target.Dispose();
            }
            catch
            {
            }
        }

        _target = null;
        _pixels = null;
        _bindings = null;
    }

    private static void Fail(string message, Exception exception)
    {
        _failures++;
        _retryAt = PerfData.Now + Stopwatch.Frequency * 3;
        Deactivate();
        DisposeTarget();

        long now = PerfData.Now;
        bool finalFailure = _failures >= 3;
        if (finalFailure)
            _failed = true;

        // Не заливаем лог: не чаще одного сообщения в 10 секунд.
        if (now - _lastErrorLog > Stopwatch.Frequency * 10)
        {
            _lastErrorLog = now;
            string suffix = exception != null ? " " + exception.GetType().Name + ": " + exception.Message : "";
            if (finalFailure)
            {
                _mod?.Logger.Warn("Smooth cursor disabled after repeated errors, using the vanilla cursor. " + message + suffix);
                if (!_offAnnounced)
                {
                    _offAnnounced = true;
                    try
                    {
                        if (!Main.dedServ)
                            Main.NewText("Engine Optimizer: hardware cursor disabled (" + message + "), vanilla cursor in use", 255, 140, 60);
                    }
                    catch
                    {
                    }
                }
            }
            else
                _mod?.Logger.Warn("Smooth cursor error (will retry). " + message + suffix);
        }
    }
}
