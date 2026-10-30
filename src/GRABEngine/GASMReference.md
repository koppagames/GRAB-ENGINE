# GRAB Assembly Language – Reference

A tiny “programming language” you can use to read and change game objects.
Everything is numbers, and your program runs one line at a time.

## Contents

* [Value Model](#value-model)
* [Execution Model](#execution-model)
* [Registers](#registers)
* [Instruction Set](#instruction-set)
* [Objects](#objects)
* [Color Blocks](#color-blocks)
* [Lights](#lights)
* [Triggers](#triggers)
* [Signs](#signs)
* [Physics](#physics)
* [Players](#players--virtual-object-player)

## Extras
 * [ASCII Table](#ascii-table)

---

## Limits: editor vs. runtime

| Limit | Retail editor | Runtime / loaded `.level` |
|---|---|---|
| Instructions per Code Block | 150 | no limit — stored in a dynamic array |
| Object connections per Code Block | 10 | no limit — stored in a dynamic array |
| Registers per Code Block | 480 | no limit — dynamic arrays, custom names allowed |
| Trigger targets | 17 | enforced |
| Connection name length | 4 characters | 4 characters |

GRAB's GASM screen refuses to keep more than 150 instructions, 10 connections or 480 registers,
and it truncates a program pasted into it. The interpreter that executes a level does not use
those numbers: a Code Block that already contains a longer program is loaded and executed as-is,
which is also why custom register names written into a `.level` keep working.

GRAB ENGINE compiles to one extended Code Block by default, so the numbers in the first three rows
are informational. `Tools → Script Compiler` switches to split mode, which keeps every generated
page inside the editor's caps using continuation Code Blocks.

## Value Model

**Everything is a number. Even “true/false.”**

* All values are numeric (**floats**).
* Boolean convention: **0 = false**, **non-zero = true**.
* Select/Control fields expect documented integer values only.

**What it’s commonly useful for:** Conditions (`IF`), toggles, “on/off” switches.

### Samples

**Truthiness: how `IF` decides**

```asm
SET R0 0          ; false
SET R1 2          ; true (any non-zero)

IF R0 WillNotJump
IF R1 WillJump

LABEL WillNotJump
; (won't run)

LABEL WillJump
; (will run)
```

**Turn a comparison into a boolean**

```asm
SET R0 10
LESS R2 R0 20      ; R2 = 1 because 10 < 20
NOT R3 R2          ; R3 = 0 (not true)
```

---

## Execution Model

**Your code runs one instruction per step/frame.**

* One instruction executes per step.
* Labels resolve at compile time.
* Writing to read-only fields has no effect.
* Runtime errors are reported **when the failing instruction is executed** and **stop the program immediately**.

**What it’s commonly useful for:** Smooth movement (use `DeltaTime`), predictable pacing, understanding why errors “suddenly stop everything.”

### Samples

**Frame-based thinking: do a little every step**

```asm
LABEL Loop

; Each frame, add elapsed time.
ADD R0 R0 DeltaTime
; R0 is now “seconds since program started” (roughly).

SLEEP 0
GOTO Loop
```

---

## Registers

Registers are your “pockets” for temporary numbers.

### Working Registers (READ/WRITE)

```
R0 – R7
```

* Any float value.

### Special Register (READ ONLY)

```
DeltaTime
```

* Seconds since previous frame, `>= 0`.

**What it’s commonly useful for:** Turning “speed per second” into “movement per frame.”

### Samples

**Move `obj0` up at 2 units/sec**

```asm
LABEL Loop

; distance = speed * time
MUL R0 DeltaTime 2
ADD obj0.Pos.Y obj0.Pos.Y R0

SLEEP 0
GOTO Loop
```

**Make a timer that finishes after 3 seconds**

```asm
LABEL Loop

ADD R0 R0 DeltaTime     ; R0 accumulates time

LESS R1 R0 3            ; R1 = 1 while R0 < 3
NOT R2 R1               ; R2 = 1 once time >= 3

IF R2 Done
SLEEP 0
GOTO Loop

LABEL Done
; do the thing here
```

---

## Instruction Set

### Data

| Instruction | Form          | What it does    |
| ----------- | ------------- | --------------- |
| `SET`       | `SET DST SRC` | `DST = SRC`     |
| `SWAP`      | `SWAP A B`    | swap two values |

**What it’s commonly useful for:** Copying values, setting constants, juggling two numbers.

### Samples

**Copy a value and preserve the original**

```asm
SET R0 12
SET R1 R0        ; R1 now also 12
ADD R1 R1 5      ; R1 changes, R0 stays 12
SLEEP 0
```

**Swap two values**

```asm
SET R0 1
SET R1 99
SWAP R0 R1       ; R0=99, R1=1
SLEEP 0
```

---

### Arithmetic

| Instruction | Form          | What it does       |
| ----------- | ------------- | ------------------ |
| `ADD`       | `ADD DST A B` | `DST = A + B`      |
| `VEC_SET`   | `VEC_SET DST SRC` | `DST..DST+2 = SRC..SRC+2` |
| `VEC_ADD`   | `VEC_ADD DST A B` | `DST..DST+2 = A..A+2 + B..B+2` |
| `VEC_SUB`   | `VEC_SUB DST A B` | `DST..DST+2 = A..A+2 - B..B+2` |
| `VEC_MUL`   | `VEC_MUL DST A B` | `DST..DST+2 = A..A+2 * B..B+2` |
| `VEC_SCL`   | `VEC_SCL DST A S` | `DST..DST+2 = A..A+2 * S` |
| `VEC_LEN`   | `VEC_LEN DST A` | `DST = length(A..A+2)` |
| `VEC_DOT`   | `VEC_DOT DST A B` | `DST = dot(A..A+2, B..B+2)` |
| `SUB`       | `SUB DST A B` | `DST = A - B`      |
| `MUL`       | `MUL DST A B` | `DST = A * B`      |
| `DIV`       | `DIV DST A B` | `DST = A / B`      |
| `MOD`       | `MOD DST A B` | `DST = fmod(A, B)` |
| `FLOOR`     | `FLOOR DST A` | `DST = floor(A)`   |
| `SQRT`      | `SQRT DST A`  | `DST = sqrt(A)`    |

**Defined error behavior (when executed; program stops immediately)**

* `DIV` with `B == 0` -> error
* `MOD` with `B == 0` -> error
* `SQRT` with `A < 0` -> error
* Any vector instruction with a vector operand that does not have 3 consecutive registers -> error

**MOD definition**

* Uses `std::fmod(A, B)` (floating remainder).

**Vector instruction definition**

* Each vector operand names the first register of a 3-register vector.
* `VEC_ADD Player.Tel.Pos.X Player.Pos.X R0` writes `Player.Tel.Pos.X/Y/Z = Player.Pos.X/Y/Z + R0/R1/R2`.
* `VEC_SCL` takes a vector and a scalar. The scalar can be a register or a constant.
* Vector sources cannot be constants, special registers, or output-only registers. Vector destinations cannot be input-only registers.

**What it’s commonly useful for:** Movement, timers, scaling, repeating patterns.

### Samples

**Safe division pattern (avoid DIV by 0)**

```asm
; If R1 is 0, don’t divide.
EQUAL R2 R1 0
IF R2 SkipDivide

DIV R0 R0 R1
GOTO After

LABEL SkipDivide
; choose a fallback result
SET R0 0

LABEL After
SLEEP 0
```

**Repeating counter 0..4 (like animation frames)**

```asm
LABEL Loop

ADD R0 R0 1
MOD R0 R0 5

SLEEP 0
GOTO Loop
```

---

### Comparison (outputs 0 or 1)

| Instruction | Form              | True when… |
| ----------- | ----------------- | ---------- |
| `EQUAL`     | `EQUAL DST A B`   | `A == B`   |
| `LESS`      | `LESS DST A B`    | `A < B`    |
| `GREATER`   | `GREATER DST A B` | `A > B`    |

**What it’s commonly useful for:** If something reached a limit, timers finishing, range checks.

### Samples

**Only move up while below a max**

```asm
LABEL Loop

LESS R2 obj0.Pos.Y 10
IF R2 CanMoveUp
GOTO EndStep

LABEL CanMoveUp
MUL R0 DeltaTime 2
ADD obj0.Pos.Y obj0.Pos.Y R0

LABEL EndStep
SLEEP 0
GOTO Loop
```

---

### Logical (outputs 0 or 1)

| Instruction | Form          | What it does              |
| ----------- | ------------- | ------------------------- |
| `AND`       | `AND DST A B` | true if `A!=0` and `B!=0` |
| `OR`        | `OR DST A B`  | true if `A!=0` or `B!=0`  |
| `NOT`       | `NOT DST A`   | true if `A==0`            |

**What it’s commonly useful for:** Combining multiple “rules” into one decision.

### Samples

**Only act if trigger is active AND timer is done**

```asm
LABEL Loop

; Timer
ADD R0 R0 DeltaTime
GREATER R1 R0 2          ; R1 = 1 if R0 > 2 seconds

; Trigger
SET R2 trg0.Trigger.Active

; Combine
AND R3 R1 R2
IF R3 DoThing
SLEEP 0
GOTO Loop

LABEL DoThing
SET R0 0                 ; fire at most every 2 seconds
; action here

SLEEP 0
GOTO Loop
```

---

### Trigonometry (degrees)

| Instruction | Form            | What it does                 |
| ----------- | --------------- | ---------------------------- |
| `SIN`       | `SIN DST A`     | `DST = sin(A°)`              |
| `COS`       | `COS DST A`     | `DST = cos(A°)`              |
| `ATAN2`     | `ATAN2 DST A B` | `DST = degrees(atan2(A, B))` |

**Special case**

* `ATAN2(0, 0) = 0`

**What it’s commonly useful for:** Circles, bobbing, aiming, turning.

### Samples

**Bobbing up and down (simple wave)**

```asm
LABEL Loop

; R7 holds an angle in degrees
MUL R0 DeltaTime 90
ADD R7 R7 R0

SIN R1 R7           ; -1..1
MUL R1 R1 0.5       ; smaller bob
ADD obj0.Pos.Y 1.5 R1

SLEEP 0
GOTO Loop
```

**2D forward direction (from yaw, XZ plane)**

```asm
LABEL Loop

; Move obj0 in its current forward direction using yaw.
SIN R0 obj0.Rot.Y    ; forward X
COS R1 obj0.Rot.Y    ; forward Z
MUL R2 DeltaTime 2   ; speed * dt
MUL R0 R0 R2
MUL R1 R1 R2
ADD obj0.Pos.X obj0.Pos.X R0
ADD obj0.Pos.Z obj0.Pos.Z R1
SLEEP 0
GOTO Loop
```

**3D forward direction (from yaw + pitch)**

```asm
LABEL Loop

; Forward vector from current yaw/pitch (degrees).
SIN R0 obj0.Rot.Y     ; sinYaw
COS R1 obj0.Rot.Y     ; cosYaw
SIN R2 obj0.Rot.X     ; sinPitch
COS R3 obj0.Rot.X     ; cosPitch

MUL R4 R0 R3          ; dirX = sinYaw * cosPitch
SET R5 R2             ; dirY = sinPitch
MUL R6 R1 R3          ; dirZ = cosYaw * cosPitch

MUL R7 DeltaTime 2    ; speed * dt
MUL R4 R4 R7
MUL R5 R5 R7
MUL R6 R6 R7
ADD obj0.Pos.X obj0.Pos.X R4
ADD obj0.Pos.Y obj0.Pos.Y R5
ADD obj0.Pos.Z obj0.Pos.Z R6
SLEEP 0
GOTO Loop
```

**Look-at target (ATAN2 yaw + pitch)**

```asm
LABEL Loop

; Rotate obj0 to face obj1.
SUB R0 obj1.Pos.X obj0.Pos.X    ; dx
SUB R1 obj1.Pos.Y obj0.Pos.Y    ; dy
SUB R2 obj1.Pos.Z obj0.Pos.Z    ; dz

ATAN2 R3 R0 R2                  ; yaw = atan2(dx, dz)

MUL R4 R0 R0
MUL R5 R2 R2
ADD R4 R4 R5
SQRT R4 R4                      ; horizontal distance
ATAN2 R6 R1 R4                  ; pitch = atan2(dy, horizontal)

SET obj0.Rot.Y R3
SET obj0.Rot.X R6
SLEEP 0
GOTO Loop
```

---

### Flow

| Instruction | Form          | What it does       |
| ----------- | ------------- | ------------------ |
| `NOOP`      | `NOOP`        | does nothing       |
| `LABEL`     | `LABEL NAME`  | marks a label      |
| `GOTO`      | `GOTO NAME`   | jump to label      |
| `IF`        | `IF REG NAME` | jump if `REG != 0` |
| `END`       | `END`         | stop program       |

**Important habit:** If you make an endless loop, **include `SLEEP 0`** so the loop yields to the next frame.

**What it’s commonly useful for:** Repeat forever, build simple state machines, do one “tick” per frame.

### Samples

**Endless loop that cooperates with frames (recommended)**

```asm
LABEL Loop

; do one frame of work here
ADD R0 R0 1

SLEEP 0
GOTO Loop
```

**Branching: pick one path**

```asm
LABEL Loop

LESS R0 obj0.Pos.Y 5
MUL R1 DeltaTime 1
IF R0 MoveUp
GOTO MoveDown

LABEL MoveUp
ADD obj0.Pos.Y obj0.Pos.Y R1
GOTO EndStep

LABEL MoveDown
SUB obj0.Pos.Y obj0.Pos.Y R1

LABEL EndStep
SLEEP 0
GOTO Loop
```

---

### Misc

| Instruction | Form         | What it does            |
| ----------- | ------------ | ----------------------- |
| `RAND`      | `RAND DST N` | random float in a range |
| `SLEEP`     | `SLEEP MS`   | pause                   |

**RAND behavior (float, upper bound exclusive)**

* If `N ≥ 0`: `0 ≤ x < N`
* If `N < 0`: `N < x ≤ 0`

**SLEEP behavior**

* `MS` is milliseconds and may be fractional.
* Timing checked **once per frame** (frame-quantized).
* `SLEEP 0` waits until next frame.
* Negative values behave like `SLEEP 0`.

**What it’s commonly useful for:** Blinking, randomness, pacing text, frame-friendly loops.

### Samples

**Random “sparkle” color every frame**

```asm
LABEL Loop

RAND R0 256
RAND R1 256
RAND R2 256

SET obj0.Col.Select 0
SET obj0.Col.R R0
SET obj0.Col.G R1
SET obj0.Col.B R2

SLEEP 0
GOTO Loop
```

**Blink physics on/off every ~0.25 seconds**

```asm
LABEL Loop

ADD R0 R0 DeltaTime
GREATER R1 R0 0.25
IF R1 Toggle
SLEEP 0
GOTO Loop

LABEL Toggle
SET R0 0
NOT R2 R2
SET obj0.Physics.Enabled R2

SLEEP 0
GOTO Loop
```

---

## Objects

Objects you link expose fields you can read/write like registers.

**What it’s commonly useful for:** Move or rotate stuff, read scale, build simple animations.

### Common Fields

**READ/WRITE**

* `obj0.Pos.X`, `obj0.Pos.Y`, `obj0.Pos.Z` — world units (float)
* `obj0.Rot.X`, `obj0.Rot.Y`, `obj0.Rot.Z` — **degrees** (float)

  * Incrementing one past 180 degrees can make the others flip and rotating more actually means decrementing from then on.

**READ ONLY**

* `obj0.Sca.X`, `obj0.Sca.Y`, `obj0.Sca.Z` — scale (float)

  * All objects fitted into a 1m cube; scale is a multiplier.

### Samples

**Move forward on X at 1.5 units/sec**

```asm
LABEL Loop

MUL R0 DeltaTime 1.5
ADD obj0.Pos.X obj0.Pos.X R0

SLEEP 0
GOTO Loop
```

**Spin slowly around Y (45°/sec)**

```asm
LABEL Loop

MUL R0 DeltaTime 45
ADD obj0.Rot.Y obj0.Rot.Y R0

SLEEP 0
GOTO Loop
```

---

## Color Blocks

**What it’s commonly useful for:** Changing “paint” (diffuse) or “shiny highlight” (specular).

**READ/WRITE**

* `obj0.Col.R`, `obj0.Col.G`, `obj0.Col.B`
* `obj0.Col.Select`

**Ranges / storage**

* `Col.R/G/B`: clamped to **0..255** (floats allowed)
* `Col.Select`: **0 or 1** (default 0)

  * `0 = diffuse`
  * `1 = specular`

**Select semantics**

* Diffuse and specular stored separately.
* Changing `Col.Select` updates `Col.R/G/B` immediately.
* Writing `Col.R/G/B` affects only current selection.

### Samples

**Set diffuse to red and specular to white**

```asm
SET obj0.Col.Select 0
SET obj0.Col.R 255
SET obj0.Col.G 0
SET obj0.Col.B 0

SET obj0.Col.Select 1
SET obj0.Col.R 255
SET obj0.Col.G 255
SET obj0.Col.B 255
```

**Pulse brightness (breathing effect)**

```asm
LABEL Loop

MUL R0 DeltaTime 90
ADD R7 R7 R0

SIN R1 R7             ; -1..1
ADD R1 R1 1           ; 0..2
MUL R1 R1 127.5       ; 0..255

SET obj0.Col.Select 0
SET obj0.Col.R R1
SET obj0.Col.G R1
SET obj0.Col.B R1

SLEEP 0
GOTO Loop
```

---

## Lights

**What it’s commonly useful for:** Changing light color, brightness, and range.

**READ/WRITE**

* `obj0.Light.Color.R`, `obj0.Light.Color.G`, `obj0.Light.Color.B`
* `obj0.Light.Brightness`
* `obj0.Light.Range`

**Ranges / storage**

* `Light.Color.R/G/B`: clamped to **0..255** (floats allowed)
* `Light.Brightness`: clamped to **0..10**
* `Light.Range`: clamped to **0..100**

**Override semantics**

* Light fields are runtime overrides from code.
* Authored light settings are not changed by these writes.
* On state reset, values revert to the authored light settings.

### Samples

**Set warm light color and medium brightness**

```asm
SET obj0.Light.Color.R 255
SET obj0.Light.Color.G 180
SET obj0.Light.Color.B 120
SET obj0.Light.Brightness 4.5
SLEEP 0
```

**Pulse light range**

```asm
LABEL Loop

MUL R2 DeltaTime 90
ADD R0 R0 R2
SIN R1 R0
ADD R1 R1 1
MUL R1 R1 5
ADD obj0.Light.Range 2 R1

SLEEP 0
GOTO Loop
```

---

## Triggers

Triggers are invisible “buttons” you walk into.

### Fields

**READ ONLY**

* `trg0.Trigger.Active` — `1` while triggered else `0`
* `trg0.Trigger.PlayerId` — stable player id `0..255`

  * `255 = no player`

**READ/WRITE**

* `trg0.Trigger.ForceActive` — force trigger on/off (non-zero = on)

### ForceActive semantics

* While `trg0.Trigger.ForceActive != 0`, `trg0.Trigger.Active` is considered active (`1`).
* `trg0.Trigger.ForceActive` stays active until you set it back to `0`.
* Changing `trg0.Trigger.ForceActive` fires trigger events:

  * `0 → 1`: triggers the **On Enter** event
  * `1 → 0`: triggers the **On Leave** event
  * Writing same value again does **not** fire events

**What it’s commonly useful for:** Doors, score zones, “start when someone steps here.”

### Samples

**Control `obj0` physics while inside `trg0`**

```asm
LABEL Loop

IF trg0.Trigger.Active In
GOTO Out

LABEL In
SET obj0.Physics.Enabled 0
GOTO EndStep

LABEL Out
SET obj0.Physics.Enabled 1

LABEL EndStep
SLEEP 0
GOTO Loop
```

**Force-trigger for testing**

```asm
SET trg0.Trigger.ForceActive 1   ; fires On Enter
SLEEP 500
SET trg0.Trigger.ForceActive 0   ; fires On Leave
```

---

## Signs

Signs are your output display.

### WRITE

* `sig0.Sign.Write` — append output (depends on `sig0.Sign.Select`)
* `sig0.Sign.Select` — `0=ASCII`, `1=NUMBER`
* `sig0.Sign.Control` — `1=clear`, `2=delete last character`
* `sig0.Sign.Number` — clear and set number directly (ignores `sig0.Sign.Select`)

### Modes

**0 – ASCII (default)**

* `sig0.Sign.Write` expects `0..255` and appends that ASCII character (see [ASCII Table](#ascii-table)).

**1 – NUMBER**

* `sig0.Sign.Write` appends the number as text.

### Capacity

* Maximum length: **150 characters**
* When full: appending stops (extra writes ignored)

**What it’s commonly useful for:** Debugging values (“what is my timer?”), showing counts/messages.

### Samples

**Clear and print a changing timer value**

```asm
LABEL Loop

ADD R0 R0 DeltaTime

SET sig0.Sign.Control 1
SET sig0.Sign.Select 1
SET sig0.Sign.Write R0

SLEEP 0
GOTO Loop
```

**Write “HI” in ASCII**

```asm
SET sig0.Sign.Control 1
SET sig0.Sign.Select 0

SET sig0.Sign.Write 72     ; 'H'
SET sig0.Sign.Write 73     ; 'I'
```

---

## Physics

### Fields

**READ/WRITE**

* `obj0.Physics.Enabled` — enable/disable physics (non-zero = enabled)

**READ ONLY**

* `obj0.Physics.State` — current physics state: `0=not a physics object`, `1=physics disabled by code (static)`, `2=sleeping physics object`, `3=kinematic physics object (often held/player-driven)`, `4=active physics object`, `5=simulation disabled (selected in editor and similar)`

**What it’s commonly useful for:** Freeze/unfreeze objects, turn physics off for decorations.

### Samples

**Toggle physics on `obj0` every second**

```asm
LABEL Loop

ADD R0 R0 DeltaTime
GREATER R1 R0 1
IF R1 Flip
SLEEP 0
GOTO Loop

LABEL Flip
SET R0 0
NOT R2 R2
SET obj0.Physics.Enabled R2

SLEEP 0
GOTO Loop
```

---

## Players – Virtual Object: `Player`

This “virtual object” lets you read info about players by selecting one.

**What it’s commonly useful for:** Find where players are, read names/colors, react to the local player.

### WRITE (selection / control)

* `Player.SelectIndex` — select by index `0..Player.Count-1`
* `Player.SelectId` — select by stable player id `0..255`
* `Player.SelectPart` — select body part:

  * `0=head`, `1=body`, `2=feet`, `3=left hand`, `4=right hand`
* `Player.Control` — name reading:

  * `1=reset`, `2=step`
* `Player.Respawn` — set to `1` to respawn the selected player (**WRITE ONLY**, auto-resets)
* `Player.Tel.Pos.X`, `Player.Tel.Pos.Y`, `Player.Tel.Pos.Z` — teleport feet position for the selected local player (**WRITE ONLY**)
* `Player.Tel.Rot.Y` — teleport head yaw in degrees for the selected local player (**WRITE ONLY**)
* `Player.Tel.Apply` — set to `1` to teleport the selected local player (**WRITE ONLY**, auto-resets)
* `Player.Col.Select` — selects which player color `Player.Col.*` shows (**READ/WRITE**):

  * `0=main`, `1=detail`
* `Player.Vel.X`, `Player.Vel.Y`, `Player.Vel.Z` — selected local player velocity (**READ/WRITE**)

### READ ONLY (data)

* `Player.Count` — player count (`>= 0`)
* `Player.Valid` — `1` if selection resolves, else `0`
* `Player.Id` — selected player stable id (`0..255`)
* `Player.LocalId` — stable id (`0..255`) of the player running this code block
* `Player.Pos.X`, `Player.Pos.Y`, `Player.Pos.Z` — selected part position
* `Player.Rot.X`, `Player.Rot.Y`, `Player.Rot.Z` — selected part rotation (**degrees**)
* `Player.Name` — ASCII character `0..255`

  * `0 = terminator`
  * After the terminator, stepping continues to return `0`
* `Player.Col.R`, `Player.Col.G`, `Player.Col.B` — player color channel values `0..255` (**READ ONLY**)

### Player color semantics

* `Player.Col.Select` is **READ/WRITE** and returns `0` or `1`.
* Changing `Player.Col.Select` immediately updates `Player.Col.R/G/B` to the selected color:

  * `0 = player main color`
  * `1 = player detail color`

### Player velocity

* `Player.Vel.*` is the selected local player's world-space velocity.
* Writing one velocity axis changes that axis immediately. Axes you do not write keep their current velocity.
* Non-local selected players read as `0` because their full velocity vector is not synced.
* Replicated remote code block updates do not execute velocity writes.

### Player teleport

* `Player.Tel.Pos.*` starts at the selected local player's current feet position.
* Write the position axes you want to change and `Player.Tel.Rot.Y` if you want to change yaw.
* Set `Player.Tel.Apply` to `1` to teleport. It resets to `0`.
* Position axes you do not write stay at the player's current feet position.
* After teleporting, `Player.Tel.Pos.*` and `Player.Tel.Rot.Y` reset to the player's current feet position and head yaw.
* Replicated remote code block updates do not execute teleport writes.

### Samples

**Track the local player’s head position onto `obj0`**

```asm
LABEL Loop

SET Player.SelectId Player.LocalId
SET Player.SelectPart 0          ; head

IF Player.Valid HavePlayer
GOTO EndStep

LABEL HavePlayer
SET obj0.Pos.X Player.Pos.X
SET obj0.Pos.Y Player.Pos.Y
SET obj0.Pos.Z Player.Pos.Z

LABEL EndStep
SLEEP 0
GOTO Loop
```

**Read player 0’s name and append it to `sig0` (ASCII)**

```asm
SET Player.SelectIndex 0
IF Player.Valid Start
GOTO Done

LABEL Start
SET Player.Control 1      ; reset reading
SET sig0.Sign.Select 0    ; ASCII mode

LABEL NextChar
SET Player.Control 2      ; step
EQUAL R0 Player.Name 0    ; terminator?
IF R0 Done

SET sig0.Sign.Write Player.Name
GOTO NextChar

LABEL Done
SLEEP 0
```

---

## ASCII Table

| Character | Decimal Code |
|-----|----|
| New Line (LF) | 10 |
| Space | 32 |
| ! | 33 |
| " | 34 |
| # | 35 |
| $ | 36 |
| % | 37 |
| & | 38 |
| ' | 39 |
| ( | 40 |
| ) | 41 |
| \* | 42 |
| + | 43 |
| , | 44 |
| - | 45 |
| . | 46 |
| / | 47 |
| 0 | 48 |
| 1 | 49 |
| 2 | 50 |
| 3 | 51 |
| 4 | 52 |
| 5 | 53 |
| 6 | 54 |
| 7 | 55 |
| 8 | 56 |
| 9 | 57 |
| : | 58 |
| ; | 59 |
| < | 60 |
| = | 61 |
| > | 62 |
| ? | 63 |
| @ | 64 |
| A | 65 |
| B | 66 |
| C | 67 |
| D | 68 |
| E | 69 |
| F | 70 |
| G | 71 |
| H | 72 |
| I | 73 |
| J | 74 |
| K | 75 |
| L | 76 |
| M | 77 |
| N | 78 |
| O | 79 |
| P | 80 |
| Q | 81 |
| R | 82 |
| S | 83 |
| T | 84 |
| U | 85 |
| V | 86 |
| W | 87 |
| X | 88 |
| Y | 89 |
| Z | 90 |
| [ | 91 |
| \\ | 92 |
| ] | 93 |
| ^ | 94 |
| _ | 95 |
| \` | 96 |
| a | 97 |
| b | 98 |
| c | 99 |
| d | 100 |
| e | 101 |
| f | 102 |
| g | 103 |
| h | 104 |
| i | 105 |
| j | 106 |
| k | 107 |
| l | 108 |
| m | 109 |
| n | 110 |
| o | 111 |
| p | 112 |
| q | 113 |
| r | 114 |
| s | 115 |
| t | 116 |
| u | 117 |
| v | 118 |
| w | 119 |
| x | 120 |
| y | 121 |
| z | 122 |
| { | 123 |
| \| | 124 |
| } | 125 |
| ~ | 126 |
