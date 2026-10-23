# Regex 101

With **Regex** off, a trigger searches for the text you entered, ignoring letter case. Turn **Regex** on to use the pattern syntax below or capture codes such as `{S1}` and `{N}`. Patterns match the message without its date/time prefix.

## 🔹 **Basics**

- **`.`** — Matches **any single character** (except new line)  
  _Example: `a.c` matches "abc", "axc", "a-c", etc._
- **`*`** — Means "**zero or more**" of the thing before it  
  _Example: `a*` matches "", "a", "aa", "aaa", etc._
- **`+`** — Means "**one or more**" of the thing before it  
  _Example: `a+` matches "a", "aa", "aaa", but not "" (empty)_
- **`?`** — Makes the thing before it **optional** (zero or one)  
  _Example: `colou?r` matches "color" or "colour"_
- **`[...]`** — Matches **one character** from inside the brackets  
  _Example: `[abc]` matches "a", "b", or "c" (just one of them)_
- **`[^...]`** — Matches **one character NOT** in the brackets  
  _Example: `[^0-9]` matches any character that's NOT a number_

## 🔹 **Anchors (Position Matchers)**

- **`^`** — The **start** of the line/text  
  _Example: `^Hello` matches only if "Hello" is at the very start_
- **`$`** — The **end** of the line/text  
  _Example: `bye$` matches only if "bye" is at the very end_

## 🔹 **Shortcuts (Character Types)**

- **`\d`** — Any **digit** (0 to 9)
- **`\w`** — Any **word character** (letter, digit, or underscore)
- **`\s`** — Any **whitespace** (space, tab, etc.)
- **`\b`** — The **edge of a word** (word boundary)

## 🔹 **Grouping and OR**

- **`( ... )`** — Groups things together  
  _Example: `(cat|dog)` matches "cat" or "dog"_
- **`|`** — Means "**or**"  
  _Example: `yes|no` matches "yes" or "no"_

## 🔹 **Specials**

- **`\`** — **Escapes** a special character so it’s treated as normal  
  _Example: `\.` matches a real dot, not "any character"_
- **Special Character List:** .   ^   $   *   +   ?   (   )   [   ]   {   }   \   |

## 🔹 **Counts (Repetition)**

- **`{n}`** — **Exactly** n times  
  _Example: `a{3}` matches "aaa" only_
- **`{n,}`** — **At least** n times  
  _Example: `a{2,}` matches "aa", "aaa", "aaaa", etc._
- **`{n,m}`** — **Between** n and m times  
  _Example: `a{2,4}` matches "aa", "aaa", or "aaaa"_

## ⚠️ **.NET Regex Syntax**

- **No slashes needed:** Just type your pattern (don’t use `/like this/`)
- **Ignores letter case by default:** `cat` also matches `Cat`
- **Literal spaces and punctuation must match:** Escape regex symbols when you mean the character itself, such as `\.` for a period
- **Named groups are supported:** Capture a name with `(?<caster>\w+)`, then use `{caster}` in display or spoken text
- **Output uses braces, not `$1`:** Use `{s1}` for a `{S1}` capture, or `{caster}` for a group named `caster`

## 🚀 **Performance Tips**

- **Keep patterns simple and specific** for fastest results.
- **Avoid `.*` in the middle** of patterns; use only if necessary.
- **Avoid nested repetitions** such as `(.*a.*)*`; these can take a long time to check.
- **Start with a specific word or phrase** (e.g., `^You take`) instead of wildcards.
- **Don’t match entire blocks of text** or paragraphs.
- **Avoid long runs of wildcards and optionals** (like `(.*a.*)*`).



# Trigger Variables

Capture codes such as `{S1}` and `{N}` read values from a trigger's **Pattern** so you can use them in messages. Other codes insert information such as your character's name or the time of the match. Each entry below lists where it can be used.

Code names ignore letter case: `{c}` and `{C}` mean the same thing. A value captured as `{S1}` can be referenced as `{s1}`. The x in `{Sx}` or `{Nx}` is one digit, `0` through `9`, so one pattern can capture several values.

### Formatting Captured Values

Add a modifier to change how a value appears in output, not what the Pattern matches:

| Modifier | Effect | Example |
|---|---|---|
| `.number` | Formats a number with local thousands separators and no decimal places | `{n.number}` |
| `.capitalize` | Uppercases the first character | `{s1.capitalize}` |
| `.lower` | Converts text to lowercase | `{s1.lower}` |
| `.upper` | Converts text to uppercase | `{s1.upper}` |

Padding modifiers are described below.

### Padding and Centering Modifiers

The `.padleft`, `.padright`, and `.center` modifiers allow you to format text with padding for aligned display. These require a width argument specified after a colon.

- **`.padleft:width`** - Pads the value with spaces on the left to reach the specified total width
- **`.padright:width`** - Pads the value with spaces on the right to reach the specified total width
- **`.center:width`** - Centers the value within the specified width by adding spaces on both sides

Example Usage:

- `{S1.padleft:20}` - Left-pads the captured value to 20 characters total
- `{S1.padright:20}` - Right-pads the captured value to 20 characters total
- `{S1.center:20}` - Centers the captured value within 20 characters

These are useful for creating aligned columns in display text or when formatting output for overlays.

## `{C}`

Replaced by the character name for the monitored log. Use it in Pattern fields or in display, spoken and shared messages, including timer warnings.

## `{S}`, `{Sx}`

Captures one or more characters, including spaces and multiple words, from a **Pattern**. Requires **Regex**. For example, `^{S1} begins casting` captures the text before ` begins casting`; use `{s1}` in your message to display or speak it.

## `{N}`, `{Nx}`

Captures a whole, non-negative number from a **Pattern**. Requires **Regex**. The capture reads digits only; signs, decimal points and thousands separators are not part of the captured value. Use `{n}` or `{n1}` in your message to display or speak the number.

## `{N>y}`, `{y<N<z}`

Adds a numeric check to an `{N}` capture. Requires **Regex**. Use `>`, `<`, `>=`, `<=` or `==`, or put a lower and upper bound around N:

- `{N>=100}` matches 100 or more
- `{100<=N<200}` matches 100 through 199

For more complex checks, capture the number first and use the **Match Variables** field. `|` is not a way to combine checks inside an `{N...}` code.

## `{L}`

Replaced by the line that triggered the event minus the date/time segment. Useful for testing and seeing the full line that matched. Available only in the `Text to Display`, `Text to Speak`, and `Alternate Timer Name` fields.

## `{LOGTIME}`

Replaced by the time from the line that triggered the event in the `hh:mm:ss` format. Useful if you want to know the time the trigger was fired. Available only in the `Text to Display`, `Text to Speak`, `Text to Send`, and `Alternate Timer Name` fields.

## `{REPEATED}`

Counts repeated firings that produce the same message. Available in **Text to Display**, **Text to Speak** and **Alternate Timer Name**. For example, `{s1} {repeated}` counts separately for each captured name used in that message.

**Repeated Reset Time (s)** controls the counting window; the default is 0.75 seconds:

- With **Sliding Reset** off, the window starts with the first firing. A match after that window starts a new count
- With **Sliding Reset** on, each firing extends the window. The count restarts after a gap longer than the configured time

The same reset settings apply to `{COUNTER}`.

## `{COUNTER}`

Counts trigger firings regardless of the captured values used in the message. It uses the same **Repeated Reset Time** and **Sliding Reset** settings as `{REPEATED}`. This built-in count is separate from custom variables with the **Counter** data type.

## `{TS}`

Like `{S}` but used to capture a timestamp in the format `hh:mm:ss` or any number which will be counted as seconds. Requires `Regex` to be enabled. Used only to dynamically set the `Timer Duration`.

## `{NULL}`

Used in any field that displays, speaks, or shares information to suppress the message entirely. Useful when overriding `Timer End Early` behavior. If `{NULL}` is set then nothing will be displayed or spoken.

## `{TIMER-WARN-TIME-VALUE}`

Replaced by the `Timer` setting for `Warn With Time Remaining`. This allows your Display/Speak messages to reference this `Trigger` configuration value if needed. More variables like this could be added in the future where configuration settings are made available when triggers run.

## `{EQLP:STOP}`

A chat command, not a Pattern variable. Start one of your own chat messages with `{EQLP:STOP}` to stop current trigger audio and clear active trigger timers and overlays. It does not reload or disable your triggers; enabled triggers can fire again on later matches.

## `{EQLP:CLEAR}`

A chat command, not a Pattern variable. Start one of your own chat messages with `{EQLP:CLEAR}` to clear custom variables, their custom Counter values and expiry times across all active trigger processors.

It does not stop audio, close overlays or reset the built-in `{COUNTER}` and `{REPEATED}` histories. Use it when you want to clear variable state without interrupting active timers or alerts.

---

## Creating Custom Variables (Trigger Variables Tab)

Custom variables let triggers remember information and share it with other triggers. For example, one trigger can remember that burn mode is on, and another can check that value before speaking.

These are sometimes called **global trigger variables** because they are shared across triggers for the **same monitored character**, not just the trigger that sets them. Each monitored character has separate values, and the values are not saved across application restarts. The `g` in a name such as `gBurn` is just a naming convention; it does not change how the variable works.

There are three places to use them:

| Where | What you enter | What it does |
|---|---|---|
| **Trigger Variables** tab | Variable Name `gBurn`, Action Type **Set Value**, Value `true` | Stores a value when this trigger fires |
| **Match Variables** under Basic Trigger Options | `{gBurn} = true` | Allows a matching trigger to fire only while that value is true |
| An output field, such as **Text to Display** | `Burn mode: {gBurn}` | Inserts the stored value into the message |

**Match Variables checks a value; it does not set one.** Use the Trigger Variables tab to set or clear values. See the [worked examples](#example-1-turn-an-alert-on-and-off) below for complete trigger setups.

To add a variable action, open **Trigger Manager**, select a trigger and open its **Trigger Variables** tab. The settings are:

### Action Type

**Set Value** stores a value in the variable. **Clear Value** removes it.

### Variable Name

Enter a name such as `gCaster` or `gSpellName`, **without braces**. Use letters, digits and underscores, with no spaces. Refer to it elsewhere with braces: `{gCaster}`.

Use the same name in every trigger that should share the value. Names ignore letter case, so `gCaster` and `gcaster` refer to the same variable.

### Data Type

- **Value** stores text or a number, including values captured by a Pattern.
- **Counter** stores a number that changes each time the trigger fires.

### Value

The value to store. This can be:

- A capture group, such as `{s1}`
- Another variable, such as `{otherVar}`
- A literal value, such as `Red Dragon`

### Initial Value

*Counter only.* The value before the first increment. The default is `0`. Step is added on the first firing too, so the default Initial Value of 0 and Step of 1 produce **1** on the first firing, then 2, 3 and so on.

### Step

*Counter only.* The amount added each time the trigger fires. The default is `1`. Use a negative value to decrement.

### Time To Live

*Optional.* The number of seconds before the stored value expires. Each time an action sets or increments the variable, its lifetime starts again. Set this to `0` for no expiration; you can still remove it with **Clear Value**.

### Adding and Removing Variables

- Click the **+ Add Variable** button at the bottom of the tab to add a new variable card.
- Click the **Remove** button on any card to delete it.
- If all cards are deleted, a blank starter card is automatically added back.

### Examples

| Value | What Gets Stored |
|---|---|
| `{otherVar}` | The current value of another custom variable |
| `Red Dragon` | The literal text "Red Dragon" |
| `{s1} the Brave` | The captured value with appended text |

### Clearing Variables

Use a **Clear Value** action with the same Variable Name to remove a stored value. Leave the Value field unused for that action.

For example, one trigger can set `gBurn` to `true` when burn mode starts, and another can clear `gBurn` when it ends. An alert with **Match Variables** set to `{gBurn} = true` passes only while burn mode is on. The complete setup is shown below.

Clearing a variable is different from storing the text `false`: a cleared variable is unset (`null`), while a variable containing `false` still has a value.

### End Clear Variables (Timer Tab)

The **End Clear Variables** field (found under *Basic Timer Options* on the Timer tab) lets you specify a list of custom variables to clear automatically when a Timer ends — whether it expires normally or ends early via an End Early Pattern. This is useful for cleaning up temporary variables that were only needed while the timer was active.

Enter variable names separated by commas, spaces, or semicolons. The following formats are all accepted and resolve to the same variable name:

- `gCaster` — plain name
- `{gCaster}` — braces (same as display text syntax)
- `${gCaster}` — dollar-brace
- `gCaster, gSpellName; gZone` — multiple names with mixed separators

Variables are cleared **after** the End Text to Display and End Sound/Text to Speak are processed, so end display text can still reference the variable values. The cleanup happens once all timer-end side effects (display, speak, log) are complete.

**Example — Caster name that dies with the buff timer:**

1. Enable **Regex** and use Pattern `^{S1} begins casting Spirit of Vesagran` → Set `gEpicCaster = {s1}`
2. Enable Timer, Duration: 3:00, End Text to Display: `BRD Epic ({gEpicCaster})`
3. End Clear Variables: `gEpicCaster`

When the 3-minute buff timer ends, the display shows the caster name and the variable is cleared.

### Dynamic Timer Name (Timer Tab)

**Dynamic Timer Name** (under *Basic Timer Options*) is checked by default. While it is checked, a Timer name that references a custom variable stays live: when another trigger changes that variable, the Timer bar on screen updates to the new value (within about half a second). This is what you want for stacks, counts or casters that change while the Timer runs.

Uncheck **Dynamic Timer Name** to pin the name instead. The Timer keeps the name it resolved when it started and ignores later changes to the variable.

- Restarting still works as expected either way. If the trigger fires again and **If Triggered Again** restarts the Timer, or a **Looping** Timer starts its next loop, that new run captures whatever the value is at that moment. Pinned means the name holds for one run.
- The built-in codes (`{COUNTER}`, `{REPEATED}`, `{LOGTIME}`) are captured when the Timer starts either way, so they do not move.
- Unchecking also avoids an awkward Timer bar. Clearing a variable, or letting its **Time To Live** expire mid-Timer, leaves the raw `{variable}` text on a live name because there is no value left to substitute.

**Example — label a buff with its caster without chasing later casts:**

1. Enable **Regex** and use Pattern `^(?<s1>\w+) begins casting Spirit of Vesagran` with a **Set Value** action for `gEpicCaster = {s1}`.
2. Alternate Timer Name: `Vesagran ({gEpicCaster})`, Duration 3:00.
3. Uncheck **Dynamic Timer Name**. The variable action still fills the name when this Timer starts, but if another player refreshes the buff halfway through and rewrites `gEpicCaster`, the bar keeps the original caster until it expires.

---

## Match Variables Field

**Match Pattern asks: “Did this message happen?” Match Variables asks: “Should this trigger fire in the current situation?”**

Find **Match Variables** under **Basic Trigger Options**. Leave it blank if the Pattern is the only check you need. Otherwise, enter a check such as `{gBurn} = true` or `{gZone} = "The Plane of Knowledge"`.

A trigger runs in this order:

1. Its **Match Pattern** must match the incoming message, along with **Match Previous Line** if you use it.
2. **Match Variables** checks the stored values and any values captured by the matching pattern.
3. If the check passes, the trigger runs its variable actions, messages and timer actions. If it fails, none of those actions run.

**The condition is checked before this trigger sets or increments variables.** It sees their existing values, not the values this firing would write. Changing a variable alone does not fire another trigger: that trigger still needs its own Pattern match.

### Example 1: Turn an Alert On and Off {#example-1-turn-an-alert-on-and-off}

This example uses your own `/say` messages so you can try it without waiting for a particular encounter. Create three triggers for the same monitored character. Keep **Regex off** for all three, save them, and enable both the triggers and that character's trigger monitoring.

| Trigger | Match Pattern | Match Variables | Action |
|---|---|---|---|
| Burn on | `You say, 'burn on'` | Leave blank | Set `gBurn` to `true` |
| Burn check | `You say, 'burn check'` | `{gBurn} = true` | Speak `Burn mode is on` |
| Burn off | `You say, 'burn off'` | Leave blank | Clear `gBurn` |

For **Burn on**, add a card on the **Trigger Variables** tab:

- **Action Type:** Set Value
- **Variable Name:** `gBurn` — no braces
- **Data Type:** Value
- **Value:** `true` — no quotes
- **Time To Live:** `0`

For **Burn check**, put `{gBurn} = true` in **Match Variables**, and `Burn mode is on` in **Sound/Text to Speak**. It does not need a variable action.

For **Burn off**, add a variable card with **Action Type = Clear Value** and **Variable Name = gBurn**. It does not need a Value.

Try these commands in EverQuest, in order:

1. `/say burn off`, then `/say burn check` → no alert, because `gBurn` is unset.
2. `/say burn on`, then `/say burn check` → the check trigger speaks.
3. `/say burn off`, then `/say burn check` → no alert, because `gBurn` was cleared.

For an encounter, replace these Match Patterns with the messages that start burn mode, need the alert and end burn mode. The variable setup and condition stay the same.

### Example 2: Remember a Zone and Check It Later {#example-2-remember-a-zone-and-check-it-later}

One trigger can capture a value and make it available to other triggers. Create a **Remember zone** trigger:

| Setting | Enter |
|---|---|
| **Regex** | On |
| **Match Pattern** | `^You have entered {S1}\.$` |
| **Match Variables** | Leave blank |
| **Variable action** | Set Value, Variable Name `gZone`, Data Type Value, Value `{s1}`, Time To Live `0` |

The message `You have entered The Plane of Knowledge.` stores `The Plane of Knowledge` in `gZone`. Enter `{s1}` in the action's **Value** field, not its Variable Name field.

Then create a **Check zone** trigger:

| Setting | Enter |
|---|---|
| **Regex** | Off |
| **Match Pattern** | `You say, 'zone check'` |
| **Match Variables** | `{gZone} = "The Plane of Knowledge"` |
| **Sound/Text to Speak** | `Zone is {gZone}` |

After the zone message, `/say zone check` speaks only if the stored zone matches. Entering a different zone replaces the stored value, so the check no longer passes. Use the full zone name you want to check; quotes keep a name containing spaces together.

To accept several zones, use:

```text
{gZone} = "The Plane of Knowledge" or {gZone} = "The Plane of Tranquility"
```

### Example 3: Count Events, Then Check the Count {#example-3-count-events-then-check-the-count}

Use a custom **Counter** when several triggers need the same running total. This example counts `/say add seen` messages, then checks the total on a separate message:

| Trigger | Match Pattern (Regex off) | Match Variables | Action |
|---|---|---|---|
| Count an add | `You say, 'add seen'` | Leave blank | Set `gAdds` using Data Type Counter, Initial Value `0`, Step `1`, Time To Live `0` |
| Check adds | `You say, 'add check'` | `{gAdds} >= 3` | Speak `{gAdds} adds counted` |
| Reset adds | `You say, 'reset adds'` | Leave blank | Clear `gAdds` |

Use **Set Value** as the Action Type for the counter card. The first `add seen` stores 1, the second stores 2 and the third stores 3. An `add check` message is silent at 1 or 2, and speaks at 3 or more. `reset adds` clears the count so the next `add seen` starts at 1 again.

**Do not put `{gAdds} >= 3` on the counting trigger.** Match Variables runs before the increment; that condition would stop an unset counter from ever reaching 3. Keep counting and checking on separate messages, as above, rather than depending on which trigger handles the same message first.

### Syntax Overview

Enter only the condition in **Match Variables**, not a label such as `Match Variables:`. Use braces around variable names and quotes around literal text containing spaces:

| What you want | Match Variables |
|---|---|
| Burn mode must be on | `{gBurn} = true` |
| A stored name must be present | `{gCaster} != null` |
| A variable must not be set yet | `{gCaster} = null` |
| The zone must contain a word | `{gZone} contains "Knowledge"` |
| A counter must be at least 3 | `{gAdds} >= 3` |
| Burn mode must be on and the counter at least 3 | `{gBurn} = true and {gAdds} >= 3` |

You can also check a capture from the current Pattern without storing it first. For example, with Regex on, Pattern `You say, 'count {N1}'` and Match Variables `{n1} >= 3`, `/say count 2` is rejected and `/say count 3` passes. The capture belongs to that match; store it in a custom variable if other triggers need it later.

### Variables

Variables are referenced using curly braces and resolve to their current string value. Both `{name}` and `${name}` syntaxes are supported:

| Syntax | Resolves To |
|---|---|
| `{s}` / `{s1}` / `{n2}` | Capture group values from the current Pattern match |
| `${s}` / `${s1}` | Same as above (dollar-sign prefix is optional) |
| `{gZone}` | A custom variable named `gZone` (set via the Trigger Variables tab) |
| `${gZone}` | Same as above (dollar-sign prefix is optional) |
| `{caster}` | A named regex capture group or custom variable |

Names such as `gZone` and `gAdds` are examples you create, not built-in character information. They remain unset until a trigger action stores a value.

If a variable is **not set**, has been cleared or has expired, it resolves to `null`. In comparisons, `null` behaves as described below.

Use different names for stored variables and capture groups, such as `gCaster` and `s1`. In Match Variables, a stored value takes precedence over a capture with the same name.

### Comparison Operators

All comparison operators are **case-insensitive**.

| Operator | Aliases | Description | Example |
|---|---|---|---|
| `=` | `==`, `eq` | Equal to | `{gBurn} = true` |
| `!=` | `<>`, `neq` | Not equal to | `{gBurn} != true` |
| `>` | `gt` | Greater than (numeric) | `{n1} > 50` |
| `>=` | `ge`, `gte` | Greater than or equal | `{gAdds} >= 3` |
| `<` | `lt` | Less than (numeric) | `{gAdds} < 5` |
| `<=` | `le`, `lte` | Less than or equal | `{gAdds} <= 10` |
| `contains` | — | Contains substring (case-insensitive) | `{gZone} contains "Knowledge"` |

**Important notes:**

- Numeric comparisons (`>`, `<`, `>=`, `<=`) treat an **unset variable as zero**. Non-numeric text makes the comparison return `false`.
- To require a value before a numeric check, write `{gAdds} != null and {gAdds} < 5`. Without the null check, an unset `gAdds` counts as 0 and passes `< 5`.
- `contains` is case-insensitive. If the right-hand side is unset, the result is `false`.
- Check for an unset variable with `{name} = null`, or for a set value with `{name} != null`. Comparing two unset variables to each other does not make them equal.

### Boolean Operators

Combine multiple conditions using boolean logic:

| Operator | Aliases | Description |
|---|---|---|
| `and` | `&&` | Both sides must be true |
| `or` | `||` | At least one side must be true |
| `not` | `!` | Negates the following expression |
| `(` ... `)` | — | Groups expressions for precedence |

**Operator precedence** (highest to lowest): `not` → `and` → `or`. Use parentheses to override.

### Literals

You can compare variables against literal values:

| Type | Syntax | Example |
|---|---|---|
| **String (bareword)** | `hello` | `{name} = test` |
| **String (quoted)** | `"hello world"` or `'hello world'` | `{name} = "red dragon"` |
| **Number** | `42`, `-10`, `3.14` | `{hp} > 50` |
| **Boolean** | `true`, `false` | `{enabled} = true` |
| **Null** | `null` | `{target} != null` |
| **Empty string** | `""` or `''` | `{name} = ""` |

Quoted strings preserve spaces. Barewords cannot contain spaces.

### Standalone Variables

A variable by itself (no comparison) is treated as a boolean check:

- `{enabled}` → `true` if the variable is set and non-empty, `false` otherwise
- `not {disabled}` → `true` if `disabled` is unset or empty

This checks whether text is present, not what it says: a variable containing `false` or `0` still passes the standalone check. Use `{enabled} = true` when you want to test the stored value.

### Troubleshooting Match Variables

- **The trigger never fires:** temporarily leave Match Variables blank and test its Pattern. If it fires, restore the condition and check the variable name, the stored value and whether the setting trigger ran for this character.
- **The value never gets set:** the setting trigger must itself be enabled, match a message and pass its own Match Variables check. Adding a variable card does not set its value immediately.
- **The count never increases:** leave the counting trigger's Match Variables blank unless its existing value is meant to control whether it counts. The increment happens after the condition passes.
- **A condition passes when the value says `false`:** `{gBurn}` checks whether a value exists, not whether it says true. Use `{gBurn} = true` for an on/off flag.
- **A variable disappears:** check its Time To Live, Clear Value actions and timer-end cleanup. The trigger tester has its own variable state, separate from monitored characters.
- **A value is not what you expected:** for a visual check, add a diagnostic trigger with a distinct Pattern such as `You say, 'show burn'`, leave Match Variables blank, and set Text to Display to `Burn value is {gBurn}` in a text overlay. An unset variable remains written as `{gBurn}` instead of being replaced. Remove the diagnostic trigger when finished.

### Error Handling

Red text in Match Variables indicates a syntax error, such as missing braces, an unknown operator or mismatched parentheses. Fix the expression and save the trigger.

An invalid condition blocks the trigger even if its Pattern matches. A warning is also written to **Tools → View Error Log**. A blank Match Variables field is valid and adds no condition.


