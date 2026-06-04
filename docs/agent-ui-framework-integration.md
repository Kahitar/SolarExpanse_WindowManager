# Solar Expanse UI Framework Integration Guide for Agents

This document is for AI agents implementing or migrating Solar Expanse UI mods that should use `SolarExpanse.UIFramework` as their base UI mod.

The framework owns the shared notification-area button group and window shell. Dependent mods register a window and build only the content inside that window.

## Current Compatibility

- Framework plugin GUID: `com.mod.solarexpanse.uiframework`
- Framework assembly: `SolarExpanse.UIFramework.dll`
- Public namespace: `SolarExpanse.UIFramework`
- Current version: `1.4.0`
- Target framework: `net472`
- Runtime: BepInEx plugin for Solar Expanse

## What the Framework Provides

- A shared button group next to the game's notification UI.
- Drag-and-drop repositioning for the whole button group.
- Open framework windows move with the button group while the user drags it.
- The button group automatically remains visible and draggable across live game-window and canvas resizing.
- One compact button per registered mod window.
- Game-styled dark beveled button visuals with active state.
- Optional anti-aliased TextMeshPro status dot, blinking critical state, and small status text.
- Game-styled window shell cloned from the notification history panel.
- Resizable windows with minimum size enforcement.
- Window clamping to the canvas when the canvas size changes.
- Topmost/focus ordering for multiple framework windows.
- ESC handling that closes the topmost framework window before opening the pause screen.

Dependent mods should not recreate those features.

## Add the Framework Reference

In the dependent mod `.csproj`, add a project reference to the framework and keep `Private="false"` so the dependent mod does not copy a second framework DLL into its output:

```xml
<ItemGroup>
  <ProjectReference Include="../SolarExpanse_WindowManager/SolarExpanse.UIFramework.csproj" Private="false" />
</ItemGroup>
```

The framework mod itself must be built and installed into `BepInEx/plugins` as `SolarExpanse.UIFramework.dll`.

## Declare the Runtime Dependency

In the dependent mod plugin class, add a hard BepInEx dependency:

```cs
using BepInEx;
using SolarExpanse.UIFramework;

[BepInPlugin("com.mod.solarexpanse.example", "Example Mod", "1.0.0")]
[BepInDependency(UiFrameworkPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    private IUiWindowHandle _window;

    private void Awake()
    {
        _window = ExampleUi.Register(Logger);
        Logger.LogInfo("Example Mod loaded");
    }
}
```

Do not add a dependent-mod Harmony patch just to inject a notification button or window. The framework patches `NotificationManager.Awake` and realizes registered windows when the game's notification UI exists.

## Register a Window

Register windows from the dependent mod's `Awake` method or equivalent startup path:

```cs
using BepInEx.Logging;
using SolarExpanse.UIFramework;
using UnityEngine;

internal static class ExampleUi
{
    internal static IUiWindowHandle Register(ManualLogSource log)
    {
        return SolarExpanseUi.RegisterWindow(new UiWindowRegistration
        {
            Id = "com.mod.solarexpanse.example.main",
            DisplayName = "Example",
            Order = 40,
            Icon = BuildIcon(),
            IconTint = Color.white,
            DefaultWindowSize = new Vector2(720f, 300f),
            MinimumWindowSize = new Vector2(500f, 180f),
            BuildContent = context => BuildContent(context, log),
            OnOpen = context => Refresh(context),
            OnClose = context => { },
        });
    }
}
```

Registration requirements:

- `Id` must be unique across all framework-based mods.
- `DisplayName` is required and is used for framework context and diagnostics.
- `Icon` is required. Use a compact sprite that remains readable at about `28x28`.
- `BuildContent` is required. It is called once when the game UI is realized.
- `Order` controls button ordering. Lower numbers appear earlier in the shared button group.
- `DefaultWindowSize` defaults to `720x300` if missing or invalid.
- `MinimumWindowSize` defaults to `500x180` if missing or invalid.

The framework accepts registrations before `NotificationManager.Awake`; it stores them and realizes them later.

## Build Window Content

Build all mod-owned UI under `context.ContentRoot`.

```cs
private static void BuildContent(UiWindowContext context, ManualLogSource log)
{
    GameObject content = new GameObject("ExampleContent", typeof(RectTransform));
    content.transform.SetParent(context.ContentRoot, false);

    RectTransform rt = content.GetComponent<RectTransform>();
    rt.anchorMin = Vector2.zero;
    rt.anchorMax = Vector2.one;
    rt.offsetMin = Vector2.zero;
    rt.offsetMax = Vector2.zero;

    var panel = context.WindowObject.AddComponent<ExamplePanel>();
    panel.WindowHandle = context.Handle;
    panel.Font = context.Font;
    panel.Log = log;
}
```

Use `context.Font` for TextMeshPro labels when possible. It is discovered from the game's notification UI so dependent windows match the surrounding game UI.

Useful context properties:

- `context.Id`: registered window ID.
- `context.DisplayName`: registered display name.
- `context.Canvas`: game canvas hosting the framework UI.
- `context.Font`: discovered TextMeshPro font, or `null` if discovery failed.
- `context.WindowObject`: root GameObject for the framework window.
- `context.WindowRect`: RectTransform for the framework window.
- `context.ContentRoot`: RectTransform where dependent content belongs.
- `context.Handle`: window handle for open/close/status actions.
- `context.Log`: framework log source.

Do not destroy, replace, or re-anchor `context.WindowObject` or `context.ContentRoot`. The framework uses them for sizing, clamping, focus, and lifecycle behavior.

## Button Status

Use `IUiWindowHandle.SetButtonStatus(...)` to display summary state on the button:

```cs
context.Handle.SetButtonStatus(new UiButtonStatus
{
    DotVisible = true,
    DotColor = Color.green,
    Blink = false,
    Text = "12",
});
```

For warnings or critical states:

```cs
windowHandle.SetButtonStatus(new UiButtonStatus
{
    DotVisible = true,
    DotColor = isCritical ? new Color(1f, 0.2f, 0.2f) : new Color(1f, 0.6f, 0f),
    Blink = isCritical,
    BlinkIntervalSeconds = 0.5f,
    BlinkOffColor = isCritical ? new Color(0.10f, 0f, 0f, 1f) : (Color?)null,
    Text = count > 0 ? count.ToString() : null,
});
```

Status behavior:

- `DotVisible=false` hides the dot.
- `DotColor` defaults to white if omitted.
- `Blink=true` dims and restores the dot repeatedly.
- `BlinkIntervalSeconds` defaults to `0.5` if omitted or invalid.
- `BlinkOffColor` optionally replaces the default dimmed off phase with an explicit color.
- Repeating an identical status update preserves the current blink phase.
- `Text` is optional and should be very short.
- `TextColor` is optional; omitted text uses the framework's muted status color.

The shared button group is the movement handle for framework windows. While the user drags the group, every currently open registered window receives the dock's actual clamped movement delta and then clamps individually to the canvas. Dependent mods do not need to implement their own window-follow behavior.

## Updating While Closed

Window content is inactive while its window is closed. If the mod needs to keep its button status current while closed, attach a small updater to an always-active object under `context.Canvas`, not inside the inactive window content.

```cs
GameObject updaterGO = new GameObject("modExampleUpdater");
updaterGO.transform.SetParent(context.Canvas.transform, false);
var updater = updaterGO.AddComponent<ExampleUpdater>();
updater.Panel = panel;
```

Keep updater work lightweight. Tracker-style mods usually refresh on a timer instead of every frame.

## Migration Checklist from a Standalone UI Mod

When moving an existing mod to this framework:

1. Add the framework project reference with `Private="false"`.
2. Add the hard `BepInDependency` on `UiFrameworkPlugin.PluginGuid`.
3. Replace custom notification-button injection with `SolarExpanseUi.RegisterWindow(...)`.
4. Move panel construction into `BuildContent` and parent it under `context.ContentRoot`.
5. Put persistent status refreshers under `context.Canvas` if they must run while the window is closed.
6. Replace custom button indicators with `SetButtonStatus`.
7. Delete custom shared-button drag code.
8. Delete custom window resize code.
9. Delete custom pause-screen ESC suppression code.
10. Delete mod-owned `NotificationManager.Awake` patches used only for UI injection.
11. Build the framework first, then build the dependent mod with `mise run build`.
12. Scan for stale references such as `NotificationManager`, `PauseScreenEscPatch`, `DraggableMover`, `ResizeHandle`, and old patch class names.

Keep Harmony only for actual game-behavior patches unrelated to framework window injection.

## Styling Guidance

Framework windows are intended to match the game's compact dark UI. Dependent mods should follow these conventions:

- Use dense, readable panels rather than large marketing-style or web-style layouts.
- Prefer dark translucent backgrounds, subdued borders, and small uppercase tab labels when matching tracker-style UI.
- Avoid white or bright rectangular buttons near the framework button group.
- Use compact generated sprites or existing game sprites for button icons.
- Keep status text short enough to fit inside the button.
- Use the framework's shell, resize handle, and button active state instead of custom outer chrome.

## Public API Reference

### `SolarExpanseUi`

```cs
public static IUiWindowHandle RegisterWindow(UiWindowRegistration registration);
public static bool UnregisterWindow(string id);
public static bool TryGetWindow(string id, out IUiWindowHandle handle);
```

Use `RegisterWindow` once per framework window. Keep the returned handle if the plugin needs to open, close, or update status later.

Use `UnregisterWindow` only if the mod supports teardown or dynamic window removal. Normal BepInEx mods usually register once at startup.

Use `TryGetWindow` when one component needs to find an already-registered window by ID.

### `UiWindowRegistration`

```cs
public sealed class UiWindowRegistration
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public int Order { get; set; }
    public Sprite Icon { get; set; }
    public Color? IconTint { get; set; }
    public Vector2 DefaultWindowSize { get; set; }
    public Vector2 MinimumWindowSize { get; set; }
    public Action<UiWindowContext> BuildContent { get; set; }
    public Action<UiWindowContext> OnOpen { get; set; }
    public Action<UiWindowContext> OnClose { get; set; }
}
```

### `UiWindowContext`

```cs
public sealed class UiWindowContext
{
    public string Id { get; }
    public string DisplayName { get; }
    public Canvas Canvas { get; }
    public TMP_FontAsset Font { get; }
    public GameObject WindowObject { get; }
    public RectTransform WindowRect { get; }
    public RectTransform ContentRoot { get; }
    public IUiWindowHandle Handle { get; }
    public ManualLogSource Log { get; }
}
```

### `IUiWindowHandle`

```cs
public interface IUiWindowHandle
{
    string Id { get; }
    bool IsRealized { get; }
    bool IsOpen { get; }
    UiWindowContext Context { get; }
    void Open();
    void Close();
    void Toggle();
    void BringToFront();
    void SetButtonStatus(UiButtonStatus status);
}
```

`Context` is `null` until the framework has realized the window. Code that runs before the game UI exists should not assume `Context` is available.

### `UiButtonStatus`

```cs
public struct UiButtonStatus
{
    public bool DotVisible;
    public Color DotColor;
    public bool Blink;
    public float BlinkIntervalSeconds;
    public Color? BlinkOffColor;
    public string Text;
    public Color? TextColor;
}
```

## Build and Test Commands

From the framework directory:

```sh
mise run build
mise run package
```

From each dependent mod directory:

```sh
mise run build
```

After migration, useful stale-reference scans include:

```sh
rg -n "NotificationManager|PauseScreenEscPatch|DraggableMover|ResizeHandle|IPointer|EventSystem" .
```

Some dependent mods may still use Harmony or `NotificationManager` for unrelated behavior. Inspect matches before deleting code.

## Agent Compatibility Changelog

This changelog is for agents updating dependent mods to newer framework versions. Add entries here whenever a framework release changes integration behavior or requires dependent mod changes.

### 1.4.0

Transient invalid canvas layouts no longer clear the user's saved dock placement. User-positioned docks preserve their offsets from the nearest horizontal and vertical viewport edges, and late rendered-viewport changes trigger placement restoration.

Dependent mod action:

- No dependent-mod code changes are required.

### 1.3.0

The shared button dock now clamps its actual transformed bounds against the rendered canvas viewport immediately before rendering and automatically recovers invalid or fully off-screen positions. Automatic resize clamping is temporary and does not overwrite the user's saved relative dock position.

Dependent mod action:

- No dependent-mod code changes are required.
- Do not add custom screen-resize recovery or dock-clamping logic.

### 1.2.0

Status dots now use the same TextMeshPro `●` glyph style as tracker row indicators, and blinking runs directly on each visible button dot.

Dependent mod action:

- No dependent-mod code changes are required.
- Continue using `UiButtonStatus.Blink`, `BlinkIntervalSeconds`, and optional `BlinkOffColor`.

### 1.1.0

Added reliable two-color status blinking and linked dock/window dragging.

Dependent mod action:

- No change is required for mods that already use `UiButtonStatus.Blink`; repeated status refreshes now preserve the active blink phase.
- Mods that need an explicit off-phase color can set the additive nullable `UiButtonStatus.BlinkOffColor` field.
- Do not add dependent-mod window-follow drag logic. The framework now moves all open registered windows with the shared button group.
- Use `../SolarExpanse_WindowManager/SolarExpanse.UIFramework.csproj` for workspace project references after the base mod directory rename.

### 1.0.0

Initial public framework API.

Dependent mod action:

- Add a `ProjectReference` to `../SolarExpanse_WindowManager/SolarExpanse.UIFramework.csproj` with `Private="false"`.
- Add `[BepInDependency(UiFrameworkPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]`.
- Register windows with `SolarExpanseUi.RegisterWindow`.
- Move UI construction under `UiWindowContext.ContentRoot`.
- Use `UiWindowContext.Font` for TextMeshPro labels where practical.
- Replace custom button status labels with `IUiWindowHandle.SetButtonStatus`.
- Remove standalone notification button creation, group dragging, window resize handles, and pause-screen ESC handling from dependent mods.
- Keep always-running refresh components outside inactive window content, usually under `UiWindowContext.Canvas`.

Known migrated examples in this workspace:

- `../SolarExpanse_LifeSupportTracker`
- `../SolarExpanse_FleetTracker`
- `../SolarExpanse_PowerTracker`
