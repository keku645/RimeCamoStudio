# Rime Shader Editor

A node editor for Battlefield 3 shaders, built on Rime.

It reads a shader out of an installed copy of the game, shows it as a node graph, lets you change it or
author a new one, previews the result on the actual object that wears it, and bakes it into a Venice
Unleashed mod. The game's own shader files are never touched: the install is opened read-only and
everything ships as an additional mod.

---

## Requirements

| | |
|---|---|
| Windows + .NET 8 desktop runtime | the editor is WPF |
| A Battlefield 3 install | read-only; the editor never writes into it |
| Venice Unleashed | to run what you bake |
| `RimeREPL.exe` **built from this repository** | the editor drives it for everything that touches the game |
| `fxc.exe` (Windows 10/11 SDK) | compiles the graph's HLSL. Found automatically under `Windows Kits\10\bin\<version>\x64` |
| `texconv.exe` *(optional)* | only for baking your own textures. Put it next to `RimeREPL.exe` or next to the editor |

`RimeREPL.exe` is looked for at `<folder>/bin/Release/RimeREPL.exe`, walking up to six folders above the
editor's own — so a normal build of this repository is found with no configuration.

**It has to be this repository's RimeREPL.** A bake drives `shader_db_slice`,
`dump_shader_material_textures`, `mvdb_add_entry`, `add_json_partition`, `add_existing_partition`,
`add_dds_texture`, `replace_resource_as` and `resolve_resource_dependencies`. A RimeREPL without them fails
partway through a build, after minutes of mounting.

Building the editor:

```
dotnet build RimeShaderEditor/RimeShaderEditor.csproj -c Release -p:SolutionDir="<path to Rime-src>/"
```

(The trailing slash matters.)

---

## First run

The first time it opens, the editor asks for two folders and nothing else:

* **Game folder** — where it reads shaders, textures and meshes from. Guessed from the retail installer's
  registry key. Read-only, always.
* **Cache and output folder** — where everything it dumps and everything it builds goes: texture thumbnails,
  mesh dumps, per-level shader lists, saved graphs and baked mods. It grows to gigabytes. Defaults to
  `Documents\RimeShaderEditor`.

Both are prefilled, and a game folder with no `bf3.exe` and no `Data` folder is flagged — as a warning, not
a wall. Both can be changed at any time in **Settings ▸ Folders**, and take effect immediately.

---

## Opening a shader

1. Pick a map in **Game shaders** and press **Load**. The first time for a given map this mounts the game,
   which takes minutes; afterwards the list is cached and instant.
2. Type in the filter box to narrow the tree.
3. **Double-click a shader.** That translates its bytecode into a graph *and* loads its textures — the whole
   thing, in one action. It opens as a tab; several shaders can be open at once, each with its own graph,
   target, textures and undo history.

**Reload from game** repeats that read for the open tab. Use it as a retry, or after clearing the texture
cache — it is not a step you need in the normal flow.

The **Preview** panel draws the shader live. The shape icons switch between primitives and, with the mesh
icon, the real objects in the map that wear this shader. **Linked objects** picks which object's texture set
is being previewed.

---

## The two ways a shader reaches the game

This is the part worth reading before your first bake. Which one you need is not a preference — it is
decided by how many objects in the map wear the shader you are editing.

### 1. The shader belongs to one object → replace it

If only one mesh in the map uses the target shader, bake it as a plain **replacement**: leave the
**Variation** dropdown on *(none — bake replaces the target)* and bake.

The mod ships its own small shader database holding just that one entry, registered after the level's own.
The lookup walks the registered databases backwards, so the mod's answer is the one that is used. Nothing
vanilla is overwritten — remove the mod and the map is exactly as it was.

### 2. The shader is shared by many objects → aim a variation at one of them

Plenty of shaders are presets worn by dozens of objects (`Objects/Shaders/PropPreset` dresses 81 different
meshes). A replacement answers for the shader's **name**, so it would repaint every one of them.

To change a single object:

1. In the **Preview** panel, pick that object under **Linked objects** — the pick is what the bake is aimed
   at, and it is saved with the graph.
2. In the **Variation** section, choose **New variation…** and give it a name.
3. Tick **"Use this on the objects already in the level"**.

The bake then clones the shader under a fresh name, ships a mesh-variation entry keyed by *(mesh, variation)*
and repoints the copies already placed in the map at it. Every other user of the preset is untouched.

Untick that box for a variation you intend to place by hand in a map editor instead: it then ships as an
extra variation and the map is left alone.

### Which of the two am I in?

You do not have to guess and you do not have to count. The caption next to the checkbox says it, for the map
the target belongs to:

> *(81 different meshes wear this shader in MP_017 — a replacement would repaint ALL of them, so aiming at
> one object needs a variation with this ticked)*

If the number has not been measured yet, press **Reload from game**. And the bake checks it again for every
map you actually build: a replacement aimed at a shared shader is **refused**, with the count, the names of
the first few objects wearing it, and what to do instead. If repainting all of them is genuinely what you
want, set `RSE_REPLACE_SHARED=1` and bake again.

### Several shaders in one mod

**Add saved graphs…** in the bake dialog takes any number of saved `.json` graphs and bakes them into the
same mod. Each keeps its own target and its own variation, and each file remembers which object it was aimed
at, so nothing has to be re-picked.

Replacements and take-overs mix freely: the mod ships two databases, one registered on each side of the
level's own, because the two kinds need opposite orders. Two graphs delivering the *same* target and the
*same* variation are refused when you add them — they would silently fight over one entry.

The one combination that cannot share a mod is a take-over (**"use this on the objects already in the
level"** ticked) together with a hand-placed variation (unticked). They ride in the same bundle, which a
take-over forces to load before the level's own — where the hand-placed one's entry points at a mesh that
is not loaded yet and kills the client as the level starts. The bake refuses it and tells you to split it
into two mods.

---

## Baking

**Bake shader…** asks for:

* **Maps** — one superbundle per map. A map whose database does not hold the target shader is reported and
  skipped, not baked empty. Each map means mounting the game again, so the dialog says up front how long the
  run will be.
* **Mod name** and **bundle name** (the bundle becomes `Win32/<bundle>/<map>`).
* **Where to save it.**
* **Additive database** (on by default) — ships a few hundred KB holding only this mod's own keys, instead of
  a regenerated copy of the level's whole 27.9 MB database. Two mods built this way install side by side
  without being merged. Leave it on unless you have a reason not to.

The result is a normal VU mod:

```
<output>/<ModName>/
    mod.json
    ext/Shared/__init__.lua      generated loader: registers the mod's bundles in the order they need
    sb/…                         one superbundle per map
```

**Installing:** copy `<ModName>` into your VU `Mods` folder (`Documents\Battlefield 3\Server\Admin\Mods`)
and add a line `<ModName>` to `ModList.txt` — with no `#`, or it stays disabled.

---

## When a bake refuses

Refusals happen before the expensive work, and each one names the fix:

* **worn by N different meshes** — a replacement aimed at a shared shader. Bake a variation (see above), or
  set `RSE_REPLACE_SHARED=1` if repainting all of them is the point.
* **no object is stamped** — a take-over with no object picked. Open **Linked objects** and pick one: a
  shared shader can be worn by meshes that are not even loaded when the level bundle is, and an entry
  registered against one of those crashes the load.
* **matched several shaders but none exactly** — the target name is ambiguous in that map. Use the full path
  from the shader tree.
* **mixes a variation that takes over … with one meant to be placed by hand** — split it into two mods.

---

## Environment switches

* `RSE_REPLACE_SHARED=1` — bake a replacement over a shader that many objects share, repainting all of them.
  The only switch meant for normal use, and only ever as a deliberate answer to the refusal above.

The rest (`RSE_DIAG_NO_MVDB`, `RSE_SETTINGS`, `RSE_CONTRACT_DXBC`) exist for the test seams below and for
diagnosing the bake; they are not part of the supported surface.

---

## For developers

The editor runs headless for its own tests. Each of these is a `--switch` on the executable:

| | |
|---|---|
| `--nodetest`, `--udkblendtest`, `--udktwintest`, `--udksurfacetest` | node emission and the UDK-vocabulary palette |
| `--luatest`, `--stubtest` | the generated loader's bundle order, and the variation stub |
| `--tabtest`, `--settingstest`, `--customtextest` | editor state: tabs, settings, custom textures |
| `--selftest` | emits, re-reads the JSON, re-emits and compares byte for byte |
| `--windowshot`, `--settingsshot`, `--firstrunshot`, `--bakeshot` | render a window to a PNG without showing it |

The ones that mount the game (`--selecttest`, `--translatetest`, `--textures`) take minutes each; the rest
run in seconds.
