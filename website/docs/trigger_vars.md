# Trigger Variables

Note that none of these trigger variables are case-insensitive. Whether you use `{c}` or `{C}` they will do the same thing.

If you define one variable as `{S}` and reference it later as `{s}` it will still work. The x value in `{Sx}` or `{Nx}` is any number from 0 to 9 so you can use more than one of these in the same trigger.

## `{C}`

Replaced by your character name. Use it in any Pattern field, Sound/Text to Speak, Text to Display, Text to Share, Text to Send, or Alternate Timer Name.

## `{L}`

Replaced by the line that triggered the event, minus the date/time segment. Useful for testing and seeing the full line that matched.

## `{S}`, `{Sx}`

Used with Regex enabled. Acts as a wildcard to capture any value (including multiple words). Can be referenced later in Text or Speak fields. Internally, `{s1}` becomes `(?<s1>.+)`.

## `{N}`, `{Nx}`

Like `{S}` but matches numbers only (no spaces or multiple numbers). Can also be used in output fields.

## `{N>y}`, `{N<y|N>z}`

Filters numbers based on comparison. Use operators like `>`, `<`, `>=`, `<=`, or `==`. Use `|` to combine. Example: `{N>100|N<200}` to match numbers between 100 and 200.

## `{TS}`

Represents a timestamp in the format `hh:mm:ss` or seconds. Must be used in Pattern field with Regex. Used for setting Timer Duration.

## `{repeated}`

Available in Text to Display, Text to Speak, and Alternate Timer Name. Replaced with the number of times the output was used. Example: `{spell} {repeated}` maintains separate counts per spell. Default reset time is 750ms.

## `{counter}`

Same as `{repeated}`. For GINA compatibility. GINA counts every trigger fire without checking the output fields.

## `{null}`

Used in output fields to suppress the message entirely. Useful when overriding Timer End Early behavior. If `{null}` is set, nothing will be displayed or spoken.

## `{EQLP:STOP}`

Not a trigger variable. Sends this text to group or player to reload triggers and stop overlays/audio.
