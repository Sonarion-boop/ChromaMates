# ChromaMates

ChromaMates gives Town of Us: Mira a much larger color wardrobe without replacing the colors Mira already provides. The default lobby uses 252 colors, while hosts who want more can open the catalog all the way up to 3,003.

Version 1.3.10 targets Town of Us: Mira 1.7.0 and Among Us 2026.6.5. It uses MiraAPI 0.4.2 and remains compatible with the BepInEx runtime bundled with the official TOU:M 1.7.0 release.

## What it adds

- 3,003 named and selectable colors
- Separate wardrobe sections for each hue, neutrals, palettes, and pride flags
- 327 static shades in every hue section, ordered from lightest to darkest
- One animated family color at the end of each hue section - Monochrome, Porphyry, Rubicund, etc.
- Animated palettes, pride colors, Rainbow
- Host presets for 52, 100, 252 (default), 500, 1,000, 1,500, 2,000, 2,500, or all 3,003 colors
- Automatically enabled Colorblind mode so colors are easier to identify in larger lobbies.

## Using it in a lobby

The 52-color preset contains the 18 vanilla colors and TOU:M's 34 colors. A lobby using that preset does not require ChromaMates on every client. Larger presets need matching ChromaMates catalogs so everyone agrees on what each extended color ID means.

The host shares the active preset, catalog fingerprint, and animation clock only with clients that Reactor has already identified as running ChromaMates. Clients never probe an unknown host, and extended-color messages are never broadcast to clients without the mod. The handshake repairs and synchronizes color state; it does not hook or stop the Start button. A late or mismatched reply also does not turn players white, because valid colors stay renderable while the selector catches up.

If a player joins with a color outside the host's preset, the host normally chooses the closest allowed shade by comparing both the body and shadow colors. Fortegreen is kept as a hidden last-resort color at ID 3007. It does not appear in the wardrobe and is not counted among the 3,003 selectable colors. Fortegreen was chosen as the classic among us color not found color.

Color IDs 252 through 255 are left unused by the wardrobe because the network protocol reserves them. The remaining shared IDs stay in the same order no matter which host preset is selected. Optional color plugins are placed after the shared ChromaMates range so they cannot rearrange multiplayer IDs.

ALso, there is a known bug where when a player selects a color in the menu that is outside of the normal 252 color limit, it will not update their wardrobe preview. I have spent like 2 days trying to bugfix this, it's the entire reason the mod is 1.3.9 instead of 1.3.0 for the update to the most recent among us version.

## Outside a lobby

The main-menu wardrobe always shows the full catalog. Among Us normally stores a color in a one-byte account field, which is too small for an ID above 255, so ChromaMates keeps the full preferred ID in its own local setting. It places a similar TOU:M color in the vanilla field while joining a lobby, then restores the preferred extended color once the host and client agree on the catalog.

## Animated colors

Animation frames are rendered locally from a shared lobby clock. Clients send the color ID and clock epoch instead of a stream of frame-by-frame messages. This keeps animated colors in step without adding constant network traffic.

## Expanded Lobbies!

Due to the mod allowing for substantially more color options, the host can drastically increase the max size of a lobby to adjust as needed. This'll also work with the planned future in-game queue mod I am working on, but it'll be designed so that they're encouraged to be used together, and the queue mod's settings will overwrite the settings in this one.

## Further planned features!

Custom Name Colors - Mira Friendly!

One of the biggest issues with colored names is when roles like Snitch are involved, whose roles change the color of a player's name. How this is planned is that players will still be able to make custom names for themselves, however the hex codes will be overwritten when these effects are applied, thereby allowing players to more effectively see things in game while still allowing them to have their favorite colors and details in the game.

## Source layout

- `ColorCatalog.cs` builds the catalog, names, presets, and animation frames.
- `ColorSelectorTabs.cs` lays out the wardrobe and updates its previews.
- `ColorAvailability.cs` decides which colors may be selected in the current lobby.
- `ColorNetwork.cs` handles extended-color requests and catalog messages.
- `ColorSynchronization.cs` keeps the host and clients on the same catalog.
- `RemoteModCompatibility.cs` confirms remote support before any custom packet is sent.
- `AnimatedColorRendering.cs` draws animated colors on players and UI elements.
- `ColorNameData.g.cs` contains generated lookup data and should not be edited by hand.

Build the release DLL with:

```powershell
dotnet build -c Release
```

Run the source checks with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\InvariantTests.ps1
```

## Credits

ChromaMates is designed and maintained by Sonarion for the Town of Us: Mira ecosystem.

Town of Us: Mira, MiraAPI, Reactor, and BepInEx provide the modding foundation this project builds on. The bundled color-name lookup is derived from Meodai's `color-names` list under the MIT license. The old ColorsPlus release was used as a historical reference for large color wardrobes; its loader and DLL code are not included.

Among Us is owned by Innersloth. ChromaMates is an unofficial community mod.
