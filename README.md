# GRAB ENGINE

A Unity-style level editor and **C# → GASM compiler** for the Steam game **GRAB**.

GRAB ENGINE reads and writes GRAB's real `.level` protobuf files, uses the game's original
SGM meshes and PNG textures, and compiles C# Code Blocks into genuine GASM bytecode and
GASM object/register connections. Levels exported here load directly in the retail game.

---

## Run it

Double-click **`GRAB ENGINE.bat`**.

It rebuilds the project when the .NET 10 SDK is available, then starts `app\GRABEngine.exe`.
If no SDK is installed it launches the last published build. The .NET Desktop Runtime must
be present either way (the .NET 10 SDK is enough to build and run).

To build it yourself:

```bat
cd src\GRABEngine
dotnet build
dotnet run
```

or publish:

```bat
dotnet publish src\GRABEngine -c Release -o app
```

## Requirements

* Windows 10/11
* .NET 10 SDK / Desktop Runtime
* GRAB installed (only used while extracting the original assets; the engine runs from the
  bundled copies in `src\GRABEngine\Assets`)

## What it can do

### Editor

* Hierarchy with create, rename, duplicate, delete, drag-and-drop parenting and per-kind icons.

* 3D viewport with grid, sky, stars, clouds, sun and ambient lighting matching the level's
  ambience settings. Block faces are tessellated where a point/spot light reaches them, so
  lights fall off across a surface instead of lighting a whole block evenly.

* Real GRAB shapes: cube, sphere, cylinder, pyramid, prism, cone, square-base pyramid.

* Real GRAB materials: Default, Grabbable, Ice, Lava, Wood, Grapplable, Grapplable Lava,
  Grabbable Crumbling, Default Colored, Bouncing, Snow, Trigger. Only Default Colored is
  paintable, matching the game; every other material uses its fixed texture colour.

* Move / Rotate / Scale gizmos with Local/Global space, grid snapping, mouse orbit/pan/zoom,
  frame-selection, align min/centre/max and even distribution on X/Y/Z, linear array
  duplication, one-click mirror selection, and a two-point ruler/measure tool (M).

* Block brush with Fill, Replace, **Erase**, Line, Box and Sphere modes (B), shape/material/
  cell-size palette, vertical layers, hollow shells and live ghost previews. Each stroke is
  one undo step and is grouped in the hierarchy, so a 500-block voxel wall does not bury the
  scene. Optional X/Z/Both symmetry mirrors every stroke across a movable pivot. The **Smart
  Palette** remembers recent brush setups and lets you save favourites with **☆ Save preset**;
  clicking a chip restores its material, colour, shape, cell size, layers and hollow flag.

* Prefab library with save/spawn plus a **prefab stamp brush**: pick a prefab, move the live
  preview across the level, rotate with `[` / `]`, and click to place as many copies as you
  need. `Esc` leaves the stamp tool.

* **Prefab variants and overrides**: save a variant of any prefab instance, edit any instance
  and the Inspector lists every changed property as an override with **Revert**, **Revert All
  Overrides**, **Apply to Prefab** and **Apply to All Instances**. Internal trigger/code
  references are rebound by name so copied prefabs keep pointing at their own objects.

* **Level Library** (Tools → Level Library): scans the user's Documents levels, the GRAB
  install, Steam Workshop and a connected Meta Quest (`/sdcard/android/data/com.slindev.grab_demo/files/levels/user`
  over adb). Levels appear as thumbnail cards rendered from their real block geometry, with
  one-click Open, Add to Level (merge as a group) and Duplicate into My Levels.

* **Hierarchy search** (Ctrl+F) filters by name, kind and material; **Editor Layers** add
  visibility, locking and colour coding without writing editor-only data into a `.level`.

* **Terrain & World Builder** (Tools): heightfield/perlin terrain with raise/lower/smooth/
  flatten/**erode** brushes, painted biomes with auto-material height/slope rules, water plus a
  sandy coastline band, and a **Paint path** brush that draws roads directly on the heightmap
  (right-drag erases; brush size is the road width). No X/Z coordinates are needed. Built
  terrain appears in the Hierarchy as selectable chunk groups, and **Slope-aware scatter**
  places prefabs only on valid slope/height bands.

* **3D Terrain Shaper** (T, or the ⛰ Terrain toolbar button): a Roblox-style scene brush with
  **Add**, **Erase**, **Smooth**, **Flatten** and **Paint** modes. Choose Sphere, Box or
  Cylinder, size, strength and a **0.5 m / 1 m / 2 m grid** (fine detail, normal or chunky
  terrain), then drag directly on the terrain. Add fills a solid block volume; Erase removes a
  rounded volume and rebuilds the affected columns so the cut follows the surface instead of
  deleting whole hills; Smooth evens the top, Flatten sets a level and Paint changes
  material/colour. Strength is used by Smooth; Add/Erase/Paint write exact volumes. All results
  are ordinary GRAB blocks and remain editable with the block brush. The heightmap studio is
  still available from the palette.

* **Draw Structures** (G, or Tools → Draw Structures): drag a footprint in the scene to make a
  box, sphere, cylinder, cone, staircase or ramp, with a live block preview. **Custom Shape**
  draws a polygon point by point and extrudes it into blocks. Height is one slider (`[` / `]`
  also work), materials/colours are in the palette, and Face snap builds directly against the
  block you point at. The palette now has **Level ground** and **Carve foundation** modes that
  trim or fill the terrain under the footprint before the blocks are placed; the Structure &
  Shape Studio exposes the same setting with a carve-depth field.

* **Structure & Shape Studio** (Tools): the first tab is a click-to-draw footprint grid that
  extrudes a custom outline; no X/Z entry. Presets add stairs, ramps, spiral stairs, bridges,
  arches, pillars, doorways, towers, gable roofs, fences, platforms, frames, domes, torus/
  cylinder shells and voxel primitives. The advanced **Silhouette** section is still available
  for typed outlines.

* **Smart snapping and measuring**: Grid/Face/Edge/Centre snap modes snap a moved object to
  the actual block under the cursor. The Measure tool (M) reports distance, delta and a live
  player-clearance check; validation markers are drawn through geometry in the overlay viewport.

* **Build Validation** (Tools): checks missing spawn/finish, floating blocks, overlaps, dead
  or over-limit triggers, broken object connections, script errors, GRAB limits and finish
  reachability. Problems select the object and highlight it in the scene.

* **Level Optimizer & Budget** (Tools): reports object/complexity budgets and offers
  **Merge adjacent identical cubes**, **Merge different block sizes on a shared grid** (0.25 m,
  0.5 m, 1 m, 2 m or 4 m — useful for terrain columns and mixed-height builds), **Remove fully
  enclosed blocks**, **Remove exact duplicate blocks**, **Remove zero-size blocks** and optional
  **Remove empty groups**. Protected physics, locked and animated objects are skipped. The
  mixed-size pass rebuilds each identical material/colour group on the chosen grid, so a 1×4
  column and its 1×1 cap can become one block without changing the occupied volume; culling runs
  after the merge so a solid region can collapse first.

* **Prefab & Asset Palette** (Tools): thumbnail browser for the prefab library with Stamp,
  Spawn and Save Selection actions.

* **.glb model import** (File → Import .glb Model): glTF 2.0 binary, indexed/non-indexed
  triangles, strips/fans, node transforms, material base colours, embedded PNG/JPEG
  base-colour textures sampled through `TEXCOORD_0`, and COLOR_0 vertex colours. **Faces**
  mode rebuilds paired coplanar triangles as oriented panels and remaining triangles as
  oriented prisms; **Voxels** mode samples the surface into a cube grid. Import settings cover
  detail (fewer/finer faces or voxels), scale, panel/prism thickness, solid voxels, block
  material, model/texture/vertex colours, centring and drop-to-ground, with a real preview of
  the resulting object counts.

* **Audio Studio** (Tools → Audio Studio): edits every Sound block field with a live waveform,
  including GRAB's advanced synth parameters (envelope punch, frequency min/slide/delta slide,
  vibrato, pitch jump, square duty, repeat, flanger, low/high-pass and reverb) and previews the
  real oscillator/filter chain. The low-pass defaults to 25000 Hz, which is GRAB's "open" value;
  the exporter repairs 0 Hz cutoffs because the retail synth treats those as a closed filter and
  plays nothing. Presets: Default, Blip, Beep, Bass, Laser, Bell, Wind, Alarm, Sub Drop. With an
  empty level the synth shows a preview patch with real defaults, so presets can be auditioned
  before pressing **Add Sound to Level** to commit it. **MP3 → Sound Blocks** analyses an MP3/WAV with an FFT,
  merges pitches into a note score, and either imports the score as Start Active sound
  blocks or builds a timeline of triggers plus generated C# Code Blocks so the tune plays in
  order. The generated scheduling compiles to real GASM and works in the retail game.

  **Trigger layout** can be *Timed* (the score auto-plays at level load) or *One Start Trigger*
  (the same timed score, but every scheduler waits for one `Music Start` trigger). Touch the
  start trigger in game, or pulse its `ForceActive` from code, to begin; it can be activated
  again after the score ends to replay. The per-slice helper triggers carry no activation
  sources, so they are script-only in GRAB (exactly how the retail levels drive their own
  force-triggered sound triggers): walking on them never fires the whole bank at once. Play
  mode honours the same rule. The `Music Start` / `Speech Start` trigger is connected with
  native START targets to its scheduler Code Blocks (up to GRAB's trigger-target limit), so
  its target list is populated and it visibly drives the score; every scheduler also polls it,
  so long scores still run completely when the list is longer than the limit.

* **Text → Speech** turns typed text into a GRAB voice. An English letter-to-sound front end
  (pronunciation dictionary plus grapheme rules, number and abbreviation expansion) produces
  phonemes, and initialisms are spelled letter by letter (API → A P I, U.S.A. → U S A) while
  ordinary upper-case words like ENGINE stay words. A Klatt-style formant synth with four
  resonators, spectral tilt, continuous formant transitions and silent word gaps gives a

  **Speak** preview you can actually understand; **GRAB Preview** plays the same score through
  the real oscillator blocks that get exported. Voice (Deep/Neutral/Bright), pitch, rate,
  intonation, pauses, volume, formant count (1-4 blocks per phoneme), formant sharpness,
  vowel contrast, consonant boost, brightness, vowel length, word gap and acronym spelling
  are adjustable, and the Natural / Clear / Ultra clear / Max clarity / Robot presets set
  them all at once. Import writes formant sine blocks, a trigger per phoneme and a generated
  C# Code Block per phoneme, so the speech plays in the retail game with real GASM. It also
  has the same **One Start Trigger** layout: the full phoneme timing waits for a single
  `Speech Start` trigger, so you can start the voice whenever you like (and replay it after
  the speech ends) without every phoneme firing at once.

* **Particle authoring** matches GRAB's particle shader: particles are untextured, solid,
  camera-facing quads coloured by Start/End colour, not soft sprites. The inspector exposes
  every emitter field plus Smoke, Fire, Sparks, Snow, Portal and Magic Dust presets.

* **C# script components**: add a C# script directly to any object in the Inspector (Unity
  style), with auto-generated `MyScript` template, inline compile status, serialized public
  fields, `[Range]` / `[Header]` / `[Tooltip]` inspector fields, `[Button]` methods and the
  real lifecycle callbacks `Awake`, `OnEnable`, `Start`, `FixedUpdate`, `Update`, `LateUpdate`
  plus `OnTriggerEnter`, `OnTriggerExit`, `OnTriggerStay` and `OnSignal`.

* **C# editor intelligence**: completion for fields, methods, types, generic collections and
  scene object names; chained receiver inference (`GameObject.Find<Sign>("Sign 1").`),
  parameter hints while typing, hover documentation, **F12** go-to-definition, **Shift+F12**
  find references, **F2** rename symbol, **Ctrl+Shift+F** format document, live GASM budget
  (instructions/150, connections/10, registers), static `string` helpers, brace snippets and a
  larger autocorrect dictionary. Breakpoints (F9), step line/instruction and the Console make
  the Problems/debug workflow complete.

* **Hot reload / live edit**: compiling while Play is running swaps the new GASM programs under
  the live session and carries working registers across by name; the toolbar shows a green
  `live edit` badge. Player position, physics and level state stay put.

* Inspector for every GRAB node type: blocks, crumbling blocks, groups (physics), spawn points,
  finish points, signs, lights (point/spot), triggers with sources and targets, gravity zones,
  particle emitters (all parameters), sounds, lobby terminals, Code Blocks and animations.

* Level settings: title, creators, description, tags, version, complexity, checkpoint count,
  default spawn, unlisted, replays, practice mode.

* Full ambience editor: sky zenith/horizon, sun altitude/azimuth/size/color/brightness,
  advanced sun, fog, ambient color/brightness, stars (density/clustering/rotation/size/visibility),
  clouds (color/shading/density/height/speed).

* Animation tab in the bottom dock (alongside C# Script, GASM, Problems and Console):
  a visible Start Here workflow, clip list and settings, keyframe timeline, playhead scrubbing,
  play/pause preview, reverse, ping-pong, looping, speed, copy/paste clips and in-editor
  keyframe preview. Frame tools include + Frame, In-between, Prev/Next, Duplicate/Append,
  Lerp 50%, Bake Ease (linear / ease in / ease out / ease in-out / stop motion) and Make Step.
  Stop-motion mode captures a pose and advances the playhead by a chosen interval. **Start
  Active** matches GRAB's Animate-menu setting (on plays the selected clip on level load; off
  writes `activeAnimation = -1` and waits for a trigger or script). Opening the tab expands
  the dock automatically.

* Project save/load (`.grabproj`, JSON) with all nodes, animations and C# scripts.

* Export real `.level` (protobuf) through an export settings dialog (Debug / Release, code
  optimisation, animation baking, limits check) and **Install to GRAB** for both Steam
  (`Documents\GRAB\levels\user`) and Meta Quest (`adb push` to the game's `levels/user`).

* Play mode that actually executes the compiled GASM (WASD + Space, Esc stops and restores
  the scene), including animation clips.

* Full app-wide undo/redo (Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z) for transforms, hierarchy edits,
  every Inspector field, ambience, animation clips and C# scripts. Continuous drags and
  typing coalesce into one gesture; history is snapshot-based and also available from
  Edit → Undo/Redo and the toolbar buttons. Stop restores the scene from a play snapshot.

### C# editor

* AvalonEdit editor with line numbers, C# syntax highlighting, bracket auto-closing,
  Ctrl+Space completion, object-name completion inside `GameObject.Find(...)`, live
  error/warning squiggles, Problems list with double-click navigation.

* Compiles to GASM on every edit, on F7, before Play, Export or Install.

* Shows the generated GASM disassembly in the **GASM** tab, including optimiser statistics in
  Release exports.

* Animation API: `Animator.Play(id)` / `Stop` / `Pause` / `Resume` / `Reverse` /
  `Restart` and `Animator.IsPlaying(id)`. Each call is compiled into a real GASM trigger
  fired at the Code Block and wired to the animation's node at export; the generated trigger
  carries GRAB's real START/STOP/TOGGLE_REVERSE/RESTART target mode and is visible in the
  Hierarchy with a script badge and the clip name.

* Completion and highlighting know the full C# language, GRAB types and collection methods.

* Supports classes, structs, interfaces, inheritance, generics, `List<T>` / `Dictionary<K,V>`
  with `foreach`, `try` / `catch` / `throw`, `using`, `lock`, `goto`, `yield` and
  `async` / `await`; see [CSharpReference.md](CSharpReference.md). Scripts compile to one Code
  Block by default, even past the retail editor's 150-instruction / 10-connection caps (see
  **GRAB's real limits** below); switch **Tools → Script Compiler** to split mode to get linked
  continuation Code Blocks instead.

* Creates real scene objects from code with `new Sign("Score")`, `new Cube(Material.Wood)`,
  `new Light(...)`, `new Trigger(...)` and object initializers. Objects are baked into the
  level on compile, and `Voxel.Grid` / `Voxel.Line` / `Voxel.Ring` build voxel shapes.

* Expanded runtime helpers: `Array.Reverse` / `IndexOf` / `Copy` / `Fill` / `Clear`,
  `List.Insert` / `AddRange`, `char.*`, `Convert.*`, object `SetPosition` / `Translate` /
  `SetRotation` / `Rotate` / `SetColor`, and more `Mathf` / `Vector3` / `Color` functions.

* `Console.Write` / `WriteLine` / `Clear` log to the **Console** tab in Play mode and to a
  generated `GASM Console` Sign in the exported level, with `{0}` placeholders, numbers,
  bools, chars, vectors and colors.

* Arrays and collections are expanded into registers, lambdas and recursion are diagnosed
  instead of miscompiled. With split mode enabled in **Tools → Script Compiler**, scripts over
  GASM's editor caps become linked continuation Code Blocks automatically.

* In-editor help viewer for this README, `CSharpReference.md` and `GASMReference.md`.

### Neural Lab graph controllers

**Tools → Neural Lab…** is the general controller authoring tool, not just the demo trainer.
You can build any number of dense layers (up to four hidden layers), choose an activation,
edit every weight and bias, and connect real GRAB scene objects to the inputs and outputs.

* **Quick Start.** Pick the **Target** (the object the network should chase) and the

  **Controlled** object (the one that moves), then press **Build chase controller**. That creates a
  ready 3 → 8 → 3 steering network; **+ Target inputs** and **+ Movement outputs** add just those
  bindings if you want to build the graph yourself.

* **Connect objects directly.** Pick a block, light, sign, trigger or group and use

  **Read every property → Inputs** / **Write every property → Outputs**, or add just Position,
  Rotation or Color. Inputs can be relative to another object, so `Target.Position — Ball.Position`
  becomes a direction vector. Outputs support **Set** and **Add** (Add on Position/Rotation is a
  velocity). Level registers `Var1`–`Var5`, player position/velocity/rotation/color, Time and

  **Sign Write (ASCII code)** are available too. Sign Write is the text escape hatch: one output
  writes one character code per frame, so several signs can spell dynamic messages.

* **Target behaviour.** **Static** keeps the goal at its level position. **Animated** follows the
  target's first animation clip, which is the closest match to a moving block in the real level.

  **Orbit**, **Random walk** and **Teleport** move the goal before every input read during editor
  training, so the network learns to follow the *current* target instead of memorising where it
  started. Target speed is degrees/sec for Orbit and blocks/sec for Random walk; Radius controls
  the training bubble.

* **Train here or in the level.** **Editor** trains a population in the editor and bakes all
  weights. **In-game** exports a second trainer Code Block and tunes shared output weights while
  Play mode or retail GRAB is running. **Editor + in-game** does both.
  
* **GRAB-safe runtime tuning.** Tunable weights are stored six per hidden weight-bus object
  (three Position XYZ values and three Rotation XYZ values, with one position parked below the
  level so GRAB hides the helper). The controller reads the buses and the trainer only writes
  them, so each Code Block stays under 150 instructions and 10 connections. **Weights tuned in
  game** is 12, the verified maximum for a 3-output controller; lower it if you add many object
  bindings and the summary turns yellow.

* **Autosave/undo and export.** Project save/load keeps the graph, settings, code blocks and bus
  objects. **Apply to Level** compiles both blocks, **Export .level** writes the real GRAB file, and
  the **C# preview** tab shows the exact controller and trainer source that go to the compiler.

### Pathfinding Studio

**Tools → Pathfinding Studio** (or the **Pathfind** toolbar button) adds a real 3D A* agent to
the editor. Pick any **Finder** and **Target** object, press **Find Path**, then press
**Test in Play Mode**.

* A* searches standable voxel cells with the agent's radius and height, so real walls,
  staircases, bridges and multiple floors are all handled. It never pathfinds through blocks.
* Paths use octile diagonal costs, block corner-cutting, optionally smooth with line of sight,
  and penalise hugging walls, producing practical shortest routes rather than grid staircases.
* **Live route**: the studio recomputes while the Target moves. Play mode replans every
  `Repath Interval` (or immediately when the target moves far enough), so a moving target is
  followed from its **current** position. Target prediction can lead a fast target.
* **Settings** cover cell size, agent radius/height, step-up, drop-down, ground/fly traversal,
  diagonals, corner cutting, smoothing, wall-hug penalty, search iteration limit, repath rate,
  target prediction, stop distance, speed, acceleration, turn speed, facing, waypoint radius and
  nav-grid rebuild rate.
* The green cube is the start, the orange ring is the destination, and cyan dots are real A*
  waypoints. The route is drawn in the overlay viewport so it stays readable through geometry.
* **Show nav mesh** draws the actual navigation grid used for the search: one translucent blue
  tile per walkable cell around the route, plus an orange outline around every blocking object a
  baked live planner would watch. It is off by default (the grid is not even collected until you
  turn it on) and it shows exactly why a route goes the way it does.
* Finder/target IDs and every setting are saved in the `.grabproj`. Play mode restores the
  original scene when stopped.

**Bake Route** compiles the current A* route into a visible Code Block. The block has real GASM
Position connections to the Finder, so the baked motion ships in a normal `.level` export and
runs in retail GRAB without the editor. Set **Loop the baked route** first to repeat it instead
of stopping at the final waypoint.

**Bake Live Nav** writes the re-planning kind of block. It connects the finder, the target and the
**Blocking Objects** you choose — 1 to 96 walls, nearest to the straight line first, with the
connection count not capped by the game — then every frame it reads their *current* positions, steers toward
the target and resolves its own movement against the live wall boxes. When the straight line is
blocked it **follows the wall**: at first contact it picks the end of *that* wall nearest the
target, walks the blocked face all the way to that end without ever reversing along it, rounds the
corner to the target's side, and only then cuts back toward the target. A target directly behind a
wall is therefore reached by going around it instead of pushing into it, and a target past one end
of a long wall is reached through that near end rather than by touring the wall. If the chosen way
is walled off it hands over to the wall that blocked it and follows that one; after repeated jams
it tries the other way around instead of standing still. Moving a wall, adding a wall, or running
the target away is therefore handled inside the game — no re-bake, no frozen waypoint list. It is a
continuous planner rather than a grid search on purpose: GASM indexes an array by expanding a
comparison chain, so a 16 × 16 BFS field would cost thousands of instructions per frame, while the
same search in the editor (`Find Path`) uses the editor's real A* on the full 3D voxel grid. Each
watched object costs roughly 160 GASM instructions per frame, which is why 24 is the default and
96 is the cap; the bake message reports the exact count for the block you just made.

Both kinds of block are ordinary C# Code Blocks — editable, undoable, and exported with the
level. Retail GRAB has no navmesh or A* opcode, which is exactly why the live version does its
planning with plain arithmetic on object positions.

### Block brush

Press **B** or click **Brush** in the toolbar. The palette stays in the scene view so you can
keep painting without chasing a dialog.

| Mode | Gesture |
| --- | --- |
| **Fill** | Click a block to flood-fill its connected same-material island with the selected material. The preview outlines the whole island before you click. |
| **Replace** | Click or drag across blocks to repaint their material. Fast drags are sampled so they do not skip blocks. |
| **Line** | Drag from the first cell to the last to place a one-block-wide run. |
| **Box** | Drag a footprint. **Layers** adds height above the dragged cells; **Hollow** keeps only the shell. |
| **Sphere** | Drag from the centre outward to set the radius; **Hollow** makes a shell. |
| **Erase** | Click or drag over blocks to delete them; mirrored erases remove the matching block on the other side too. |

Shape, material, cell size (0.25–2), layers, hollow, colour and symmetry (None/X/Z/Both)
are all in the palette. New blocks only occupy empty cells, so a brush never overwrites
existing geometry. The preview updates live, and each stroke is a single undoable edit.
Drags are capped at 4096 cells (the status bar says when that happens). **Pivot** moves the
symmetry plane to the selection centre; the transform menu exposes the same pivot.

### Draw structures

Press **G** or click **Draw** in the toolbar. Pick a shape in the palette, then drag a footprint
in the scene and release. The preview is a real block volume, not a separate scene object.

| Shape | Gesture |
| --- | --- |
| **Box** | Drag a rectangle; the Height slider sets the number of layers. |
| **Sphere** | Drag from the centre outward; the drag radius sets the sphere size. |
| **Cylinder / Cone** | Drag the base radius; the Height slider sets the height. |
| **Stairs / Ramp** | Drag the run direction and length; Height sets the rise. |
| **Custom Shape** | Click points to draw a polygon; click the first point, double-click or press Enter to build. Backspace removes the last point. |

The `Snap` menu controls placement: **Face** builds on the surface under the cursor (including
side faces), **Grid** builds on the ground plane, and Edge/Centre are available for prefab/stamp
work. The preview and the committed blocks use only ordinary GRAB block nodes, so they export
normally. Draw groups are collapsed in the Hierarchy like brush strokes.

### Play mode

Play compiles every Code Block, then runs the GASM VM: one instruction stream per program,
yielding on `SLEEP`, with object/player/trigger/light/sign registers wired to the scene.
The player capsule can be moved with WASD and jumped with Space; triggers fire their
ambience/light targets. Stop restores the pre-play snapshot.

## Controls

| Input | Action |
| --- | --- |
| Right mouse drag | Orbit camera |
| Middle mouse drag | Pan camera |
| Wheel | Zoom |
| F | Frame selected / frame all (except while typing in a text field) |
| Right mouse + W A S D | Fly the camera; Space / Ctrl move up / down, Shift boosts |
| Q / W / E / R | Select / Move / Rotate / Scale |
| B | Block brush (Fill / Replace / Erase / Line / Box / Sphere) |
| T | Terrain shaper (Add / Erase / Smooth / Flatten / Paint) |
| G | Draw structures (footprint drag / custom polygon) |
| M | Measure tool |
| P | Prefab stamp tool (then choose a prefab) |
| Ctrl+A | Add GameObject / Component menu |
| Delete | Delete selected object (deletes the selected keyframe when the Animation tab has focus) |
| Ctrl+D | Duplicate |
| Ctrl+S / Ctrl+Shift+S | Save project / Save as |
| Ctrl+O / Ctrl+N | Open / New |
| Ctrl+F | Search the Hierarchy |
| Ctrl+E | Export `.level` (opens export settings) |
| F5 | Play / stop |
| F7 | Compile all |
| Ctrl+Z / Ctrl+Y | Undo / redo the whole editor |
| Ctrl+Shift+Z | Redo |
| K (Animation tab) | Add a frame from the object's current pose |
| Space (Animation tab) | Play / pause the animation preview |
| [ / ] (Animation tab) | Previous / next keyframe |
| Ctrl+Space | Completion |
| F12 / Shift+F12 | Go to definition / find references (C# editor) |
| F2 | Rename symbol in the C# editor |
| Ctrl+Shift+F | Format the C# document |
| Esc | Stop play |

## Command line

```
GRABEngine.exe --selftest          runs the end-to-end checks, writes selftest.log
GRABEngine.exe path\to\file        opens a .grabproj or .level
GRABEngine.exe --train-neural project.grabproj [--hidden 4] [--generations 80]
                                   trains the Neural Ball demo and writes the project,
                                   .level, README and .training.txt report
```

The self-test checks the full pipeline: C# compile, GASM emission, play-mode execution,
project round-trip, `.level` round-trip, ambience, meshes and textures, every block-brush
mode (including fill isolation and replace), the 3D terrain shaper and foundation preparation,
mixed-size/duplicate optimisation, GLB quad/triangle/voxel import with embedded texture
sampling, sound synthesis, MP3 analysis and music-block scheduling, speech phonemes,
formant/block rendering and speech-block scheduling, the neural trainer and its early-stop
rule (including a headless replay of the project and exported `.level`), a 12-object script
that must split before the connection limit, plus undo/redo (including continuous-gesture
coalescing and a real editor scene edit).

## How the game formats work

* `.level` is a protobuf message (`COD.Level.Level`) with nodes in field 6. The field numbers
  used by every node type are documented in [GASMReference.md](GASMReference.md) and
  implemented in `Formats/LevelIO.cs`.

* GASM programs are stored in the Code Block node's field 13. Registers, labels,
  instructions and connections use the game's own encoding (`Formats/GasmIO.cs`).

* SGM meshes are Rayne's binary format (magic `352658064`, version 3). Block meshes carry
  no UVs; the editor box-projects the original textures exactly like the game's shaders.

* Point/spot ranges are exported and previewed in GRAB world units (no editor multiplier).
  The preview subdivides large flat faces near lights because WPF shades per vertex, then
  keeps texture coordinates unchanged.

* `.glb` is parsed directly (no external library). glTF is triangle-only, so quad detection
  pairs coplanar triangles sharing an edge; face/preexisting material colours become
  `Color1` on Default Colored blocks. The importer writes ordinary GRAB nodes, so imported
  geometry exports to `.level` and prefabs without a separate runtime format.

* Object references inside GASM are 64-bit ids assigned by deterministic scene traversal,
  so scripts must be recompiled after renaming/reordering objects. Export always compiles.

## GRAB's real limits (editor vs. runtime)

GRAB shows three numbers on its GASM screen: **150 instructions**, **10 object connections** and
**480 registers** per Code Block. Those checks belong to that screen, not to the interpreter. The
interpreter keeps instructions, connections and registers in dynamic arrays and a `.level` is
loaded straight into them, so a program that is already longer than 150 still runs — the same way
a hand-written custom register name survives loading, even though the screen would not let you type
it.

GRAB ENGINE therefore compiles **one extended Code Block** by default (`Tools → Script Compiler`).
Nothing is split, there is no bus object and no per-frame page hand-off, and a script can use the
whole budget it needs. Switch the mode off to get the old behaviour, where a long script becomes a
chain of continuation Code Blocks that each stay inside the editor's caps.

The live planner baked by Pathfinding Studio depends on this: it is a single Code Block with a few
hundred instructions and one connection per wall it watches.

**Trigger targets** work the same way. A trigger stores its target list as a repeated protobuf
field, so the file has no maximum; the retail editor stops adding at 17 and its screen prints
`Connections: n / 17`, but the loader and the runtime walk the whole list. **Extended trigger
targets** (same window, on by default) lets one trigger START a whole song or a whole scene, and
the audio importers connect every scheduler instead of the first 17. Build Validation then reports
an information line instead of an error.

Still enforced by the game itself (Build Validation reports these): 4-character connection names,
20 transparent blocks, 100 physics groups, 10 spawn points, 1000 animation frames per object and a
150-character sign limit.

## Notes / limitations

* Sound blocks are previewed by the same oscillator, pitch envelope, filter and reverb chain
  the editor exports. The exporter always writes the full GRAB parameter block (frequency min
  35, pitch jump 1, duty 0.5, low-pass 25000); an incomplete block would leave GRAB's low-pass
  at 0 Hz and make the sound silent in game. MP3 import is a chiptune-style note transcription,
  not an embedded audio file: retail `.level` files cannot contain PCM/MP3 data.

* Text-to-speech exports formant oscillators, not a recorded voice: it sounds like a classic
  robot/formant singer. Clarity 2-3 is the most intelligible and uses three sine blocks per
  voiced phoneme, so a sentence can be 80-100 objects; keep Max seconds short while tuning.

* Animation playback is integrated in Play mode. The retail game remains the authority for
  networking and scoring.

* Play mode is a faithful-enough GASM VM for authoring; the retail game remains the final
  authority for physics, networking, scoring, animations and rendering.

* The bundled SGM/PNG assets were extracted from the user's GRAB installation for local
  authoring. Do not redistribute them.
  
* GRAB and its assets belong to their respective owners. This is a fan-made authoring tool.
