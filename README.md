# Rime Camo Studio

Custom camouflages for Battlefield 3 on Venice Unleashed: weapons, their attachments, vehicles and soldiers.

Pick what you want to paint, give it a camo — an image with its tiling and wear, or the full node graph of the
shader underneath — place stickers and the emblem where you want them, see the result on the real model, and
bake it. The bake drops a ready package into the **CamoFramework** mod, which shows your camos in the game's own
customization menus.

> **You need both:** Rime Camo Studio makes the camos, CamoFramework (a separate repository) puts them in the
> game. Players on a server need the framework mod with your camos in it.

---

## Download

Get `RimeCamoStudio.exe` from the **Releases** page of this repository. It is the whole tool in one file.

---

## Requirements

| | |
|---|---|
| Windows 10 or 11, 64-bit | |
| Battlefield 3, installed | read only: the studio never writes a byte into the game |
| Venice Unleashed | to play with what you bake |
| CamoFramework | the mod a bake drops its camos into |
| About 4 GB of disk | for the cache the studio reads out of your game |

Nothing else: the release is **one executable** with everything it needs inside (the .NET runtime, the Rime
tools that read the game, the shader compiler and texconv). No .NET install, no Windows SDK, no build of Rime.

**Nothing of Battlefield 3 ships with the studio.** Every model, texture and shader it shows is read out of
*your* copy of the game, on your machine.

---

## Getting started

1. Put `RimeCamoStudio.exe` in any folder and run it.
2. The first time it asks for two folders:
   * **Game folder** — your Battlefield 3 install (pre-filled when the installer registered it).
   * **Cache folder** — where everything read out of the game and everything built goes
     (defaults to `Documents\RimeShaderEditor`).

   Both can be changed later in **Settings ▸ Folders**.
3. **Prepare from your game.** Before it can show anything the studio reads the weapons, attachments, vehicles
   and soldiers out of your game into the cache, once: their models, materials, textures and shaders, and the
   node graphs it edits. The window lists each step; count on about an hour the first time. You can stop and
   come back later — what is done stays done, and the studio offers the rest the next time it opens.
4. The studio opens.

The first time a bake runs it asks for your **CamoFramework** folder (the one holding its `mod.json`); the
studio registers every package it bakes there itself.

---

## Making a camo

* **Weapons / Vehicles / Soldiers** — pick the family, then the subject. The preview shows it as it ships;
  the attachment picker shows a weapon's sights, grips and suppressors.
* **Simple mode** — an image, its tiling and its wear. Or work on the **node graph** of the shader directly:
  both reach the same place.
* **Stickers** — images and animated GIFs placed on the model, and the **emblem** square (the player's own
  emblem, drawn where you put it). The "1st person" view shows the weapon as its owner sees it.
* **Save camo… / Open camo…** — keeps a camo ready to bake, nodes, pictures and stickers included, as a
  `.camo.json` file with a `<name>_files` folder beside it; opening it brings back the subject it was saved on.
* **Bake** — choose the weapons, vehicles or soldiers that get the camo; the package lands in CamoFramework.

---

## Where things are kept

| | |
|---|---|
| Settings | `%LOCALAPPDATA%\RimeShaderEditor\settings.json` |
| Cache (game data read once, previews, translations) | the cache folder you chose |
| The tools inside the executable | unpacked by .NET to `%TEMP%\.net\RimeCamoStudio\` on first start |

---

## Rime

Camo Studio is built on **Rime**, the Frostbite modding toolkit. Rime itself lives in its own repository:

* **https://github.com/keku645/Rime** (branch `terraindecals-json`) — the Rime the studio is made with: RimeREPL, the
  tool that reads the game, and the Frostbite 2 libraries it loads, with the commands the studio drives.
* It is a fork of **https://github.com/ModdersLink/Rime**.

This repository holds the studio's own source (`RimeCamoStudio/`) and the source of the **Rime Shader Editor**
(`RimeShaderEditor/`), the node editor the studio compiles in. The release executable carries a RimeREPL built
from the Rime repository above.

---

## Building from source

Requirements: the .NET 8 SDK, a checkout of Rime (see above), and `texconv.exe` from
[DirectXTex](https://github.com/microsoft/DirectXTex/releases).

* **The release executable**

  ```
  RimeCamoStudio\publish-release.ps1 -Rime <folder of the Rime checkout> -Texconv <path of texconv.exe>
  ```

  It builds RimeREPL self-contained with every Frostbite 2 library it loads, and publishes the studio as one
  self-contained file: `RimeCamoStudio\bin\publish\RimeCamoStudio.exe`.

* **A build to work on**

  ```
  dotnet build RimeCamoStudio/RimeCamoStudio.csproj -c Release
  ```

  The studio drives RimeREPL for everything that reads the game. Build it in the Rime checkout (its
  `build-release.ps1` puts RimeREPL and its libraries in `dist\`), copy that folder's content into
  `RimeCamoStudio\bin\Release\net8.0-windows\rime\`, and put `texconv.exe` beside `RimeREPL.exe` there.

### What the `Data` folder holds

Only what is not the game's: catalogues of names and hashes (which weapons, attachments, vehicles and soldiers
exist and where the game keeps them), the emblem squares placed by hand on each weapon and vehicle, and the
starting documents of a few subjects (the sound suppressor, some vehicles, some soldiers' legs). Those starting
documents are kept as the **changes** made to the shader's graph and are applied to the graph the user's own
preparation translates; a mask made of the game's own textures is kept as its **recipe** (which textures, and the
hand edit on top) and made in the user's cache.

---

## Notes

* Not affiliated with or endorsed by Electronic Arts or DICE. Battlefield 3 and its assets belong to their owners.
* `RimeCamoStudio/Data/emblem/emblem_atlas.png` (and its `.tga`) holds the emblem shapes of Battlefield 4's emblem
  editor (EA's artwork, as published on Battlelog), used so an emblem drawn with those shapes shows in the studio
  and in the game. It is not covered by this repository's terms.
* Third-party components the release carries, and their terms: see `RimeCamoStudio/THIRD-PARTY-NOTICES.txt`.

<img width="1600" height="722" alt="studio_stickers" src="https://github.com/user-attachments/assets/fd7be0e4-c973-48ba-a8d0-b80e942cc7e9" />
<img width="1600" height="722" alt="studio_graph" src="https://github.com/user-attachments/assets/ab6d601d-6684-4cf8-9fb4-f73ac89b8c73" />
<img width="2561" height="1204" alt="vehicle" src="https://github.com/user-attachments/assets/a7427c87-5800-43ad-a967-5544d4281607" />
<img width="2559" height="1205" alt="soldier" src="https://github.com/user-attachments/assets/15308cc6-a2f3-4d14-8a61-9797d8f564d5" />
