# Solar Expanse Window Manager (SEWM)

A unified UI framework and windowing system for *Solar Expanse*. 

**Solar Expanse Window Manager** acts as a central hub for UI mods. Instead of every mod cluttering your screen with disjointed buttons, custom windows, and conflicting hotkeys, SEWM consolidates them into a single, clean, draggable button group and provides a standardized, high-performance window shell that matches the game's native aesthetic.

*This is a base utility mod. It does nothing on its own unless you have other mods installed that use it as a framework.*

---

## Features

### For Players
*   **Unified Button Dock:** Consolidates all mod shortcuts into a compact button group right next to the game's notifications.
*   **Fully Clean & Draggable:** Drag and reposition the button dock anywhere on your screen. Open mod windows follow the dock while it moves, your layout remains saved, and the dock automatically stays reachable when the game window is resized.
*   **Consistent Window Shells:** Every mod window uses the same dark, beveled, native-looking frames with resize handles.
*   **Smart ESC Key Behavior:** Pressing `ESC` closes the topmost mod window first rather than immediately opening the game's pause menu.
*   **Focused Window Ordering:** Click any window to bring it to the front. Windows automatically clamp to your screen when changing resolution.
*   **Rich Status Indicators:** Mod buttons can display active states, anti-aliased notification dots, warning blinks, and status text.

### For Developers / Modders
*   **Zero Shell Boilerplate:** Register your window size, icon, and callbacks, and let SEWM handle the rest.
*   **Standardized Layouts:** Focus entirely on your mod's core layout inside a pre-built, performance-optimized `ContentRoot` canvas.
*   **Native Font Discovery:** Automatically inherits the game's TextMeshPro fonts for perfect aesthetic alignment.

---

## Installation (Players)

1.  Ensure you have **BepInEx** (v5.x) installed for *Solar Expanse*.
2.  Download the latest release of `SolarExpanse.UIFramework.dll`.
3.  Place the execution DLL inside your game's plugin directory:
    ```text
    Solar Expanse/BepInEx/plugins/SolarExpanse.UIFramework.dll
    ```
4.  Launch the game.

---

## Developer Quick Start

To use Solar Expanse Window Manager as the base for your UI mod, follow these steps. For advanced implementations, please read the [Agent Integration Guide](docs/agent-ui-framework-integration.md) (or pass it to your AI code assistant).

### 1. Add Reference to your .csproj
Add a project reference to the framework DLL, making sure to set `Private="false"` so the assembly is not duplicated in your build output.

```xml
<ItemGroup>
  <ProjectReference 
    Include="../SolarExpanse_WindowManager/SolarExpanse.UIFramework.csproj"
    Private="false" 
  />
</ItemGroup>
```

### 2. Declare BepInDependency
Require the Window Manager in your main plugin class:

```cs
using BepInEx;
using SolarExpanse.UIFramework;

[BepInPlugin("com.mod.solarexpanse.mymod", "My Mod", "1.0.0")]
[BepInDependency(
    UiFrameworkPlugin.PluginGuid, 
    BepInDependency.DependencyFlags.HardDependency
)]
public class MyModPlugin : BaseUnityPlugin
{
    private IUiWindowHandle _window;

    private void Awake()
    {
        _window = MyModUi.Register();
    }
}
```

### 3. Register your Window shell
Instantiate your UI window during your mod's startup using `SolarExpanseUi.RegisterWindow`:

```cs
using SolarExpanse.UIFramework;
using UnityEngine;

internal static class MyModUi
{
    internal static IUiWindowHandle Register()
    {
        return SolarExpanseUi.RegisterWindow(new UiWindowRegistration
        {
            Id = "com.mod.solarexpanse.mymod.main",
            DisplayName = "My Mod Panel",
            Order = 50,
            Icon = MySpriteLoader.LoadIcon(),
            DefaultWindowSize = new Vector2(600f, 400f),
            BuildContent = context => {
                // Instantiate your custom panels inside context.ContentRoot
                GameObject panel = new GameObject("MyContent");
                panel.transform.SetParent(context.ContentRoot, false);
            }
        });
    }
}
```

---

## Build from Source

This project uses `mise` to automate builds.

Compile the framework assembly:
```bash
mise run build
```

Package the release containing installation folders:
```bash
mise run package
```

---

## License

This project is licensed under the MIT License. Feel free to use this in any of your *Solar Expanse* UI modding projects!
