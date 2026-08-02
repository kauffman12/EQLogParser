# Regex 101

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
- **Special Character List:** .   ^   $   *   +   ?   (   )   [   ]   {   }   \   |   /

## 🔹 **Counts (Repetition)**

- **`{n}`** — **Exactly** n times  
  _Example: `a{3}` matches "aaa" only_
- **`{n,}`** — **At least** n times  
  _Example: `a{2,}` matches "aa", "aaa", "aaaa", etc._
- **`{n,m}`** — **Between** n and m times  
  _Example: `a{2,4}` matches "aa", "aaa", or "aaaa"_

## ⚠️ **.NET Regex Syntax**

- **No slashes needed:** Just type your pattern (don’t use `/like this/`)
- **Case-sensitive by default:** "cat" doesn’t match "Cat"
- **Spaces and punctuation:** They must match **exactly** as typed
- **Named groups are supported:** You can name parts with `(?<name>...)`
- **Don’t use `$1`, `$2`, etc.:** These are not used as Group references

## 🚀 **Performance Tips**

- **Keep patterns simple and specific** for fastest results.
- **Avoid `.*` in the middle** of patterns; use only if necessary.
- **Don’t nest lots of parentheses** or use complex patterns.
- **Start with a specific word or phrase** (e.g., `^You take`) instead of wildcards.
- **Don’t match entire blocks of text** or paragraphs.
- **Avoid long runs of wildcards and optionals** (like `(.*a.*)*`).



# Trigger Variables

These are special variables or codes that can be used in trigger `Pattern` fields to capture values so that they can be displayed or spoken.

None of these trigger variables are case-insensitive. Whether you use `{c}` or `{C}` they will do the same thing. Also, if you define one variable as `{S}` and reference it later as `{s}` it will still work. The x value in `{Sx}` or `{Nx}` is any number from `0` to `9` so you can use more than one of these in the same trigger.

In addition, modifiers may be used with these variables for display purposes. They do not function in any of the `Pattern` fields but will work in the display fields. These modifiers include `.number`, `.capitalize`, `.lower`, `.upper`, `.padleft`, `.padright`, and `.center`. Number will format number values based on region. For example, in the U.S., they will be formatted with commas. The other options are self-explanatory.

Example Usage when using Modifiers: `{S1.capitalize}`  `{n.number}`

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

Replaced by your character name. Use it in any `Pattern` field as well as all other fields that display, speak, or share information including timer warnings. Everything.

## `{S}`, `{Sx}`

Acts as a wildcard to capture values in `Pattern` fields. It will capture anything, including multiple words, and allow the value to be used later in any fields that displays, speaks, or shares information. Requires `Regex` to be enabled; internally, the parser replaces `{s1}` with `(?<s1>.+)`.

## `{N}`, `{Nx}`

Like `{S}` but captures numbers (no spaces or multiple numbers are allowed). Also, allows the value to be used later in any fields that displays, speaks, or shares information.

## `{N>y}`, `{y<N<z}`

Works like `{N}` but allows additional checks on the range of numbers that will match the trigger. Use operators like `>`, `<`, `>=`, `<=`, or `==`. Use `|` to combine. Example: `{100<=N<200}` to match numbers between `100` and `200`.

## `{L}`

Replaced by the line that triggered the event minus the date/time segment. Useful for testing and seeing the full line that matched. Available only in the `Text to Display`, `Text to Speak`, and `Alternate Timer Name` fields.

## `{LOGTIME}`

Replaced by the time from the line that triggered the event in the `hh:mm:ss` format. Useful if you want to know the time the trigger was fired. Available only in the `Text to Display`, `Text to Speak`, `Text to Send`, and `Alternate Timer Name` fields.

## `{REPEATED}`

This variable is replaced with the number of times the trigger has been repeated and has captured the same values that have been used to display or speak information. Available only in `Text to Display`, `Text to Speak`, and `Alternate Timer Name`. Example Text to Display: `{s1} {repeated}`. This will print the count of how many times the trigger is fired with the same value captured by `{s1}`. The count resets after `750ms` and this reset time can be configured by setting the `Repeated Reset Time` of the trigger.

## `{COUNTER}`

Similar to `{REPEATED}` but it counts the number of times the trigger has fired regardless of the variables captured and used in the display or speak information fields. This variable also uses the `Repeated Reset Time` field to specify the delay used to restart the count.

## `{TS}`

Like `{S}` but used to capture a timestamp in the format `hh:mm:ss` or any number which will be counted as seconds. Requires `Regex` to be enabled. Used only to dynamically set the `Timer Duration`.

## `{NULL}`

Used in any field that displays, speaks, or shares information to suppress the message entirely. Useful when overriding `Timer End Early` behavior. If `{NULL}` is set then nothing will be displayed or spoken.

## `{TIMER-WARN-TIME-VALUE}`

Replaced by the `Timer` setting for `Warn With Time Remaining`. This allows your Display/Speak messages to reference this `Trigger` configuration value if needed. More variables like this could be added in the future where configuration settings are made available when triggers run.

## `{EQLP:STOP}`

Not a trigger variable. You send this text as a say, to the group, raid, another player, or custom channel if you want your triggers to reload, overlays to close, and audio to stop. The chat you send needs to start with this code and it's limited to ensure that it came from you.

## `{EQLP:CLEAR}`

Not a trigger variable. You send this text as a say, to the group, raid, another player, or custom channel if you want to clear all custom variables, counters, and their expiry timers across all active trigger processors. Unlike `{EQLP:STOP}`, this does **not** stop audio or close overlays — it only resets variable state. Use this after zoning, changing zones, or when you want a clean slate for spell/zone tracking without interrupting active triggers.

---

## Creating Custom Variables (Trigger Variables Tab)

In addition to the built-in variables above, you can create **custom variables** that persist across trigger firings. This lets one trigger capture a value (like a caster name or spell being cast) and other triggers reference it later. To access this feature, open the **Trigger Manager**, select a trigger, and click the **Trigger Variables** tab. Each variable has the following settings:

### Action Type

**Set Value** stores a value in the variable. **Clear Value** removes it.

### Variable Name

A unique name for the variable, such as `gCaster` or `gSpellName`. It can be used in conditions and display text. Names beginning with `g` are conventional but not required.

### Data Type

- **Fixed** stores a specific text or numeric value.
- **Counter** stores a number that changes each time the trigger fires.

### Value

The value to store. This can be:

- A capture group, such as `{s1}`
- Another variable, such as `{otherVar}`
- A literal value, such as `Red Dragon`

### Initial Value

*Counter only.* The starting value when the counter is first created. The default is `0`.

### Step

*Counter only.* The amount added each time the trigger fires. The default is `1`. Use a negative value to decrement.

### Time To Live

*Optional.* The number of seconds before the variable expires and is automatically cleared. Set this to `0` for no expiration.

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

Use **Clear Value** actions to remove a variable when a condition ends. For example:

- Trigger A matches "Player begins casting" → **Set** `gCasting = true`
- Trigger B matches "Player stops casting" → **Clear** `gCasting`

Other triggers can check `{gCasting}` in their Match Variables field to only fire while the player is actively casting.

### End Clear Variables (Timer Tab)

The **End Clear Variables** field (found under *Basic Timer Options* on the Timer tab) lets you specify a list of custom variables to clear automatically when a Timer ends — whether it expires normally or ends early via an End Early Pattern. This is useful for cleaning up temporary variables that were only needed while the timer was active.

Enter variable names separated by commas, spaces, or semicolons. The following formats are all accepted and resolve to the same variable name:

- `gCaster` — plain name
- `{gCaster}` — braces (same as display text syntax)
- `${gCaster}` — dollar-brace
- `gCaster, gSpellName; gZone` — multiple names with mixed separators

Variables are cleared **after** the End Text to Display and End Sound/Text to Speak are processed, so end display text can still reference the variable values. The cleanup happens once all timer-end side effects (display, speak, log) are complete.

**Example — Caster name that dies with the buff timer:**

1. Trigger matches "Player begins casting Spirit of Vesagran" → Set `gEpicCaster = {s1}`
2. Enable Timer, Duration: 3:00, End Text to Display: `BRD Epic ({gEpicCaster})`
3. End Clear Variables: `gEpicCaster`

When the 3-minute buff timer ends, the display shows the caster name and the variable is cleared.

---

## Match Variables Field

The **Match Variables** field (found under *Basic Trigger Options*) lets you add an extra gate on top of the Pattern match. Even if the Pattern matches, the trigger will **only fire** if the condition expression evaluates to `true`.

This is useful for situations like:
- Only fire when a captured HP value is above 50
- Only fire when a custom variable set by another trigger has a specific value
- Combine multiple checks with `and` / `or` logic

### Syntax Overview

A condition expression consists of **variables**, **literals**, **comparison operators**, and **boolean operators**:

```
{s} = hello
{hp} > 50
{name} contains dragon
({hp} > 50 and {mana} > 10) || {godmode} = true
```

### Variables

Variables are referenced using curly braces and resolve to their current string value. Both `{name}` and `${name}` syntaxes are supported:

| Syntax | Resolves To |
|---|---|
| `{s}` / `{s1}` / `{n2}` | Capture group values from the current Pattern match |
| `${s}` / `${s1}` | Same as above (dollar-sign prefix is optional) |
| `{hp}` | A custom variable named `hp` (set via the Trigger Variables tab) |
| `${hp}` | Same as above (dollar-sign prefix is optional) |
| `{target}` | A named regex capture group or custom variable |

If a variable is **not set** (never assigned a value), it resolves to `null`. In comparisons, `null` behaves as described in the operator table below.

### Comparison Operators

All comparison operators are **case-insensitive**.

| Operator | Aliases | Description | Example |
|---|---|---|---|
| `=` | `==`, `eq` | Equal to | `{name} = test` |
| `!=` | `<>`, `neq` | Not equal to | `{class} != druid` |
| `>` | `gt` | Greater than (numeric) | `{hp} > 50` |
| `>=` | `ge`, `gte` | Greater than or equal | `{level} >= 20` |
| `<` | `lt` | Less than (numeric) | `{mana} < 100` |
| `<=` | `le`, `lte` | Less than or equal | `{damage} <= 999` |
| `contains` | — | Contains substring (case-insensitive) | `{name} contains dragon` |

**Important notes:**
- Numeric comparisons (`>`, `<`, `>=`, `<=`) attempt to parse both sides as numbers. If either side cannot be parsed as a number, the comparison returns `false`.
- `contains` is case-insensitive. If the right-hand side resolves to `null` (unset variable), the result is `false`.
- `=` and `==` treat `null` values correctly: `{unsetVar} = null` is `true`, `{setVar} = null` is `false`.

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

### Error Handling

If the Match Variables field contains a syntax error (unclosed braces, unknown operators, mismatched parentheses), the condition is treated as **false** — meaning the trigger will **not** fire even if the Pattern matches. A warning is logged to the EQLogParser error log so you can identify and fix the issue.


