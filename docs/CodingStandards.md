# EQLogParser C# Coding Standards

## File Encoding

- All `.cs` files must be saved as **UTF-8 without BOM** (Byte Order Mark)
- Configure your editor to enforce this; BOM characters cause issues with git diffs and some tooling

## Indentation and Formatting

- **2-space indentation** throughout all files
- Braces on same line for class/method declarations
- Braces on new line for control structures (if/else/for/while)
- Consistent spacing around operators and after commas
- Logical grouping of related methods with blank lines
- Line length limited to ~120 characters

## Naming Conventions

### Classes and Types
- PascalCase (e.g., `DamageValidator`, `AudioManager`)

### Methods
- PascalCase (e.g., `GetVoiceList()`, `RebuildTotalStats()`)
- Verb-noun pattern preferred (e.g., `CheckTimersAsync`, `HandleTriggerAsync`)
- Clear prefixes: `Get`, `Set`, `Update`, `Check`, `Handle`
- Async suffix: `Async` for asynchronous methods

### Variables
- prefer use of var over naming the type when possible
- camelCase (e.g., `_assassinateEnabled`, `_mainWindow`)
- Short, meaningful names (e.g., `wrapper`, `lineData`, `matches`)
- Descriptive names for complex data (e.g., `dynamicDuration`, `swTime`)

### Fields
- Private fields prefixed with underscore (e.g., `_dsEnabled`, `_theState`)
- Readonly fields for dependencies (e.g., `_counterTimes`, `_repeatedTextTimes`)
- Volatile fields for thread safety (e.g., `_isDisposed`, `_ready`)

### Constants
- PascalCase (e.g., `Twincast`, `LogTimeCode`)

### Enums
- Use `enum` for fixed sets of related values (view types, option lists, states, modes) instead of `int` constants or magic numbers
- Enums make switch expressions self-documenting and the compiler catches missing cases
- When an enum value is added, all switch expressions and filter logic referencing the underlying type must be updated together in the same commit

### Events
- PascalCase with "Events" prefix (e.g., `EventsChartOpened`, `EventsGenerationStatus`)

## Access Modifiers

- `internal` is used extensively for classes and members that should be visible within the assembly
- `public` is used for UI components and public interfaces
- `private` is used for implementation details

## Code Organization

- XML documentation comments are used for public members
- **Remove unused `using` statements** — run `dotnet format` or your IDE's cleanup to auto-remove them before committing
- Methods are ordered by visibility (public first, then internal, then private)
- Related methods are grouped together
- Lifecycle methods grouped together
- Core processing methods grouped together
- Utility methods grouped together

## Singleton Pattern

Managers and shared services use a singleton pattern with lazy initialization:

```csharp
private static T _instance;
internal static T Instance
{
  get => _instance ??= new();
  set => _instance = value;
}
```

The setter enables test injection. Consumers access the singleton via `ClassName.Instance`.

## Interface Design

Split large interfaces by responsibility:
- `IDataManager` — static spell/NPC/class data (lookup methods)
- `IFightManager` — runtime fight state (active fights, overlays, ADPS)

Each interface has only the methods relevant to its responsibility. Classes that need both inject both interfaces.

## Error Handling & User Communication

### General Principles
- Exceptions are caught and handled appropriately
- `async`/`await` pattern is used for I/O operations
- `ValueTask` is used for performance-critical async operations
- Try-catch blocks with empty catch blocks are only used for expected exceptions
- Graceful degradation when errors occur
- Logging for important events and errors
- Null checks before accessing properties/methods

### Implementation Patterns
- **Null Safety**: When updating UI elements based on a loaded object, always wrap the updates inside the `null` check that validated the object to avoid `NullReferenceException`.
- **Nullable Checks**: When checking nullable return values from helper methods, prefer `is null` / `is not null` over `is not SomeType` to make the intent clear — the method is returning a nullable result, not a different type.
- **Visual Feedback**:
    - Use `MessageWindow.IconType.Warn` for non-critical errors that do not stop the application from functioning.
    - Use `MessageWindow.IconType.Question` for confirmation dialogs.
- **Logging**: Every caught exception that results in a user-facing `MessageWindow` must be logged via `Log.Error(ex)` to ensure diagnostic data is available.

### User-Facing Messages
To maintain a consistent tone and professional feel, all messages in `MessageWindow` should follow these guidelines:
- **Tone**: Use a helpful, non-alarmist, and professional tone.
- **Message Casing**: Use **Sentence case** for the main message (e.g., `"Problem loading layout. Check Error Log for details."`).
- **Caption Casing**: Use **Title Case** for the window caption (e.g., `"Load Layout"`, `"Delete Layout"`).
- **Specific Terminology**: Always capitalize `"Error Log"` as it refers to a specific menu item in the Tools menu.
- **Clarity**: Always provide a clear indication of where the user can find more information if the error is complex (e.g., `"Check Error Log for details."`).
- **Standard Phrases**:
    - Instead of "Error loading...", use `"Problem loading [Component]. Check Error Log for details."`
    - Instead of "Failed to save...", use `"Problem saving [Component]. Check Error Log for details."`

## WPF & XAML Standards

### Resource Management
- Use `DynamicResource` for colors, fonts, and sizes to ensure theme changes are applied instantly across the UI.
- Use `StaticResource` only for resources that are guaranteed not to change during the application's lifetime.

### Naming and Layout
- **Naming**: Use `camelCase` for XAML element names (e.g., `layoutSelector`) and `PascalCase` for Styles, ControlTemplates, and DataTemplates.
- **Layout**: Prefer `Grid` and `StackPanel` over absolute positioning to ensure the UI scales correctly across different resolutions.
- **Interaction**: Use `IsHitTestVisible="False"` for overlay elements (like placeholders) to ensure they do not interfere with user interaction with the underlying controls.

### UI Helpers
- Reusable UI logic belongs in `*Util` classes (e.g., `UIElementUtil`, `UiUtil`), not in code-behind files
- If a helper method is used in more than one code-behind file, extract it to the appropriate Util class

### View-Logic Communication
- Use `ItemTemplates` for rendering lists of data objects to keep the UI definition separate from the data model.
- Handle complex interaction logic in the code-behind via event handlers, ensuring that UI updates are performed on the Dispatcher thread.

### Binding Type Consistency
- Match model property types to binding requirements — avoid `IValueConverter` when the model type can match directly
- Decide the source property type early in a feature and stick with it; changing a property from `int` to `string` (or vice versa) mid-feature requires updating all switch/filter logic, converters, and binding references
- If a `string` representation is needed for the UI, use a computed property or view model rather than changing the model type

## Performance Guidelines

### Collection Choices
- Use `List<T>` for general purpose ordered lists.
- Use `HashSet<T>` for fast lookup of unique items.
- Use `ConcurrentDictionary<T, K>` for collections accessed by multiple threads.

### Efficient Data Processing
- Use `StringBuilder` for repeated string concatenation in loops.
- Use `ReadOnlySpan<char>` for high-performance string slicing and parsing to reduce memory allocations.
- Use `CollectionsMarshal.AsSpan()` when iterating over `List<T>` in performance-critical loops to avoid overhead and enable better compiler optimizations.
- Use `StringComparison.OrdinalIgnoreCase` for case-insensitive comparisons to avoid culture-related performance overhead.
- Use `string.Intern()` for strings that are repeated frequently across many objects (e.g., spell names, NPC names) to reduce memory footprint and allow for faster reference equality checks.

### Threading and Responsiveness
- **The Golden Rule**: Perform heavy processing (log parsing, file I/O) on a background thread to keep the UI responsive.
- **UI Updates**: Use the `Dispatcher` to marshal updates back to the UI thread.
- Use `Volatile` fields or `Interlocked` operations for frequently accessed shared state to avoid expensive locking where possible.

## Documentation

- Summary tags are used to describe purpose
- Param tags document method parameters
- Returns tags document return values
- Inline comments for complex logic
