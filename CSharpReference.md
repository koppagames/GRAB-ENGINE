# GRAB ENGINE — C# Scripting Reference

This is the complete manual for writing C# in GRAB ENGINE. It covers the editor workflow,
the two ways to attach a script, the Unity-style component system, the supported C# language,
the GRAB API, how every construct becomes GASM, and the limits of the retail game.

GRAB ENGINE is a **C#-to-GASM compiler and editor**, not a .NET game runtime. Your script is
parsed and compiled when you press **Compile** / **F7**. The exported `.level` contains GASM
programs and connections, exactly like a Code Block made in the retail GRAB editor. The game
never loads C# or .NET assemblies.

That distinction explains almost every rule in this manual:

* There is no heap and no call stack. Values become registers, method calls are inlined, and
  arrays/collections are fixed banks of registers.
* Strings are known while compiling. They can contain live numbers only at a place that types
  text into a sign (or the Console).
* `new` creates a scene object in the editor while compiling. It is not a runtime spawn.
* The API is the GRAB API, not `UnityEngine`.
* A Code Block is a standalone program. A **script component** is a friendly editor workflow
  layered on top of a Code Block; it is not a runtime component attached to a GameObject.

> Keep this manual open with **Help → C# Scripting Reference**. It opens inside GRAB ENGINE,
> so it works without a browser or internet connection.

---

## Contents

1. The two ways to use C#
2. Unity-style script components, in depth
3. Lifecycle and events
4. Inspector fields and attributes
5. A complete script, step by step
6. Editor reference and shortcuts
7. Scene objects and `GameObject.Find`
8. Creating objects from code
9. Types, values and operators
10. Statements and control flow
11. Methods, parameters and `params`
12. Arrays
13. `List<T>`, `Dictionary<K,V>` and `foreach`
14. Strings
15. Console and `Debug`
16. Player, Level and Time
17. Math, Vector3, Color and Random
18. Classes, structs, interfaces and generics
19. Exceptions, `using`, `lock` and `goto`
20. Coroutines, `yield` and `async` / `await`
21. Animation from C#
22. Sound and particles
23. Code Block flags and the inspector
24. How C# becomes GASM, limits and continuation blocks
25. Hot reload, debugging, export and install
26. Diagnostics and troubleshooting
27. Worked examples
28. C# coverage cheat sheet
29. Known rough edges

**New here?** Read sections 2 and 5 first. They explain the component workflow and build a
working component end to end. The language chapters after that are reference material; you do
not need to read them in order.

---

## 1. The two ways to use C#

There are two authoring workflows. They produce the **same kind of compiled GASM**, but they
are organized differently in the editor.

| | Standalone Code Block | Script component |
|---|---|---|
| What it is | A normal Code Block object in the level | A Code Block linked to a host object in the editor |
| Created with | **Component → Code Block (C#)** or the Add GameObject menu | **GameObject → Add C# Script to Selection**, or **+ Add C# Script** in the Inspector |
| Hierarchy | Appears as its own Code Block | Also appears as its own Code Block; the host link is metadata, not a parent/child relationship |
| Starts active | Yes by default | Yes by default |
| Program | One independent GASM program | One independent GASM program |
| Fields in Inspector | Shown when the Code Block is selected | Shown when the attached Code Block is selected (Inspector card → **Edit**) |
| Best for | Global systems, controllers, level logic | Behaviour that "belongs" to one object, and Unity-style organization |

### What "script component" means here

The component system is a **Unity-style authoring workflow**. It gives you:

* a real Inspector card on the host object,
* a recorded host id (`ComponentHostId`),
* an **Edit** button that selects the script,
* a backlink from the script to its host,
* the same lifecycle, fields, attributes and debugging as any Code Block.

It does **not** create a runtime `MonoBehaviour`, a C# object parented to the host, or a new
GASM "component" concept in the retail game. In the exported `.level`, the script is just a
Code Block next to every other Code Block. It talks to the host through `GameObject.Find`,
and the host does not know the script exists at runtime.

If you remember only one thing, remember this:

> **A script component is a Code Block with an editor-side host link. It is not attached at
> runtime. You still have to find and drive the host yourself.**

That is why this pattern works:

```csharp
public class DoorBehaviour : CodeBlock
{
    // The host is found by name, at compile time.
    GameObject door = GameObject.Find("Door");

    void Update()
    {
        door.Rotation.Y = door.Rotation.Y + 30f * Time.DeltaTime;
    }
}
```

And this does **not** exist:

```csharp
// There is no GetComponent, no MonoBehaviour, no automatic host field.
var door = GetComponent<...>();
```

### Which workflow should I choose?

Use a **script component** when:

* the script is logically "the behaviour of this object",
* you want the script visible in the object's Inspector,
* you want a per-object set of inspector fields,
* you are coming from Unity and want the familiar organization.

Use a **standalone Code Block** when:

* the script is level-wide logic (a timer, a game manager, a lobby controller),
* the script drives several unrelated objects,
* the script creates objects with `new` or `Voxel`,
* the script has no single natural host.

Both can be converted conceptually at any time: a component is still a Code Block. There is no
separate language or API.

---

## 2. Unity-style script components, in depth

### 2.1 Adding a component

1. Select the object in the Hierarchy or the Scene.
2. In the Inspector, find **C# SCRIPT COMPONENTS**.
3. Press **+ Add C# Script**.

You can also use **GameObject → Add C# Script to Selection** from the menu bar.

**Do not confuse the two commands.** **Component → Code Block (C#)**, the Code Block entry in
the **Ctrl+A** Add GameObject/Component menu, and the normal Add menu create a standalone Code
Block. Only **GameObject → Add C# Script to Selection** and the Inspector's **+ Add C# Script**
link the new Code Block to a host.

GRAB ENGINE then:

* creates a Code Block,
* names it `<Host Name> Script`,
* records the host's editor id in `ComponentHostId`,
* records the template class name `MyScript`,
* copies the host's position, rotation and editor layer (for organization),
* sets **Start Active** on,
* selects the new Code Block and opens the **C# Script** tab.

The host's Inspector now has a component card showing the script and its compile status. The
Code Block is also a normal Hierarchy item; it is **not nested under the host**. The host link
is an editor relationship, not a scene-graph parent.

A component card looks roughly like this:

```
C# SCRIPT COMPONENTS
┌─────────────────────────────────────────┐
│ ⚙ MyScript                    Edit   ✕ │
│ 14 GASM ops · MyScript                  │
└─────────────────────────────────────────┘
+ Add C# Script
```

* **Edit** selects the Code Block and shows its C# source and fields.
* **✕** removes the component Code Block, plus any generated pages or generated objects that
  belong to it. The host object is not deleted.
* The status line tells you whether the script compiled and how many GASM instructions it uses.
* **+ Add C# Script** adds another component to the same host. A host may have several.

When you select a component Code Block itself, its Inspector also shows a button such as
**Component of Door**, which selects the host again.

### 2.2 Editing the script

Select the component card's **Edit**, or select the Code Block in the Hierarchy. The bottom
**C# Script** tab edits the Code Block selected in the Hierarchy.

The template starts like this:

```csharp
using Grab;

// GRAB ENGINE C# script. This is compiled to real GASM.
// Use GameObject.Find("Name") to bind a scene object at compile time.
public class MyScript : CodeBlock
{
    GameObject target = GameObject.Find("Floor");

    public float speed = 2f;

    void Start()
    {
        // Runs once when the code block starts.
    }

    void Update()
    {
        // Example: bob the target up and down.
        target.Position.Y += Mathf.Sin(Time.Time * speed * 30f) * speed * Time.DeltaTime;
    }
}
```

The template's `target` is a demonstration. It chooses the first solid block it can find, which
may not be the object you attached the component to. If you want the host, replace the string
with the host's exact Hierarchy name:

```csharp
public class DoorBehaviour : CodeBlock
{
    GameObject host = GameObject.Find("Door");
    // ...
}
```

`GameObject.Find` is resolved while compiling. If you rename the host later, compile again
(**F7**) so the connection is rebuilt.

The class name is free. It does not have to match the Code Block name, the component card label,
or a file name. The editor stores the source as part of the project's Code Block, not as a
separate `.cs` file.

### 2.3 What the host link actually stores

The level model stores two editor-only strings on the Code Block:

* `ComponentHostId` — the host object's stable editor id,
* `ComponentName` — the name recorded when the component was created.

The Inspector uses these to draw the component card and the **Component of ...** backlink. The
compiler does not use them. They are saved in the `.grabproj` project file, but they are **not**
written into the exported `.level`.

The actual runtime connection is whatever your code does:

```
Host object (editor)                   Code Block (editor + exported level)
  │                                      │
  │  Inspector card                      │  C# source
  │  ComponentHostId ────────────────────┘  (editor-only link)
  │                                      │
  │  GameObject.Find("Door") ────────────┘  (real GASM object connection)
  ▼                                      ▼
retail GRAB sees a normal scene object   retail GRAB sees a normal Code Block
```

This is why a component keeps working after export: the `GameObject.Find` line produced a real
object connection to the host. The component metadata itself did not.

### 2.4 Does the component follow the host?

No. The host link does not couple transforms, activation, layers or lifetime.

* Moving or rotating the host in the Scene does **not** move or rotate the Code Block. The
  component's copied position is only used for editor placement and for generated display
  objects such as the `GASM Console` sign. The host's transform is accessed through
  `GameObject.Find`.
* Changing the host's editor layer does not change the component's layer.
* Hiding the host does not stop the script.
* Setting the host's `Start Active`-style state has nothing to do with the Code Block's
  **Start Active** checkbox. A component has its own **Start Active** flag.
* If you delete the host, the component Code Block is left behind as an orphan. Its
  **Component of ...** button disappears, and any `GameObject.Find(hostName)` will fail to
  compile. Delete the orphan separately or recreate the component on a new host.
* If you duplicate the host, the duplicate does **not** automatically get a copy of the
  component. Select and duplicate the component separately, or add a new script to the copy.
* Saving a prefab from the host alone does not include the separate component Code Block.
  Include the component in the selection, or add the script again after stamping the prefab.

> This is the biggest difference from Unity. In Unity a component is part of the object. In
> GRAB ENGINE it is an independent GASM program that was filed under that object in the editor.

### 2.5 Several components on one object

A host can have multiple script components. Each one is a separate Code Block and therefore:

* has its own instruction budget, with automatic continuation pages if the body is too large
  (section 24.3),
* has its own **10-connection** budget per page,
* has its own fields and **Start Active** setting,
* runs as its own GASM program.

There is no automatic ordering between components. In GRAB ENGINE's Play mode, programs are
created in level order; do not design one component to assume another component's `Start()` has
already run. Use a shared `Level.Var1` … `Level.Var5` flag, an object property, or a trigger if
you need coordination.

There is also no `GetComponent` / `SendMessage` / direct cross-script method call. A method in
another Code Block is not visible to this compiler unit. Use the level as the shared bus:

```csharp
// Script A
Level.Var1 = 1;

// Script B
if (Level.Var1 == 1)
{
    // do the thing
}
```

Or target a Trigger's **Program** callback at the other Code Block and use `OnSignal` in that
script. See section 3.4.

### 2.6 Removing and reattaching

**Remove:** press **✕** on the host's component card. The component Code Block and its generated
children are removed. The host stays.

**Reattach an existing Code Block:** there is no "attach existing Code Block" action. The
component card creates a new Code Block. If you want to keep existing code, paste it into the
new component and delete the old Code Block. A Code Block that is not part of a component still
works perfectly as a standalone script; it simply has no host card.

**Save the project:** use **File → Save Project** (`Ctrl+S`). The project file stores the C#
source, the host link and the inspector field values. Exporting a `.level` stores only compiled
GASM, so keep the `.grabproj` if you want to edit the C# later.

### 2.7 Component template gotchas

The automatically generated template is intentionally small. Keep these points in mind:

1. **The template's target is not guaranteed to be the host.** Replace it with the host name.
2. **The template's `speed` field is a real inspector field** because it is `public`.
3. **The default class name is `MyScript`.** Rename it freely; the component card's stored label
   may keep showing the old recorded name. The actual compiled class is the one in the source.
4. **The template uses `Update`.** If you remove `Update` and only leave `Start`, the script runs
   once and ends. If you want work every frame, keep `Update`, `FixedUpdate` or `LateUpdate`.
5. **The component does not need `GetComponent`.** Bind the host with `GameObject.Find`.
6. **The component's fields are compiled into GASM.** They are not live .NET serialization; see
   section 4.

---

## 3. Lifecycle and events

The compiler supports a practical subset of the Unity message names. They are real GASM code,
not fake callbacks that only work in the editor.

### 3.1 Supported callbacks

| Callback | When it runs | Notes |
|---|---|---|
| `Awake()` | once at program start | runs before `OnEnable` and `Start` |
| `OnEnable()` | once at program start | runs after `Awake`, before `Start` |
| `Start()` | once at program start | the usual setup point |
| `FixedUpdate()` | once per frame | there is one GASM loop; it is not a separate fixed clock |
| `Update()` | once per frame | the normal gameplay update |
| `LateUpdate()` | once per frame, after `Update` | useful for camera/follow logic |
| `OnTriggerEnter()` | first frame a targeting Trigger becomes active | parameterless |
| `OnTriggerStay()` | every frame while that Trigger is active | parameterless |
| `OnTriggerExit()` | first frame the Trigger becomes inactive | parameterless |
| `OnSignal()` | same rising edge as `OnTriggerEnter` | parameterless; use it for named/semantic events |

`Awake`, `OnEnable`, `Start`, `FixedUpdate`, `Update` and `LateUpdate` must return `void`.

```csharp
public class Spinner : CodeBlock
{
    GameObject wheel = GameObject.Find("Wheel");

    void Awake()
    {
        // Runs before Start.
    }

    void OnEnable()
    {
        // Also runs before Start.
    }

    void Start()
    {
        wheel.Rotation.Y = 0;
    }

    void FixedUpdate()
    {
        // Runs once per frame, before Update in this compiler.
    }

    void Update()
    {
        wheel.Rotation.Y += 45f * Time.DeltaTime;
    }

    void LateUpdate()
    {
        // Runs after Update.
    }
}
```

### 3.2 Exact order

For a normal script with an update loop, the emitted program behaves like this:

```
field initialisers / default values
Awake
OnEnable
Start
loop:
    FixedUpdate
    Update
    LateUpdate
    poll targeting Triggers:
        OnTriggerStay while active
        OnTriggerEnter + OnSignal on rising edge
        OnTriggerExit on falling edge
    SLEEP 0
    GOTO loop
```

If the script has no `Update`, `FixedUpdate`, `LateUpdate` or trigger callback, the program ends
after `Awake` / `OnEnable` / `Start`.

`Awake`, `OnEnable` and `Start` are not separate GASM programs; the compiler emits their bodies
in order at program entry. They do not automatically run again every frame.

### 3.3 Start Active

Every Code Block has **Start Active** in its Inspector. When it is on, GRAB ENGINE's Play mode
loads the program when play begins. When it is off, the program is not started by Play mode.

The flag is also written to the retail `.level`. GRAB can start an inactive Code Block through a
Trigger **Program** target. In GRAB ENGINE's Play mode, turn **Start Active** on while testing a
script that is meant to begin immediately.

`Start Active` is not a C# field; it is a native Code Block flag. Do not confuse it with the
`Start()` method.

### 3.4 Trigger events

Trigger callbacks are real GASM. While the program runs, it reads the `Active` register of each
Trigger whose **Program** target points at this Code Block. It calls the hook on the correct
edge. No extra hidden connection is required beyond the Trigger object itself.

To wire one up:

1. Add or select a Trigger.
2. In the Trigger Inspector, under **TARGETS**, choose the target kind **GASM**.
3. Choose the Code Block (a script component appears under its Code Block name, not the host's
   name).
4. Add the target.
5. In the script, implement `OnTriggerEnter`, `OnTriggerStay`, `OnTriggerExit` or `OnSignal`.

```csharp
public class Door : CodeBlock
{
    Trigger zone = GameObject.Find<Trigger>("Door Zone");
    GameObject door = GameObject.Find("Door");

    void OnTriggerEnter()
    {
        door.Position.Y = 2;
    }

    void OnTriggerExit()
    {
        door.Position.Y = 0;
    }
}
```

Important details:

* Hooks must be **parameterless**. There is no event object, collider argument or player argument.
  Get the player through `Player` if you need it.
* `OnSignal` is invoked on the same rising edge as `OnTriggerEnter`. Use whichever name reads
  better for your event.
* `OnTriggerStay` runs while `Active` is non-zero, including the first frame.
* A trigger with no sources (Hand, Head, Grapple, Feet, Block, Spawn) is script-only in GRAB
  ENGINE Play mode: only `ForceActive` or a GASM write can activate it. This is how audio and
  animation helper triggers avoid firing because the player happens to stand on them.
* Each Trigger that targets the script consumes one of the Code Block's connections. If there
  are more triggers than the connection budget allows, the compiler warns and uses the first
  ones. Reduce targets or split the logic into another Code Block.
* A trigger targeting the **host** does not fire the component's callback. The target must be
  the component's Code Block. This is a common mistake.

### 3.5 Callbacks that cannot exist in GRAB

The compiler recognizes these names so it can give a useful warning, but GRAB has no matching
event. They are not executed:

| Callback | Why not |
|---|---|
| `OnDisable()` | GASM has no unload/disable callback for a program |
| `OnDestroy()` | same |
| `OnGrab()` | no GASM event for grabbing |
| `OnRelease()` | no GASM event for release |
| `OnPlayerJoin()` | no GASM join event |
| `OnPlayerLeave()` | no GASM leave event |

Use the state that actually changes instead: poll a Trigger's `Active`, read `Player` or another
object property in `Update`, or use `OnTriggerEnter` / `OnSignal` as the event. The compiler
reports a warning; it does not silently pretend the callback ran.

`FixedUpdate` is also only an approximation: GASM has one program loop, so it executes once per
frame rather than on Unity's fixed timestep. The compiler warns once so this is never a surprise.

### 3.6 Native "Late Update" checkbox

The Code Block Inspector also has native Code Block flags:

* **Start Active** — described above.
* **Shared** — GRAB's native shared-program flag, exported as-is.
* **Late Update** — GRAB's native late-update flag, exported as-is.

These are different from the C# `LateUpdate()` method. The C# method is compiled into the program
loop after `Update()`. The **Late Update** checkbox is a game-side flag stored on the Code Block;
GRAB ENGINE Play mode does not use it to reorder C# callbacks.

---

## 4. Inspector fields and attributes

Script fields can appear in the Inspector without opening the code. This is Unity-like, but the
values are **compile-time constants**, not runtime serialization.

### 4.1 Which fields are shown?

A field is shown when it is:

* `public`, or marked `[SerializeField]` / `[SerializeReference]`,
* not `static` or `const`,
* not marked `[HideInInspector]` or `[NonSerialized]`,
* not an array,
* a supported type: `float`, `int` (and other numeric spellings), `bool`, `string`, `Vector3`,
  `Color`, or an enum.

Private fields without `[SerializeField]` stay private.

```csharp
public class Platform : CodeBlock
{
    [Header("Motion")]
    [Tooltip("How far the platform moves up and down.")]
    [Range(0f, 4f)]
    public float height = 1f;

    [SerializeField]
    private float speed = 2f;

    [HideInInspector] public float debugValue;

    public bool startMoving = true;
    public Vector3 axis = new Vector3(0, 1, 0);
    public Color tint = Color.white;
}
```

The Inspector displays:

* a **SERIALIZED FIELDS** section when the Code Block is selected,
* an asterisk (`*`) beside a field that has an override,
* a **Reset to script default** button for each overridden field,
* sliders for `[Range]` / `[Slider]`,
* dedicated editors for booleans, colors, vectors and strings.

The host's component card does not list the fields. Select the component (or press **Edit** on
the card) to see them.

### 4.2 What an inspector override means

When you change a field in the Inspector, GRAB ENGINE stores the value in the Code Block's
project data. On the next compile, the compiler substitutes that constant into the field's
initializer. It does **not** create a runtime C# object or serialize a live value.

Consequences:

* Changing a field during Play mode does not affect the currently running program until you
  compile (and hot reload may restart the script).
* An override can only be a constant expression accepted by the Inspector: number, bool, string,
  `Vector3` or `Color` literal, or a qualified enum name such as `DoorState.Open`. You cannot
  put arbitrary code in an inspector field.
* `const`, `static`, object-reference and array fields ignore inspector overrides. Change those
  in source.
* A field without an explicit initializer starts at `0`/`false`/empty, including all
  `Vector3` and `Color` components. Give visual fields an explicit value if the type editor
  shows a friendlier default.
* If the Inspector value cannot be parsed for the field type, the compiler emits a warning and
  falls back to the script's initializer.

This is deliberate: the retail game does not know about your C# fields, so there is nowhere to
put a true serialized C# value. The exported level only gets the resulting constants and GASM.

### 4.3 Supported attributes

| Attribute | Effect |
|---|---|
| `[Header("Text")]` | Inserts a section heading before the field |
| `[Tooltip("Text")]` | Shows helper text under the field |
| `[Label("Text")]` | Replaces the automatic pretty-printed field label |
| `[Range(min, max)]` | Shows a slider with that range |
| `[Slider(min, max)]` | Same as `Range` |
| `[ColorUsage]` | Treats the field as a color editor |
| `[SerializeField]` | Exposes a private field to the Inspector |
| `[SerializeReference]` | Also exposes the field |
| `[HideInInspector]` | Keeps a public field out of the Inspector |
| `[NonSerialized]` | Keeps a field out of the Inspector |

Example:

```csharp
public class LightFlicker : CodeBlock
{
    Light lamp = GameObject.Find<Light>("Lamp");

    [Header("Flicker")]
    [Range(0f, 10f)]
    [Tooltip("Base brightness.")]
    public float brightness = 4f;

    [Range(0f, 2f)]
    public float flickerAmount = 0.4f;

    void Update()
    {
        lamp.Brightness = brightness + Mathf.Sin(Time.Time * 31f) * flickerAmount;
    }
}
```

### 4.4 Attributes that are parsed but do not create UI

The C# front end records a few more attributes so the editor can inspect scripts and give better
diagnostics, but the current Inspector does not render interactive controls for them:

* `[Button]` — recorded for reflection-style inspection, but the Inspector does not draw a button
  and GASM has no runtime reflection. Call the method from `Update` or from a trigger instead.
* `[TextArea]` / `[Multiline]` — recorded, but script string fields currently use the normal
  single-line editor. Keep such strings short.

Custom attributes are otherwise ignored. Do not expect `System.Reflection` or Unity's attribute
system to work in the exported game.

---

## 5. A complete script, step by step

This walkthrough builds a component that opens a door when a trigger is active.

### Step 1 — Build the scene

1. Add a solid block and name it **Door**.
2. Add a Trigger and name it **Door Zone**.
3. Position the Trigger where the player should stand.

### Step 2 — Attach a component

1. Select **Door**.
2. Inspector → **C# SCRIPT COMPONENTS** → **+ Add C# Script**.

GRAB ENGINE creates a Code Block called **Door Script**.

### Step 3 — Replace the template

Paste this into the **C# Script** tab:

```csharp
using Grab;

public class DoorBehaviour : CodeBlock
{
    GameObject host = GameObject.Find("Door");
    Trigger zone = GameObject.Find<Trigger>("Door Zone");

    [Header("Door")]
    [Tooltip("How fast the door slides.")]
    [Range(0.5f, 6f)]
    public float speed = 2f;

    [Label("Open Height")]
    public float openHeight = 2f;

    float currentHeight;

    void Start()
    {
        currentHeight = host.Position.Y;
    }

    void Update()
    {
        float target = zone.Active == 1 ? openHeight : 0f;
        currentHeight = Mathf.MoveTowards(currentHeight, target, speed * Time.DeltaTime);
        host.Position.Y = currentHeight;
    }
}
```

### Step 4 — Compile and test

1. Press **F7** (Compile All) or **F6** in the code editor.
2. Look at the **Problems** tab. It should have no errors.
3. Press **F5** to play.
4. Walk into the Trigger and watch the door move.
5. Press **Esc** to stop; the editor restores the scene.

### Step 5 — Adjust without editing code

Select **Door** in the Hierarchy, then press **Edit** on the script card. The **SERIALIZED
FIELDS** section now has **Speed**, **Open Height** and any other public/serialized fields.
Changing them and compiling bakes the new values into the program.

### What the compiled program looks like conceptually

The compiler turns the script into roughly:

* one named register for `speed`,
* one named register for `openHeight`,
* one named register for `currentHeight`,
* one connection to the Door block (position),
* one connection to the Trigger (active),
* an entry body for field setup and `Start`,
* a frame loop containing the `Update` body,
* `SLEEP 0` and `GOTO` back to the loop.

The actual **GASM** tab shows the exact instructions. Open it and compare; this is the fastest way
to learn what a C# construct costs.

---

## 6. Editor reference and shortcuts

### 6.1 The scripting panels

| Panel | Purpose |
|---|---|
| **C# Script** bottom tab | Code editor for the selected Code Block |
| **GASM** bottom tab | Read-only compiled program for the selected Code Block |
| **Problems** | All diagnostics; double-click a row to jump to the line |
| **Console** | `Console.Write` / `Debug.Log` output mirrored by Play mode |
| **Debug** | Pause state, breakpoints, variables and stepping controls |
| **Inspector** | Native Code Block flags, script component cards and serialized fields |

The editor's source header shows `<Code Block>.cs`. That name is cosmetic: the source is stored
inside the project, not written to a real file with that name.

### 6.2 Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New level |
| `Ctrl+O` | Open project |
| `Ctrl+S` | Save project |
| `Ctrl+Shift+S` | Save project as |
| `Ctrl+E` | Export `.level` |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo editor actions |
| `Ctrl+Shift+Z` | Redo |
| `Ctrl+D` | Duplicate selection |
| `Ctrl+Shift+A` | Select all objects (when not typing) |
| `F5` | Start Play; while paused, continue |
| `F7` | Compile all scripts |
| `Esc` | Stop Play |
| `F9` | Toggle breakpoint on the current code line |
| `F10` | Step one source line while paused |
| `F11` | Step one GASM instruction while paused |
| `F6` | Compile from the C# editor |
| `Ctrl+Space` | Force the completion list |
| `F2` | Rename the symbol at the caret |
| `F12` | Go to definition |
| `Shift+F12` | Find references |
| `Ctrl+Shift+F` | Format the document |
| `F` (Scene) | Frame the selection |
| `W A S D` (Play) | Move the player |
| `Space` (Play) | Jump |

Single-letter editor shortcuts are suppressed while an input box has focus, so typing `f` in a
field types `f` instead of framing the scene.

### 6.3 Where the code is stored

Scripts live in the `.grabproj` project data. Saving the project saves:

* the C# source of every Code Block,
* inspector field overrides,
* component host links,
* all scene objects and animations,
* the level metadata and ambience.

The `.level` export contains the compiled GASM, connections, objects and animations. It does not
contain the C# source. This is why a `.level` can be played by retail GRAB but cannot be opened
as a C# project later; keep the `.grabproj`.

### 6.4 Compile lifecycle in the editor

When you press **F7** / **F6**:

1. Every non-generated Code Block with source is compiled.
2. Diagnostics are collected with file/line/column information.
3. Generated console signs, animation triggers, `new` objects and continuation pages are
   reconciled with the new program.
4. The GASM tab and Problems list are refreshed.
5. If Play mode is running and compilation succeeds, the new programs are hot-swapped.

An empty Code Block is skipped. Generated continuation pages are not compiled as user scripts;
they are managed by the compiler.

---

## 7. Scene objects and `GameObject.Find`

### 7.1 Binding an object

Scene objects are compile-time references. Bind them in a field declaration:

```csharp
GameObject box     = GameObject.Find("Wood Cube");
GameObject sign    = GameObject.Find<Sign>("Score");
Trigger zone       = GameObject.Find<Trigger>("Door Zone");
Light lamp         = GameObject.Find<Light>("Sun Light");
PhysicsBlock crate = GameObject.Find<PhysicsBlock>("Crate");
```

`GameObject.Find` resolves the name against the Hierarchy while compiling and stores the object's
stable id in the GASM connection. It is not a runtime hierarchy search.

* Matching is case-insensitive, but exact spelling is still the sane choice.
* Start points and Finish points cannot be connected to Code Blocks. Use a normal block or a
  trigger as the anchor instead.
* `GameObject.Find<Sign>` performs a type check. Asking for a Sign when the object is a block is
  a compile error.
* Renaming or deleting an object after compiling requires **Compile All**. Until then, the old
  connection is still in the compiled program.
* Object field initializers must be `GameObject.Find(...)`. Object locals inside `Update` are not
  a runtime lookup either; prefer fields.

The generic type is a compile-time safety check, not a runtime cast. `GameObject` is the generic
"any scene reference" type.

### 7.2 Members shared by every scene object

| Member | Type | Access | Notes |
|---|---|---|---|
| `Position` | `Vector3` | read/write | World units. Finish points have no Position. |
| `Rotation` | `Vector3` | read/write | Euler degrees. |
| `Scale` | `Vector3` | read only | Set it in the Inspector or a `new` initializer. |
| `Name` | string | read only | Compile-time display name. |
| `Write` | string / number | write | Signs display text; other objects store an editor label. |
| `Text` / `Label` | string | write | Same storage as `Write` for non-sign labels. |

```csharp
box.Position = new Vector3(2, 1, 0);
box.Position.Y += 1.5f * Time.DeltaTime;
box.Rotation.Y = 90;
box.Translate(new Vector3(0, 0.25f, 0));   // relative move
box.Rotate(new Vector3(0, 15, 0));         // relative rotation
box.SetPosition(new Vector3(0, 1, 0));
box.SetRotation(new Vector3(0, 45, 0));
```

`Position`, `Rotation` and `Scale` are accessed through real GASM object connections. Each object
used by a Code Block consumes one of its 10 connection slots, no matter how many of its
properties you read.

### 7.3 Blocks

`PhysicsBlock` (or `Block`) is the typed view of a solid static or crumbling block.

```csharp
PhysicsBlock block = GameObject.Find<PhysicsBlock>("Crate");

block.Color = new Color(1, 0.3f, 0.1f);
block.Physics.Enabled = 0;     // 0 = physics off, 1 = physics on
float state = block.Physics.State;
```

`Color` is only programmable on solid blocks and lights. `Scale` is read-only at runtime. Some
materials, such as ice, do not use the paint color; the Inspector enforces that rule visually.

### 7.4 Lights

```csharp
Light lamp = GameObject.Find<Light>("Sun Light");

lamp.Color = new Color(1, 0.85f, 0.65f);
lamp.Brightness = 4f;    // 0..10 in the Inspector
lamp.Range = 12f;        // 0..100
```

`lamp.Color` uses the C# 0..1 color convention. The compiler scales it to GASM's color
registers automatically.

### 7.5 Triggers

```csharp
Trigger zone = GameObject.Find<Trigger>("Door Zone");

if (zone.Active == 1)
{
    // player/hand/block is inside
}

zone.ForceActive = 1;    // fire the On Enter side
zone.ForceActive = 0;    // fire the On Leave side
float who = zone.PlayerId; // 255 means nobody, otherwise the player id
```

`Active`, `PlayerId` and `Part` are read-only. `ForceActive` is writable and is the native
`Trg.Force` connection. A trigger with no sources can be activated purely from code this way.

See section 3.4 for the event callbacks and how to wire the **Program** target.

### 7.6 Signs

`Sign` is both a scene object and the display used by `Console`. Its members are raw GASM sign
registers:

| Member | Effect |
|---|---|
| `Write` | In ASCII mode, writes one character code. In number mode, writes a number. A string value writes the literal text into the level. |
| `Select` | `0` = ASCII mode, `1` = number mode. |
| `Control` | `1` = clear, `2` = delete the last character. |
| `Number` | Clears and prints the number. |
| `Color` | Sign color in an object initializer. |

```csharp
Sign score = GameObject.Find<Sign>("Score Sign");

score.Number = 0;
score.Control = 1;
score.Select = 0;
score.Write = 72;            // ASCII 'H'
score.Write = "Hello";       // literal text, baked into the level
```

Live numbers are supported only when text is being written. This is the normal scoreboard
pattern:

```csharp
void Update()
{
    score.Control = 1;
    score.Write = string.Concat("Score: ", Level.Var1);
}
```

The compiler emits `SET score.Control 1`, ASCII text writes and a number-mode write. That is the
same sequence a hand-authored GASM sign program uses, so it works in retail GRAB and in Play
mode. Keep live lines short: each literal character costs an instruction every frame.

`.Write`, `.Text` and `.Label` exist on every object. On a sign, they change the displayed text.
On a non-sign object they store an editor label in the project; GRAB does not render text on
blocks, so the label is not drawn in game. A live number cannot be displayed by a non-sign
object.

### 7.7 Sounds and particle emitters

Sound and particle settings are authored in the Inspector or set as compile-time properties when
creating the object with `new`. GASM has no general live property registers for them.

```csharp
Sound chime = new Sound("chime");
ParticleEmitter sparks = new ParticleEmitter
{
    Position = new Vector3(0, 2, 0),
    Rate = 30,
    StartColor = Color.cyan,
    Velocity = new Vector3(0, 3, 0)
};
```

To play a sound in game, use the native Trigger sound target or the Audio Studio workflow. C#
cannot call an arbitrary `Play()` method on a Sound object; that method does not exist in the
GASM API.

---

## 8. Creating objects from code

### 8.1 `new` is a compile-time creation

`new` is supported for scene object types, but it does not allocate at runtime. The compiler
creates the object in the level, applies the constructor and initializer, and writes a real GASM
connection for it.

```csharp
Sign board = new Sign("Score");

Cube crate = new Cube(Material.Wood)
{
    Name = "Crate",
    Position = new Vector3(3, 1, 0),
    Scale = new Vector3(2, 2, 2),
    Physics = true,
    Grabbable = true
};

Light lamp = new Light(Color.yellow, 5f, 9f)
{
    Position = new Vector3(0, 4, 0),
    LightType = LightType.Point
};

Trigger zone = new Trigger(3f)
{
    Position = new Vector3(0, 2, 0)
};
```

The object appears in the level after compilation and is part of the export. Running the program
again does not create another object. Recompiling reuses the object associated with that `new`
expression and updates its properties.

`new` inside a loop or an `if` still creates **one** object while compiling. The compiler warns
about this because it is the most common misunderstanding:

```csharp
// One block, not ten. The loop is not doing what a runtime spawn would do.
for (int i = 0; i < 10; i++)
{
    Cube c = new Cube(Material.Wood);
}
```

Use `Voxel.Grid`, `Voxel.Line` or `Voxel.Ring` for many compile-time blocks.

### 8.2 Constructors

| Type | Constructor |
|---|---|
| `Sign` | `new Sign(text)` or `new Sign()` |
| `Cube`, `Sphere`, `Cylinder`, `Pyramid`, `Prism`, `Cone`, `PyramidSquare` | `new Cube(material)` etc. |
| `PhysicsBlock` / `Block` | `new PhysicsBlock(shape, material)` |
| `CrumblingBlock` / `Crumbling` | `new CrumblingBlock(shape, material)` |
| `Light` | `new Light(color, intensity, range)` |
| `Trigger` | `new Trigger(size)` or `new Trigger(new Vector3(w, h, d))` |
| `Sound` / `SoundEmitter` | `new Sound(name)` |
| `ParticleEmitter` / `Particles` | `new ParticleEmitter()` |
| `GravityBlock` | `new GravityBlock(direction)` |
| `StartPoint` / `FinishPoint` | `new StartPoint(radius)` / `new FinishPoint(radius)` |
| `LobbyTerminal` | `new LobbyTerminal()` |

All arguments must be compile-time constants.

### 8.3 Object initializer properties

These can be set inside `{ ... }` after `new`. All values must be compile-time constants.

| Group | Properties |
|---|---|
| Common | `Name`, `Position`, `Rotation`, `Scale`, `Size`, `Hidden`, `StartActive` |
| Block | `Color`, `Shape`, `Material`, `Physics`, `LocalPhysics`, `Mass`, `Grabbable`, `Grapplable`, `Passable`, `Neon`, `Transparent`, `Additive`, `Gradient`, `GradientDirection`, `Specular`, `SpecularBrightness`, `StableTime`, `RespawnTime`, `Local`, `HideModel` |
| Sign | `Text`, `Write`, `Label`, `SignScale`, `SignWeight`, `SignNeon`, `HideModel`, `Color` |
| Light | `Color`, `Brightness`, `Intensity`, `Range`, `Angle`, `LightType`, `DistanceFalloff`, `ConeFalloff` |
| Trigger | `Size`, `Shared`, `Radius` (where applicable) |
| Particle emitter | `Rate`, `ParticlesPerSecond`, `LifeSpanMin`, `LifeSpanMax`, `StartColor`, `EndColor`, `StartSizeMin`, `StartSizeMax`, `EndSizeMin`, `EndSizeMax`, `Velocity`, `VelocityMin`, `VelocityMax`, `AccelerationMin`, `AccelerationMax`, `HideParticles` |
| Sound | `SoundName`, `Volume`, `Repeat`, `StartActive`, `MaxRange`, `Frequency`, `WaveType`, `Attack`, `Sustain`, `Release` |
| Gravity | `GravityMode`, `GravityDirection` |
| Crumbling | `StableTime`, `RespawnTime` |

`Shape` and `Material` are integer constants, so the readable forms are:

```csharp
Shape.Cube
Shape.Sphere
Shape.Cylinder
Shape.Pyramid
Shape.Prism
Shape.Cone
Shape.PyramidSquare

Material.Default
Material.Grabbable
Material.Ice
Material.Lava
Material.Wood
Material.Grapplable
Material.GrapplableLava
Material.GrabbableCrumbling
Material.DefaultColored
Material.Bouncing
Material.Snow
Material.Trigger
```

Use `new Cube(Material.Wood)` rather than passing a raw number when possible; it is checked and
readable.

### 8.4 Voxel helpers

The voxel helpers exist because `new` inside a loop cannot mass-produce objects. They run while
compiling and create ordinary blocks in the level.

```csharp
// Grid: origin, countX, countY, countZ, spacing, shape, material
int n = Voxel.Grid(new Vector3(0, 1, 0), 8, 1, 8, 1.25f, Shape.Cube, Material.Wood);

// Line: start, end, spacing, shape, material
Voxel.Line(new Vector3(-4, 1, 0), new Vector3(4, 1, 0), 1f, Shape.Cube, Material.DefaultColored);

// Ring: centre, radius, count, shape, material
Voxel.Ring(new Vector3(0, 1, 0), 6f, 48, Shape.Cube, Material.Wood);
```

Rules and limits:

* All arguments must be compile-time constants.
* A grid is clamped to 64 cells per axis and a total of **4096 blocks** per call.
* `Voxel.Grid` creates a solid rectangular volume, not a hollow shell.
* `Voxel.Ring` places blocks around the XZ plane at the given Y.
* The function returns the number of blocks created.
* These objects are real level objects; rename, move or edit them afterwards like any other
  block.

---

## 9. Types, values and operators

### 9.1 Numeric types

All numeric C# types are represented as 32-bit floating-point registers. `float`, `int`,
`double`, `long`, `uint`, `short`, `byte`, `decimal` and their aliases are accepted so normal C#
code parses, but arithmetic is floating point.

* Integer division is not special: `5 / 2` is `2.5`, not `2`.
* There is no integer overflow or bit-level integer representation.
* `int` fields still appear as number fields in the Inspector.
* Number suffixes such as `f` are accepted.
* `(int)x` does **not** truncate; it is a float conversion. Use `Mathf.FloorToInt`,
  `Mathf.RoundToInt` or `Mathf.CeilToInt` when you need integer-like behavior.
* `int.Parse("42")`, `float.Parse("2.5")` and `Convert.*` work on **compile-time** text.

### 9.2 Booleans

`bool` is a numeric value interpreted as false at `0` and true at non-zero. `true` and `false`
are compile-time constants. Comparisons produce booleans. A bool known only at runtime displays
as `0`/`1` in Console output, not `True`/`False`.

### 9.3 Strings

Strings are **compile-time values**. A literal, a compile-time string expression and a
compile-time string field are all known when the level compiles. GASM has no string registers.

Live numbers can be part of text only at a text sink:

* `sign.Write = string.Concat("Time: ", Time.Time);`
* `Console.WriteLine("Y: {0}", Player.Pos.Y);`
* `sign.Write = $"Time: {Time.Time}";`

A runtime number cannot be converted with `ToString()` outside those sinks:

```csharp
// Error: the value is only known at runtime.
string text = Time.Time.ToString();
```

See section 14 for the full string API.

### 9.4 Vector3 and Color

`Vector3` holds three floats. `Color` holds four floats in the C# 0..1 range. Both are value
bundles: assigning one to another copies its components.

```csharp
Vector3 a = new Vector3(1, 2, 3);
Vector3 b = a;                 // copy
b.Y = 9;                       // a.Y is still 2
Color c = new Color(1, 0.5f, 0, 1);
```

### 9.5 Object references

Scene objects are compile-time references, not runtime C# objects. They can be assigned to
fields and typed variables, passed to methods, and used to read/write their GASM properties.
`GameObject.Find` is the only way to obtain one.

### 9.6 Classes, structs, arrays and collections

Classes are compile-time bundles of registers. A class assignment aliases the same bundle;
a struct assignment copies its fields. Arrays are fixed-size register banks. `List<T>` and
`Dictionary<K,V>` are fixed-capacity register banks. None of these are allocated at runtime.

### 9.7 `var`

`var` infers the initializer's type locally:

```csharp
var speed = 2.5f;                    // float
var axis = new Vector3(0, 1, 0);     // Vector3
var label = "hello";                 // string
```

### 9.8 Operators

Supported operators:

* arithmetic: `+`, `-`, `*`, `/`, `%`
* comparison: `==`, `!=`, `<`, `>`, `<=`, `>=`
* logical: `!`, `&&`, `||`
* assignment: `=`, `+=`, `-=`, `*=`, `/=`, `%=`
* increment/decrement: prefix and postfix `++` / `--`
* ternary: `condition ? a : b`
* casts: `(float)x`, `(int)x`, `(bool)x`, object type casts
* string concatenation with `+` when at least one side is a string

**Important difference from C#:** `&&` and `||` do **not** short-circuit, and `?:` evaluates both
arms. The compiler must materialize both sides because GASM is register-based. Do not put
side-effecting calls on the right side of `&&` and expect them to be skipped:

```csharp
// Both calls are evaluated.
if (CheckA() && CheckB()) { ... }
```

Likewise, both expressions in `flag ? DoA() : DoB()` are compiled as calls. Use a separate `if`
when you need lazy behavior.

Bitwise complement `~` is not supported, and there are no true bitwise integer operations. Use
arithmetic or the supported logical operators.

---

## 10. Statements and control flow

Supported statements:

* local declarations, including multiple declarators: `float a = 1, b = 2;`
* expression statements and assignments
* `if` / `else`, including `else if`
* `while`, `do ... while`, `for`
* `switch` / `case` / `default`
* `break`, `continue`, `return`
* `goto` and labels
* blocks and nested scopes
* `try` / `catch` / `finally` / `throw`
* `using`, `lock` (see section 19)
* `yield return` / `yield break` and `await` at coroutine top level
* `foreach` over arrays, `List<T>` and `Dictionary<K,V>`

Example:

```csharp
void Update()
{
    float total = 0;

    for (int i = 0; i < 4; i++)
    {
        if (i == 2) continue;
        total += i;
    }

    while (total > 10)
    {
        total -= 10;
    }

    switch (Mathf.RoundToInt(total))
    {
        case 0:
            host.Neon = true;
            break;
        case 1:
            host.Neon = false;
            break;
        default:
            host.Neon = false;
            break;
    }
}
```

Use braces for clarity. The compiler tracks scopes, but GASM has no separate block lifetime; a
local declaration still reserves registers for the whole program.

### 10.1 Top-level statements

If a script has no class, GRAB ENGINE accepts top-level statements. They run once at program
entry and the program ends. This is useful for one-shot setup:

```csharp
using Grab;

GameObject floor = GameObject.Find("Floor");
floor.Color = new Color(0.8f, 0.8f, 0.9f);
```

Top-level statements cannot define methods. For anything that needs a frame loop, use a class
with `Update()`.

If both a class and top-level statements exist, the class is compiled as the entry point and the
top-level statements are ignored with a warning.

A top-level-only script has no `Update`, so the compiler warns that it has no frame loop. That
warning is informational: the top-level statements still run once at program entry, then the
program ends.

### 10.2 Which class is the script?

When a script has more than one class, the compiler chooses:

1. the first class with `Update()` or `Start()`, otherwise
2. the first class in the file.

That class becomes the program. Other classes, structs, interfaces and enums are still available
as types. This is why helper classes work but only the entry class's fields are shown in the
Inspector and used as top-level fields.

---

## 11. Methods, parameters and `params`

Methods are supported and are inlined at every call site:

```csharp
public class Mover : CodeBlock
{
    GameObject box = GameObject.Find("Box");

    float Ease(float t)
    {
        return t * t * (3f - 2f * t);
    }

    void Update()
    {
        float t = Time.Time % 4f / 4f;
        box.Position.Y = Ease(t) * 3f;
    }
}
```

Features:

* parameters and return values of supported types
* `void`, `float`, `int`, `bool`, `string` (compile-time), `Vector3`, `Color`, object types,
  structs and class types
* overloads with different parameter counts
* `static` methods
* expression-bodied methods: `float Double(float x) => x * 2;`
* `ref` and `out` parameters
* a trailing `params` parameter

```csharp
float Add(float a, float b) => a + b;

void Bump(ref float value, float amount)
{
    value += amount;
}

void Sum(out float result, params float[] values)
{
    result = 0;
    for (int i = 0; i < values.Length; i++) result += values[i];
}
```

A `params` call packs its arguments into registers at the call site; it is not a heap array.
`ref`/`out` variables are inlined and write back to the caller's storage.

Because methods are inlined, a large helper called from ten places costs its body ten times. Keep
helpers small when you are near the instruction limit, or use a separate Code Block.

### 11.1 Recursion

Recursion is **not supported**. GASM has no call stack. A method cannot call itself directly or
indirectly. Rewrite recursive algorithms as loops with an explicit array/stack-like set of
registers.

### 11.2 Lambdas, delegates and events

Lambdas, delegates, events and LINQ are not supported. There is no runtime function object. Use a
named method, a loop, or an object/array that represents the state.

### 11.3 Method calls from `Update`

`Start()` and `Update()` are engine entry points. Do not call them manually; the compiler gives
an error. Call your own helper methods instead.

### 11.4 Coroutines are special

`IEnumerator` methods, `yield`, `async` methods and `await` are compiled into a resumable state
machine. See section 20.

---

## 12. Arrays

Arrays are fixed-size and allocated at compile time. Every element has its own register.

```csharp
float[] heights = new float[] { 1, 2, 3, 4 };
float[] buffer  = new float[8];        // zero-filled
int[]   ids     = new int[3];
bool[]  flags   = new bool[4];

heights[0] = 5;                        // constant index: one SET
for (int i = 0; i < heights.Length; i++)
{
    heights[i] = heights[i] * 2;       // runtime index: compare/branch chain
}
```

Rules:

* Element types: `float`, `int` and `bool`. All are stored as numbers.
* Size must be a compile-time constant, from `1` to `32`.
* `new T[] { ... }` sets the size from the initializer.
* `array.Length` is a compile-time constant.
* A constant index compiles to one register access.
* A runtime index compiles to a comparison chain: about 3 instructions per element. Keep arrays
  small in hot paths. A loop over an array plus a second loop can already approach the
  150-instruction page budget.
* Out-of-range constant indexes are compile errors. Runtime indexes outside the array read `0`
  and ignore writes.
* Whole-array assignment copies element by element.
* Arrays are not shown in the Inspector.
* Jagged and multi-dimensional arrays are not supported.

Array helpers:

```csharp
Array.Reverse(a);                 // in place
int i = Array.IndexOf(a, 1);     // index or -1
float[] b = new float[3];
Array.Copy(a, b, 3);             // length is optional
Array.Fill(b, 7);
Array.Clear(a);
```

`Array.Copy` only copies into an existing array of a compatible size. `Array.Sort` is not
available.

---

## 13. `List<T>`, `Dictionary<K,V>` and `foreach`

Collections are real C# syntax backed by a fixed register bank. They have no heap growth.

### 13.1 Lists

```csharp
List<float> heights = new List<float>(8);   // capacity 8, default is 4
heights.Add(1f);
heights.Add(2f);
heights[0] = heights[0] + 10f;
int n = heights.Count;

List<int> wave = new List<int>();
wave.Add(3);
```

Supported list members:

`Add`, `AddRange(array)`, `Insert(index, value)`, `RemoveAt`, `Remove(value)`, `Clear`,
`Contains`, `IndexOf`, `Count`, `Capacity`, indexer, and `foreach`.

`List<T>` stores numbers only (`float`, `int`, `bool`). Objects, structs, `Vector3` and `Color`
cannot be stored.

### 13.2 Dictionaries

```csharp
Dictionary<string, int> scores = new Dictionary<string, int>();

scores["ann"] = 10;
scores["bob"] = scores["ann"] + 5;

if (scores.ContainsKey("bob"))
{
    int value = scores["bob"];
}

scores.Remove("ann");
```

Supported dictionary members:

`Add(key, value)`, indexer read/write, `ContainsKey`, `Remove`, `Clear`, `TryGetValue`,
`Count`, and `foreach` over `KeyValuePair<K,V>`.

Keys may be numbers, booleans or strings. String keys are stored as hashes, so use literal keys
and compare with literals. Values must be numeric. A dictionary `foreach` walks the entire fixed
capacity, so a capacity-8 dictionary can push one loop past the 150-instruction page budget. Keep
enumerated dictionaries at capacity 4 unless you have measured the compiled GASM.

`dict.Keys` and `dict.Values` are not available at runtime. Use `foreach` and read `kv.Key` /
`kv.Value`.

### 13.3 Fixed capacity

A collection's capacity is fixed by the `new` expression and is clamped to `1..32`. The default
is 4.

```csharp
List<float> a = new List<float>();      // capacity 4
List<float> b = new List<float>(16);    // capacity 16
```

`Add` silently does nothing once the list is full. Size it for the largest number of entries the
script can reach. There is no `EnsureCapacity`, no growth and no shrinking beyond `Clear` /
`Remove`.

### 13.4 `foreach`

`foreach` works on arrays, lists and dictionaries:

```csharp
float total = 0;

foreach (float h in heights)
{
    total += h;
}

foreach (var pair in scores)
{
    total += pair.Value;
}
```

Use `var` for the dictionary pair. The explicit `KeyValuePair<string, int>` type name is not
accepted by the current type parser, even though `pair.Key` and `pair.Value` are available on the
inferred pair. The pair is read-only here. The compiler walks the fixed capacity at runtime;
`List.Sort` and `List.Reverse` are not supported.

These two loops are shown as separate small examples. Combining both in one `Update` can exceed
one 150-instruction page, and the dictionary loop in particular grows with capacity.

---

## 14. Strings

Strings are evaluated while compiling. A `string` field or local can hold a literal, another
compile-time string, or a string expression whose parts are known while compiling.

```csharp
string title = "  grab engine  ".Trim().ToUpper();   // "GRAB ENGINE"
int len      = title.Length;                        // 11
bool hasGrab = title.Contains("GRAB");              // true
string part  = title.Substring(0, 4);               // "GRAB"
string loud  = "hello".ToUpper() + " " + "world".ToUpper();
string csv   = string.Join(",", "a", "b", "c");     // "a,b,c"
string line  = string.Concat("x", 1, "y");          // "x1y"
bool empty   = string.IsNullOrEmpty(title);         // false
string fmt   = string.Format("Score: {0}", 42);    // "Score: 42"
```

### 14.1 Instance members

`Length`, `ToUpper`, `ToUpperInvariant`, `ToLower`, `ToLowerInvariant`, `Trim`, `TrimStart`,
`TrimEnd`, `Substring`, `Contains`, `StartsWith`, `EndsWith`, `IndexOf`, `LastIndexOf`,
`CompareTo`, `Replace`, `Remove`, `Insert`, `PadLeft`, `PadRight`, `Equals`, `ToString`.

All arguments that affect the result must be compile-time values. For example,
`"abc".Substring(1, 1)` works; a substring index from a runtime variable does not.

### 14.2 Static members

`string.Concat`, `string.Join`, `string.Format`, `string.IsNullOrEmpty`,
`string.IsNullOrWhiteSpace`, `string.Compare`, `string.CompareOrdinal`, `string.Copy`,
`string.Empty`.

### 14.3 Characters and conversion

`char` helpers are compile-time:

`char.IsDigit`, `IsLetter`, `IsLetterOrDigit`, `IsNumber`, `IsWhiteSpace`, `IsUpper`, `IsLower`,
`IsPunctuation`, `IsSymbol`, `IsControl`, `ToUpper`, `ToLower`, `ToString`, `Parse`.

`Convert.ToInt32`, `ToInt64`, `ToUInt32`, `ToSingle`, `ToDouble`, `ToDecimal`, `ToBoolean`,
`ToChar`, `ToString`.

`int.Parse` and `float.Parse` convert constant text.

### 14.4 Interpolated strings

Interpolated strings are supported where the result is text:

```csharp
sign.Write = $"Time: {Time.Time}";

Console.WriteLine($"x={Player.Pos.X} y={Player.Pos.Y}");
```

The result can contain live values only while it is being written to a sign or Console. Do not
store an interpolation containing a live number in a `string` variable and expect it to keep the
value: a string variable is compile-time text. Write it directly at the sink instead.

String comparison requires compile-time text. `a == b` on runtime strings is not possible.

### 14.5 What strings cannot do

* `Split` and `ToCharArray` are not supported (they would need runtime allocation).
* Runtime `ToString()` on a live number is not supported outside a sign/Console sink.
* There is no `StringBuilder`, regex, culture formatting or file/network text.
* Format specifiers such as `{0:0.00}` are ignored with a warning; format numeric text at
  compile time or use separate writes.

---

## 15. Console and `Debug`

### 15.1 Console output

`Console.Write`, `Console.WriteLine` and `Console.Clear` are supported.

```csharp
Console.WriteLine("Ready");
Console.WriteLine("Time: {0}  y: {1}", Time.Time, floor.Position.Y);
Console.Write("Score: ");
Console.WriteLine(Level.Var1);
Console.WriteLine('A');
Console.Clear();
```

GASM has no print instruction. The compiler creates a hidden generated Sign named
`GASM Console` about 2.2 units above the Code Block and emits ordinary sign writes. In Play mode,
every completed line is also mirrored into the **Console** tab.

Formatting details:

* Values can be strings, numbers, bool constants (`True` / `False`), chars, `Vector3`
  (`(x, y, z)`) and `Color` (`RGBA(r, g, b, a)`).
* `+` concatenation and `{0}` / `{1}` placeholders work.
* `{{` and `}}` print literal braces.
* Format specifiers such as `{0:0.00}` are ignored with a warning.
* A `bool` known only at runtime prints as `0` or `1`.
* `Write` appends; `WriteLine` starts a fresh line and appends a newline.
* A sign stops at 150 characters, so one line is capped there.
* Each literal character costs one GASM instruction. Large logs can be split across continuation
  Code Blocks.
* `ReadLine`, `ReadKey`, `Beep`, `ResetColor` and cursor/window members are rejected or warned
  about. GASM has no keyboard or OS console.

The generated console sign is part of the level. It is hidden from the Hierarchy because it is
generated, but it is written to the exported level.

### 15.2 `Debug` aliases

`Debug.Log`, `Debug.LogWarning`, `Debug.LogError`, `Debug.LogFormat` and `Debug.Print` are
aliases for Console output. They do not use Unity's logging system.

```csharp
Debug.Log("Door opened");
Debug.LogWarning("Low score");
```

`Debug.Assert` is not supported; use an `if` and log a message.

---

## 16. Player, Level and Time

### 16.1 Player

`Player` is the special GASM player connection.

```csharp
float x = Player.Pos.X;
float y = Player.Pos.Y;

Player.Vel.Y = 8;               // jump
Player.Tel.Pos = new Vector3(0, 3, 0);
Player.Tel.RotY = 90;
Player.Tel.Apply = 1;           // teleport now
Player.Respawn = 1;
```

Read-only: `Pos`, `Rot`, `Count`, `Valid`, `Id`, `LocalId`, `Col.R`, `Col.G`, `Col.B`.

Writable: `Vel`, `Tel.Pos`, `Tel.RotY`, `Tel.Apply`, `Respawn`, `Col.Select`, `SelectIndex`,
`SelectId`, `SelectPart`, `Control`.

`Pos`/`Rot` are read-only vectors; use `Tel.Pos` and `Tel.RotY` to move the player. `Player.Col`
itself is not assignable; use `Player.Col.R` etc. for reads and `Player.Col.Select` for the
color-selection register.

### 16.2 Level variables

Five lobby-wide variables persist across scripts and frames:

```csharp
Level.Var1 = Level.Var1 + 1;

if (Level.Var1 > 10)
{
    Level.Var1 = 0;
}
```

`Var1` … `Var5` are read/write. They are the `Lobby` connection in GASM. They are shared by all
scripts in the lobby, not private storage for one component.

### 16.3 Time

```csharp
float dt = Time.DeltaTime;      // seconds since the last frame
float t  = Time.Time;           // seconds since this program started
float f  = Time.FrameCount;     // frames since this program started
```

* `Time.DeltaTime` is the frame delta for this step; use it for smooth motion.
* `Time.Time` is a hidden accumulator advanced once per frame by the update loop. In a
  `Start`-only script with no loop, it stays at zero.
* `Time.FrameCount` is incremented once per frame before the update body.
* Lowercase `time` / `deltaTime` / `frameCount` are also accepted.

---

## 17. Math, Vector3, Color and Random

### 17.1 `Mathf` / `Math` / `MathF`

Supported functions:

`Sin`, `Cos`, `Tan`, `Atan`, `Atan2`, `Asin`, `Acos`, `Sqrt`, `Abs`, `Floor`, `FloorToInt`,
`Ceil`, `CeilToInt`, `Round`, `RoundToInt`, `Pow`, `Min`, `Max`, `Clamp`, `Clamp01`, `Lerp`,
`Sign`, `Repeat`, `PingPong`, `Approximately`, `InverseLerp`, `SmoothStep`, `MoveTowards`,
`LerpAngle`, `MoveTowardsAngle`, `DeltaAngle`, `Rad2Deg`, `Deg2Rad`, `IsPowerOfTwo`,
`NextPowerOfTwo`.

Constants: `Mathf.PI`, `Mathf.E`, `Mathf.Deg2Rad`, `Mathf.Rad2Deg`,
`Mathf.Infinity`, `Mathf.NegativeInfinity`.

Trigonometry uses **radians**, following C# / Unity conventions. `LerpAngle`,
`MoveTowardsAngle`, `DeltaAngle` and `Vector3.Angle` use **degrees**, matching GRAB rotations.

`IsPowerOfTwo` and `NextPowerOfTwo` need a compile-time constant because GASM has no bitwise
instructions.

### 17.2 `Vector3`

```csharp
Vector3 a = new Vector3(1, 2, 3);
Vector3 b = new Vector3(4, 5, 6);

float d = Vector3.Distance(a, b);
float dot = Vector3.Dot(a, b);
Vector3 cross = Vector3.Cross(a, b);
Vector3 n = Vector3.Normalize(a);       // or a.Normalized
float len = a.Length;                   // or a.Magnitude
float sq = a.SqrMagnitude;
Vector3 p = Vector3.Project(a, b);
Vector3 q = Vector3.ProjectOnPlane(a, b);
float angle = Vector3.Angle(a, b);
Vector3 r = Vector3.Reflect(a, b);
Vector3 moved = Vector3.MoveTowards(a, b, 1f);
Vector3 scaled = Vector3.Scale(a, b);
Vector3 min = Vector3.Min(a, b);
Vector3 max = Vector3.Max(a, b);
```

Constants:

`Vector3.zero`, `one`, `up`, `down`, `left`, `right`, `forward`, `back` (capitalized forms are
accepted too).

Also supported:

* `Vector3.Lerp`, `Vector3.LerpUnclamped`
* `Vector3.ClampMagnitude`
* `Vector3.Normalize`
* component access `.X` / `.Y` / `.Z` (lowercase accepted)
* operators `+`, `-`, unary `-`, `vector * scalar`, `scalar * vector`, `vector / scalar`

`Vector2` is not supported; use `Vector3` with `Z = 0`.

### 17.3 `Color`

```csharp
Color c = new Color(1f, 0.5f, 0.25f);
Color withAlpha = new Color(1f, 0.5f, 0.25f, 0.8f);

Color mixed = Color.Lerp(Color.red, Color.blue, 0.5f);
float r = c.R;    // 0..1
```

Constants: `red`, `green`, `blue`, `white`, `black`, `yellow`, `cyan`, `magenta`, `gray`,
`grey`, `clear`. Capitalized forms are accepted.

C# colors are 0..1. GASM stores channels as 0..255 internally; the compiler scales
automatically.

### 17.4 `Random`

`Random` is GRAB's random helper, not `System.Random`.

```csharp
float chance = Random.Value;                    // 0..1
float height = Random.Range(1f, 5f);            // 1..5
int index = (int)Random.Next(0, 4);              // 0..3
if (Random.Chance(0.25f)) { /* 25% chance */ }
```

Supported: `Random.Value`, `Random.Range(min, max)`, `Random.Next()`, `Random.Next(max)`,
`Random.Next(min, max)`, `Random.Chance(probability)`.

### 17.5 `Debug`

Covered in section 15.2.

---

## 18. Classes, structs, interfaces and generics

GRAB ENGINE supports a practical object-oriented C# subset. Because there is no heap, an object
instance is a **compile-time bundle of registers**.

```csharp
using Grab;

public interface IDamageable
{
    float Health();
    void Hit(float amount);
}

public struct Point
{
    public float X;
    public float Y;

    public Point(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float Sum() => X + Y;
}

public abstract class Actor : IDamageable
{
    public float hp = 10f;
    public float Speed { get; set; }

    public virtual void Hit(float amount)
    {
        hp -= amount;
    }

    public virtual float Health() => hp;

    public abstract void Think();
}

public class Enemy : Actor
{
    public string label = "enemy";

    public Enemy()
    {
        Speed = 1f;
    }

    public override void Hit(float amount)
    {
        // The current compiler has no `base.Hit` call. Apply the base rule here.
        hp -= amount * 2f;
    }

    public override float Health() => hp;

    public override void Think() { }
}

public class Box<T>
{
    public T Value;

    public Box(T value)
    {
        Value = value;
    }
}

public class MyScript : CodeBlock
{
    Point origin = new Point(1f, 2f);
    Enemy enemy = new Enemy();
    Box<float> box = new Box<float>(4f);

    void Update()
    {
        IDamageable target = enemy;
        target.Hit(1f);
        enemy.Health();
        origin.Sum();
        Level.Var1 = enemy.Health() + origin.Sum() + box.Value;
        Console.WriteLine(enemy.label);
    }
}
```

Supported:

* `class`, `struct`, `interface`, `abstract` classes and methods
* constructors
* fields, `const`, `readonly`, `static` fields and methods
* auto-properties such as `public float Speed { get; set; }`
* `virtual`, `override`, `abstract`
* inheritance and interface implementation
* generics (classes, methods and `List<T>` / `Dictionary<K,V>`)
* object initializers: `new Box<float>(1f) { Value = 2f }`
* `this.field` access inside an instance method of a user class

`base.Method(...)` calls are not accepted yet. Put the inherited rule directly in the override or
move it into a helper method and call that. `this` is not a runtime object for the entry
`CodeBlock` itself, so avoid `this` inside `Awake`, `Start`, `Update` and the trigger callbacks; it
works inside helper classes that you create with `new`.

### 18.1 Allocation model

Each `new` expression becomes its own registers at compile time.

* A class variable refers to the same bundle: assigning it aliases the object.
* A struct variable copies its fields.
* `new` in a loop does **not** allocate a fresh instance each iteration. It creates one bundle
  while compiling and reuses it. Write `new` once, usually as a field initializer, then mutate.
* Recompiling preserves the object associated with the same source location when possible.
* Method calls are inlined; a class instance does not exist as a C# object at runtime.

### 18.2 Auto-properties

`public float Speed { get; set; }` is turned into an ordinary field. A property with an accessor
body (`get { return hp; }` or `set { ... }`) is still treated as a field; the body is ignored
with a warning. Use a method or keep the computed value in a field.

### 18.3 Static members and enums

Static fields and methods are supported. Enums become static constant ints:

```csharp
public enum DoorState
{
    Closed,
    Opening,
    Open
}

DoorState state = DoorState.Closed;
```

### 18.4 Not supported

* `base(...)` constructor chaining and `base.Method(...)` calls
* `new`-hidden members
* operator overloading
* indexers with custom accessors
* delegates, events, lambdas and LINQ
* custom runtime attributes / reflection
* recursion
* multiple entry classes (only the class with `Start` / `Update` is the program; others are types)
* true runtime polymorphism through .NET objects; virtual dispatch is compiled as branches

---

## 19. Exceptions, `using`, `lock` and `goto`

```csharp
public class Res
{
    public float Used;
    public void Dispose() { Used = 1; }
}

try
{
    if (Level.Var1 < 0) throw new Exception("negative");
    Level.Var2 = 10;
}
catch (Exception ex)
{
    Level.Var2 = ex;        // ex is a numeric error code, not an exception object
}
finally
{
    Level.Var3 = 1;
}

goto skip;
Level.Var4 = 1;
skip:
Level.Var4 = 2;

Res r = new Res();
using (r)
{
    Level.Var5 = 1;
}

lock (r)
{
    Level.Var5 = Level.Var5 + 1;
}
```

* `catch` (with or without an `Exception ex`) catches a `throw` from this script.
* The caught variable is a **number**. GASM has no exception object; the message is hashed to a
  code. `ex.Message` is not available.
* GASM runtime faults such as division by zero are not catchable exceptions. They halt the Code
  Block and are reported in the Console.
* `finally` runs on normal completion, on `throw`, and before `return`. A `break` or `continue`
  that leaves a `try` skips `finally`; restructure if that matters.
* `using` works with any type with a `Dispose()` method; the call is inlined at the end of the
  block. `using var x = ...;` declarations are not supported.
* `lock` compiles but has no effect. A Code Block runs on GASM's single thread. The editor shows
  a note, not an error. Lock any class instance, as in `lock (r)`; `lock (this)` is rejected in an
  entry `CodeBlock` because the entry class is not a runtime instance.
* `goto` supports forward and backward jumps to a label in the same method.

---

## 20. Coroutines, `yield` and `async` / `await`

Both coroutines and async methods compile to the same resumable state machine. Locals become
persistent registers and a hidden state register chooses the next segment.

```csharp
IEnumerator Blink()
{
    box.Color = new Color(1, 0, 0);
    yield return new WaitForSeconds(1f);

    box.Color = new Color(0, 1, 0);
    yield return null;                // resume next frame

    box.Color = new Color(0, 0, 1);
}

void Start()
{
    StartCoroutine(Blink());
}
```

```csharp
async Task Fade()
{
    box.Color = new Color(1, 1, 1, 1);
    await Task.Delay(500);            // milliseconds
    box.Color = new Color(1, 1, 1, 0);
}

void Start()
{
    Fade();                           // starting an async method also starts it
}
```

Supported waits:

* `yield return null` — next frame.
* `yield return new WaitForSeconds(seconds)` — delay in seconds.
* `await Task.Delay(milliseconds)` — delay in milliseconds.
* `await Task.Yield()` — next frame.
* `await null` — next frame.
* `await new WaitForSeconds(seconds)` — delay.

Rules and limitations:

* `yield` and `await` must be **top-level statements of the coroutine body**. A yield inside an
  `if` or a `for` is rejected. Put the loop inside a helper or restructure the coroutine.
* A coroutine cannot await another coroutine. Use `StartCoroutine` instead.
* Waits always last at least one frame. You cannot resume later in the same frame.
* The coroutine state is initialized on the page that owns it and survives continuation pages.
* Methods returning `IEnumerator`, `IEnumerable`, `Task` or `ValueTask`, and methods marked
  `async`, are treated as coroutine bodies.
* `StartCoroutine` takes a method call, e.g. `StartCoroutine(Blink())`, not an arbitrary object.
* A coroutine that has finished does not reset unless it is started again.

---

## 21. Animation from C#

Every scene object can hold GRAB animation clips. Author them in the **Animation** tab or with
the Inspector's animation card. A clip stores key time, position, rotation, restart/ping-pong
direction, speed and interpolation.

### 21.1 Start Active on an animation

The Animation tab's **Start Active** setting is GRAB's native `activeAnimation` field. When it is
on, the selected clip plays as soon as the level loads. When it is off, `activeAnimation` is
written as `-1` and the clip waits for a trigger or an `Animator` call.

### 21.2 `Animator`

```csharp
GameObject target = GameObject.Find("Floor");

void Start()
{
    Animator.Play(target, "spin");              // loop, forward
    Animator.Play(target, "spin", false);       // play once
    Animator.Play(target, "spin", true, true);  // loop, backwards
}

void Update()
{
    if (Player.Pos.Y < 1)
    {
        Animator.Reverse(target, "spin");
    }

    if (Animator.IsPlaying(target))
    {
        // ...
    }
}
```

| Call | Effect |
|---|---|
| `Animator.Play(target, "clip")` | Start a clip; optional `loop` and `reverse` compile-time booleans |
| `Animator.Stop(target)` | Fire a native STOP animation target; optional clip name |
| `Animator.Pause(target)` | Same native STOP target, for readable code |
| `Animator.Resume(target)` | Fire the native START target |
| `Animator.Restart(target)` | Fire GRAB's native RESTART target |
| `Animator.Reverse(target, "clip")` | Fire GRAB's native TOGGLE_REVERSE target |
| `Animator.IsPlaying(target)` | Read the START trigger's Force register as a bool |

`Stop`, `Pause`, `Resume`, `Restart` and `IsPlaying` ensure the required generated animation
trigger exists even when the script never calls `Play`.

### 21.3 How the trigger is built

The compiler creates a real Trigger object with an **Animation** target connected to the scene
object. This is the native mechanism GRAB uses. The trigger appears in the Hierarchy with a
`⚙ script` / clip badge so you can inspect it.

`Play` writes that trigger's Force register. The exported `.level` therefore plays the clip in the
retail game. Targets carry GRAB's real animations modes:

* `START = 1`
* `STOP = 0`
* `TOGGLE_REVERSE = 3`
* `RESTART = 4`

Each target/clip/loop/reverse/mode combination consumes a Code Block connection. Very
animation-heavy scripts can hit the 10-connection limit; split the animation control into another
Code Block if needed. Unused generated triggers are removed when the script stops referencing
them.

---

## 22. Sound and particles

Sound blocks and particle emitters are primarily authored in the Inspector and in **Audio
Studio** / **Tools → Audio Studio**. C# can create them at compile time and set their initial
properties, but GASM does not expose general live sound or particle property registers.

```csharp
Sound chime = new Sound("chime")
{
    Position = new Vector3(0, 2, 0),
    Volume = 1f,
    Repeat = false
};

ParticleEmitter fire = new ParticleEmitter
{
    Position = new Vector3(0, 1, 0),
    Rate = 40,
    LifeSpanMin = 0.5f,
    LifeSpanMax = 1.5f,
    StartColor = Color.yellow,
    EndColor = new Color(1, 0, 0, 0),
    Velocity = new Vector3(0, 3, 0)
};
```

To start or stop a sound in game, use a native Trigger with a Sound target. The Audio Studio does
this automatically for imported songs and speech. A C# script can activate such a trigger with
`ForceActive`, but there is no `sound.Play()` API in GASM.

---

## 23. Code Block flags and the inspector

When a Code Block is selected, the Inspector shows the native flags and script data:

| Control | Meaning |
|---|---|
| **Start Active** | Start the program when the level loads / Play begins |
| **Shared** | GRAB's native shared-program flag |
| **Late Update** | GRAB's native late-update flag |
| **Open C# Editor** | Focus the source tab |
| **Compile** | Compile this script (and refresh diagnostics) |
| **Component of ...** | Backlink to the host, when the block is a script component |
| **SERIALIZED FIELDS** | Fields exposed by the script class |
| **Lifecycle / Events** | Detected callbacks, useful as a checklist |

The hierarchical name of a Code Block (for example `Door Script`) is independent of its C# class
name. You can rename either one without renaming the other.

---

## 24. How C# becomes GASM, limits and continuation blocks

### 24.1 What the compiler emits

* Every script field becomes a named working register (for example `speed`). An uninitialized
  field emits `SET speed 0`.
* Temporaries are named `t0`, `t1`, … and reused.
* `Update` compiles into a `loop:` label ending with `SLEEP 0` and `GOTO loop`.
* Object access becomes real connections such as `obj.Pos.X`, `obj.Rot.Y`, `obj.Col.R`,
  `trg.Active`, `sign.Sign.Write`, `Player.Pos.X` and `Lobby.Var1`.
* Arrays become one named register per element. Dynamic indexes become compare/branch chains.
* Methods are inlined.
* Strings are folded while compiling; live number values become sign writes or Console text.
* `new` scene objects are added to the level; `Voxel.*` creates blocks at compile time.
* `Console.*` creates a generated `GASM Console` sign and emits sign writes.

The compiled program and its connections are written into the Code Block's protobuf fields when
you export the `.level`.

### 24.2 Hard limits

| Limit | Value | Applies to |
|---|---:|---|
| Instructions per Code Block | 150 | one compiled program/page |
| Connections per Code Block | 10 | objects, player, lobby and generated targets |
| Working registers per program | 480 | all fields, temporaries, arrays and collections |
| Array length | 32 | each fixed-size array |
| Collection capacity | 32 | each `List` / `Dictionary` |
| Sign characters | 150 | one sign's text |
| Trigger targets | 17 | one Trigger |
| Transparent blocks | 20 | level-wide |
| Physics groups | 100 | level-wide |
| Animation frames | 1000 | per object |
| Named spawn points | 10 | level-wide |
| Overlapping lights | 3 | GRAB disables the newest past three |

The compiler itself uses four-character connection names (for example `bl00`, `tr02`, `sg01`).
**You do not need to shorten object names to four characters.** GRAB ENGINE generates a unique
four-character connection name for each object. The object's display name can be longer; only
the internal connection name has the four-character restriction. If an object is renamed after
compiling, recompile so `GameObject.Find` rebuilds the connection.

### 24.3 Continuation Code Blocks

GRAB limits one Code Block to 150 instructions and 10 connections. GRAB ENGINE does **not**
reject a script merely because its total is larger than one page. It splits the update loop into
pages and creates extra Code Blocks automatically. The splitter now checks both budgets before
compiling each statement: it breaks the page when the next statement would exceed the
instruction budget **or** when it would add a scene object connection that no longer fits. This
is what keeps a large script with many `GameObject.Find` bindings from failing with “Page N
needs 11 connections”. The split still happens between statements, so a single oversized
statement has to fit on one page.

* One page holds as much of the loop as fits; the rest continues on the next page.
* Event dispatcher connections for `OnTriggerEnter` / `Exit` / `Stay` / `OnSignal` are reserved
  before the final body page is closed. If the body fills the page, the dispatcher starts on a
  fresh continuation page instead of dropping trigger events with a warning.
* Pages share values through hidden generated **GASM Bus** objects (`GASM Bus 1`,
  `GASM Bus 2`, …) parked far below the level. A typical script needs one bus; very large scripts
  add more when the spill space runs out.
* The first bus position's X component carries the page counter. The other bus position components
  carry spilled locals, so variables keep their values across pages and frames.
* Only one page is active per frame. Each page ends by stepping the counter and starting the next
  page, so state is continuous but execution is spread across frames. A script that needs several
  pages therefore updates at a lower effective rate; keep frame-critical loops inside one page.
* Generated continuation blocks are named `<Script> 2`, `<Script> 3`, … and are **hidden from
  the Hierarchy** because they are generated. They are still real Code Blocks in the exported
  level and in the GASM view.
* The generated bus objects are also hidden.
* Editing the script regenerates the pages. If the script fits in one block again, old pages are
  deactivated and removed from the exported active programs.
* Because the bus consumes one connection, each page can reference at most **9 scene objects**
  (or fewer if player, lobby or other connections are used). The splitter reserves the bus and
  starts a new page before a statement that would add another object. Generated spill buses can
  also use connections in state-heavy scripts. If one page still needs too many, reduce the
  objects that a single statement touches or move that statement to another Code Block.
* The split applies to `Update()` (or to `Start()` when the script has no update loop). A
  `Start()` that must run before a separate update loop has to fit in one block on its own.
* A single statement must fit in one page. A huge loop or an extremely long sign write is
  compiled as one unit; keep each one under GRAB's 150-instruction limit and leave headroom for
  the loop branch and page hand-off instructions.
* When a script is paged, the paged loop body is the primary loop method: `Update` when present,
  otherwise `FixedUpdate`, otherwise `LateUpdate`. Keep extra loop callbacks small or put their
  work in `Update` for very large scripts.
* Coroutines (`yield` / `await`) work across pages; their state is initialized at program entry
  on the page that owns them.

### 24.4 Generated objects

Some objects are created and managed by the compiler:

* `GASM Console` — hidden sign used by `Console` / `Debug`
* `GASM Bus N` — hidden continuation-bus objects
* animation control triggers — visible, with a script badge
* `new` scene objects — visible, with a script-created marker
* Voxel blocks — visible, grouped by source

Deleting the Code Block or component removes the generated objects associated with it. If you
hand-edit a generated object, the next compile may overwrite it.

### 24.5 The Release optimizer

**File → Export** offers two profiles.

* **Debug** writes the compiler output as-is, with readable names. Use it when inspecting GASM or
  reporting a bug.
* **Release** runs safe passes:
  * constant folding,
  * removal of self-assignments (`SET x x`),
  * removal of identity operations (`x + 0`, `x * 1`) and multiplication by zero,
  * removal of no-ops,
  * removal of unreachable code after `GOTO` / `END`,
  * animation cleanup (sort keys, remove duplicate times, preserve duration),
  * trimming imported GASM copies that a compiled Code Block no longer needs.

Labels are not renumbered, so control flow is preserved. The export verifies the file by
re-reading nodes, ids, programs and animations. Optimisation never intentionally changes
register values, control flow, animation timing or gameplay.

---

## 25. Hot reload, debugging, export and install

### 25.1 Hot reload

When Play mode is running and you press **F7** / compile after editing a script, GRAB ENGINE
swaps in the new compiled programs without resetting the level, player or physics.

* Matching registers are carried from the old program to the new one by name (non-zero values
  are counted in the status message).
* Fields and temporaries that no longer exist are dropped.
* The new program starts at its entry; this means `Awake`, `OnEnable` and `Start` can run again
  on hot reload. Avoid destructive startup side effects or guard them with a flag.
* Coroutine state machines restart with the new program.
* The toolbar shows the live-edit status and how many registers were kept.

This is a live edit, not a full Play restart. Keep a **Stop** and **Play** cycle for changes that
need a fresh scene.

### 25.2 Debugging

1. Press **F5** to start Play (this compiles first).
2. Click the gutter or press **F9** on a source line to add a breakpoint.
3. When execution reaches it, Play pauses and the **Debug** tab shows the current line, GASM
   instruction, breakpoints and variables.
4. Use **F5** to continue, **F10** to step one source line, **F11** to step one GASM instruction.
5. Press **Esc** to stop.

Breakpoints are per Code Block. The Debug panel follows the selected Code Block when possible.
Pausing stops all scripts at the same game step, so inspecting one program does not let others
race ahead.

`Console.WriteLine` and `Debug.Log` are often faster than a breakpoint. Remember they type into a
sign and mirror into the Console tab; they consume instructions per character.

### 25.3 Export

Use **File → Export .level** (`Ctrl+E`).

The dialog provides:

* **Debug** or **Release** profile
* safe script optimisation
* animation cleanup
* trimming unused imported GASM
* verify after writing
* optional human-readable GASM listing
* open the export folder

Export writes the real GRAB protobuf `.level`. The C# source is **not** included. To keep editing
later, keep the `.grabproj` project.

### 25.4 Install to GRAB

**Install to GRAB** uses the same export pipeline and then copies the `.level` to the game's
levels folder. The dialog can target Steam or Meta Quest; the Quest flow uses adb and the levels
directory:

`/sdcard/android/data/com.slindev.grab_demo/files/levels/user`

C# source remains in GRAB ENGINE. Only the compiled level is installed.

---

## 26. Diagnostics and troubleshooting

### 26.1 How diagnostics are shown

Errors and warnings include a file/line/column. They are:

* underlined in the C# editor,
* listed in the **Problems** tab,
* attached to the compiled Code Block,
* shown on the component card as **Has compile errors**.

Double-click a Problems row to jump to the source line. A warning does not block Play; an error
does.

### 26.2 Common messages

| Message | Meaning and fix |
|---|---|
| `No object named 'X' was found` | `GameObject.Find` does not match the Hierarchy. Fix the spelling or recompile after renaming. |
| `This property is read-only` | e.g. assigning `Scale`, `Player.Pos`, `Player.Col.R`. Use the Inspector, an initializer, or the `Player.Tel` teleport helper. |
| `Array needs a fixed size` | Use `new float[4]` or `new float[] { ... }`. |
| `Array index N is outside 0..M` | Constant index is out of range. |
| `Array size must be 1..32` | Reduce the array or split the data. |
| `String methods need compile-time text` | Strings are compile-time values. Use a literal, or write live text directly to a sign/Console. |
| `foreach needs an array, List<T> or Dictionary<K,V>` | Only those are enumerable. |
| `Recursion is not supported` | GASM has no call stack. Use a loop. |
| `Program needs more than 480 registers` | Reduce live fields, arrays, list capacities or large temporaries. |
| `Page N still needs ... instructions after splitting` | One statement is too large. Shorten the loop or sign write. |
| `needs ... connections; GRAB allows 10` | One page touches too many objects. Split the statement into another Code Block or reduce object references. |
| `Console.ReadLine() cannot work in GRAB` | GASM Code Blocks have no keyboard input. |
| `This console line is N characters` | A warning; signs stop at 150 characters. |
| `must be initialized with GameObject.Find(...)` | Object fields need a compile-time scene reference. |
| `Inspector value '...' is not a constant ...` | The Inspector field contains something the compiler cannot use; it falls back to the source default. |
| `Property has an accessor body ... body is ignored` | Auto-properties are fields; do not use computed accessors. |

### 26.3 Troubleshooting by symptom

**The script compiles but does nothing.**

* Does it have `Start`, `Update`, `FixedUpdate`, `LateUpdate` or a trigger callback?
* Is **Start Active** on?
* Did you press F7 after editing?
* If it is a component, did you bind the host with `GameObject.Find`, or did you only assume the
  host was available automatically?

**The component does not affect the object it is attached to.**

The component link is editor metadata. There is no automatic host field. Add:

```csharp
GameObject host = GameObject.Find("Exact Host Name");
```

**The trigger callback never runs.**

* Does the Trigger have a **Program** target pointing at the Code Block, not the host?
* Is the Trigger's source set (Hand/Head/Feet/Block/etc.), or are you using `ForceActive`?
* Is the component Code Block **Start Active** on?
* Is the callback parameterless?
* Are there more targeting triggers than the 10-connection budget permits?

**An object name change broke the script.**

Recompile with **F7**. `Find` is compile-time. Long object names are fine; the compiler generates
four-character connection names.

**A string works in one place but not another.**

Strings are compile-time except at a text sink. `sign.Write = "Score: " + Level.Var1` works
because the sign writes the live number. `string s = "Score: " + Level.Var1;` does not, because a
plain string cannot hold a runtime number.

**The sign only shows the first frame or the wrong text.**

Use the clear/retype pattern:

```csharp
sign.Control = 1;
sign.Write = string.Concat("Value: ", someLiveNumber);
```

Typing every frame costs one instruction per character. Keep it short.

**A script ran before its data was ready.**

There is no automatic ordering between Code Blocks. Use `Start` on the data script and an
`if`/flag in the consumer, or drive the consumer from a Trigger.

**Hot reload runs my setup twice.**

Hot reload restarts each program at entry while preserving named registers. `Awake`, `OnEnable`
and `Start` run again. Guard one-shot side effects with a persistent field or use `Level.Var`.

**The animation call does nothing.**

The target must have at least one clip with the exact name, the generated trigger must fit the
connection budget, and **Start Active** must be on for the controlling Code Block. See section
21.

**The exported level has no C#.**

That is expected. The `.level` contains GASM. Keep the `.grabproj` for source.

---

## 27. Worked examples

### 27.1 Bobbing platform (standalone Code Block)

```csharp
using Grab;

public class Bob : CodeBlock
{
    GameObject platform = GameObject.Find("Floor");
    float t;

    void Update()
    {
        t += Time.DeltaTime;
        platform.Position.Y = -0.5f + Mathf.Sin(t * 6f) * 0.25f;
    }
}
```

### 27.2 Trigger door (component)

```csharp
using Grab;

public class Door : CodeBlock
{
    GameObject door = GameObject.Find("Door");
    Trigger zone = GameObject.Find<Trigger>("Door Zone");

    [Range(0.5f, 5f)]
    public float speed = 2f;

    void Update()
    {
        if (zone.Active == 1)
        {
            door.Position.Y = Mathf.MoveTowards(door.Position.Y, 2f, speed * Time.DeltaTime);
        }
        else
        {
            door.Position.Y = Mathf.MoveTowards(door.Position.Y, 0f, speed * Time.DeltaTime);
        }
    }
}
```

Attach this to the Door. The component is a Code Block; `Find("Door")` is what actually binds it.

### 27.3 Live score sign

```csharp
using Grab;

public class ScoreSign : CodeBlock
{
    Sign sign = GameObject.Find<Sign>("Score Sign");

    void Update()
    {
        sign.Control = 1;
        sign.Write = string.Concat("Score: ", Level.Var1);
    }
}
```

### 27.4 Random colour flash

```csharp
using Grab;

public class Flash : CodeBlock
{
    GameObject cube = GameObject.Find("Wood Cube");

    [Range(0.05f, 2f)]
    public float interval = 0.5f;

    float timer;

    void Update()
    {
        timer += Time.DeltaTime;

        if (timer >= interval)
        {
            timer = 0;
            cube.Color = new Color(Random.Value, Random.Value, Random.Value);
        }
    }
}
```

### 27.5 Coroutine traffic light

```csharp
using Grab;

public class TrafficLight : CodeBlock
{
    GameObject lamp = GameObject.Find("Lamp");

    IEnumerator OpenSequence()
    {
        lamp.Color = new Color(1, 0, 0);
        yield return new WaitForSeconds(2f);

        lamp.Color = new Color(0, 1, 0);
        yield return new WaitForSeconds(2f);

        lamp.Color = new Color(0, 0, 1);
        yield return null;
    }

    void Start()
    {
        StartCoroutine(OpenSequence());
    }
}
```

For a repeating light, put the timing in `Update` and reset a timer, or start a fresh coroutine
from the state that should repeat. Do not put a `yield` inside a nested `if` or `while` and expect
it to resume there; yields must be top-level statements in the coroutine body.

### 27.6 Object-oriented state machine

```csharp
using Grab;

public interface IState
{
    void Tick(float dt);
}

public class IdleState : IState
{
    public void Tick(float dt) { }
}

public class RunState : IState
{
    public float Speed = 3f;

    public void Tick(float dt)
    {
        // ...
    }
}

public class Robot : CodeBlock
{
    IState state = new IdleState();
    float timer;

    void Update()
    {
        timer += Time.DeltaTime;

        if (timer > 2f)
        {
            timer = 0;
            state = new RunState();
        }

        state.Tick(Time.DeltaTime);
    }
}
```

Because `new RunState()` is a compile-time bundle, it reuses the same storage each time; treat it
as a persistent object, not a fresh heap allocation.

### 27.7 Voxel platform

```csharp
using Grab;

public class PlatformBuilder : CodeBlock
{
    void Start()
    {
        Voxel.Grid(new Vector3(0, 1, 0), 10, 1, 10, 1f, Shape.Cube, Material.Wood);

        Voxel.Ring(new Vector3(0, 2, 0), 8f, 32, Shape.Cube, Material.DefaultColored);
    }
}
```

Press F7 while not in Play to build the objects. They are part of the scene, not spawned at
runtime.

---

## 28. C# coverage cheat sheet

| Feature | Status |
|---|---|
| Variables, fields, constants, `readonly` | Yes |
| Methods, returns, overloads, `static` | Yes |
| `ref`, `out`, `params` | Yes |
| `if`, `else`, `while`, `do`, `for`, `switch` | Yes |
| `break`, `continue`, `return`, `goto` | Yes |
| `try` / `catch` / `finally` / `throw` | Yes, numeric error codes |
| `using` blocks | Yes, `IDisposable.Dispose()` inlined |
| `lock` | Parsed, no runtime effect |
| Arrays (`float[]`, `int[]`, `bool[]`) | Yes, max 32 |
| `List<T>`, `Dictionary<K,V>` | Yes, fixed capacity, numeric values |
| `foreach` | Yes for arrays/lists/dictionaries |
| Strings and string methods | Compile-time values |
| String interpolation | Yes at text sinks |
| `Vector3`, `Color` | Yes |
| Classes, structs, interfaces, abstract, inheritance | Yes |
| Constructors, auto-properties, enums | Yes |
| Generics | Yes, including generic collections |
| `virtual` / `override` | Yes |
| `base.Method` calls | No; put the inherited rule in the override or call a helper |
| `this` in an instance method of a user class | Yes; not available in the entry CodeBlock class |
| `yield`, `IEnumerator` coroutines | Yes, top-level yields |
| `async` / `await` | Yes, supported waits |
| `new` for scene objects | Yes, compile-time creation |
| `Voxel.Grid` / `Line` / `Ring` | Yes, compile-time creation |
| Lambdas, delegates, events, LINQ | No |
| Recursion | No |
| `unsafe`, `extern`, operator overloading | No |
| Runtime `ToString()` of a live number | No |
| `Vector2` | No (use `Vector3`) |
| Multi-dimensional / jagged arrays | No |
| `string.Split`, `ToCharArray` | No |
| `Dictionary.Keys` / `Values` at runtime | No |
| `List.Sort` / `List.Reverse` | No |
| Arbitrary .NET libraries, file/network/thread APIs | No |
| Unity's `MonoBehaviour`, `GetComponent`, `SendMessage` | No |

### Short translation table

| Unity habit | GRAB ENGINE |
|---|---|
| `GetComponent<Rigidbody>()` | No equivalent; use `GameObject.Find` and object properties |
| `transform.position` | `obj.Position` |
| `transform.rotation.eulerAngles` | `obj.Rotation` (Euler degrees) |
| `transform.localScale` | `obj.Scale` is read-only; set it in the Inspector or an initializer |
| `Instantiate(prefab)` | `new Cube(...)`, `Voxel.*` or a prefab stamp in the editor |
| `Destroy(obj)` | Edit the level; there is no runtime destroy |
| `Time.deltaTime` | `Time.DeltaTime` |
| `Mathf.Sin` | `Mathf.Sin` (radians) |
| `Debug.Log` | `Debug.Log` / `Console.WriteLine` |
| `StartCoroutine` | `StartCoroutine` (supported) |
| UI / input APIs | No direct equivalent; use player/trigger/object properties |
| `SerializeField` | Yes, values are compiler constants |
| `MonoBehaviour` on a GameObject | Script component is an editor link, not a runtime component |

---

## 29. Known rough edges

These are deliberate limits or current implementation details, not hidden behavior:

1. **A component is not attached at runtime.** Bind the host with `GameObject.Find`.
2. **The component card's stored label** can keep the name recorded when it was created (often
   `MyScript`) after you rename the class. The source and compiled class are still correct.
3. **`[Button]` does not draw a clickable button** in this Inspector; call the method yourself.
4. **`[TextArea]` / `[Multiline]`** are recorded but string fields use a single-line editor.
5. **`&&`, `||` and `?:` evaluate both sides.** Do not rely on short-circuiting.
6. **`new` is compile-time.** It cannot spawn at runtime or create one object per loop iteration.
7. **`List` / `Dictionary` do not grow.** `Add` past capacity is silently ignored.
8. **Strings are compile-time.** A live number can only be printed at a sign or Console.
9. **There is no `GetComponent`, `SendMessage` or cross-Code-Block method call.** Use
   `Level.Var1` … `Level.Var5`, object properties, or Trigger events.
10. **Generated continuation pages are hidden.** They are real Code Blocks in the export; look in
    the GASM output or the exported level rather than the Hierarchy.
11. **Hot reload restarts the entry point.** `Start` / `Awake` may run again.
12. **`FixedUpdate` is per-frame**, not a separate fixed timestep.
13. **Object and connection limits are real.** A single script can only touch 10 objects per
    page, and a single Code Block can hold 150 instructions.

---

If you need the low-level instruction set, register naming and protobuf details, see
`GASMReference.md`. If you need editor tools (terrain, prefabs, audio, animation), use the
corresponding GRAB ENGINE window; those tools write the same level data this compiler targets.
