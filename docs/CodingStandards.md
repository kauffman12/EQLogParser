# EQLogParser C# Coding Standards

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
- camelCase (e.g., `_assassinateEnabled`, `_mainWindow`)
- Short, meaningful names (e.g., `wrapper`, `lineData`, `matches`)
- Descriptive names for complex data (e.g., `dynamicDuration`, `swTime`)

### Fields
- Private fields prefixed with underscore (e.g., `_dsEnabled`, `_theState`)
- Readonly fields for dependencies (e.g., `_counterTimes`, `_repeatedTextTimes`)
- Volatile fields for thread safety (e.g., `_isDisposed`, `_ready`)

### Constants
- PascalCase (e.g., `Twincast`, `LogTimeCode`)

### Events
- PascalCase with "Events" prefix (e.g., `EventsChartOpened`, `EventsGenerationStatus`)

## Access Modifiers

- `internal` is used extensively for classes and members that should be visible within the assembly
- `public` is used for UI components and public interfaces
- `private` is used for implementation details

## Code Organization

- XML documentation comments are used for public members
- Methods are ordered by visibility (public first, then internal, then private)
- Related methods are grouped together
- Lifecycle methods grouped together
- Core processing methods grouped together
- Utility methods grouped together

## Error Handling

- Exceptions are caught and handled appropriately
- `async`/`await` pattern is used for I/O operations
- `ValueTask` is used for performance-critical async operations
- Try-catch blocks with empty catch blocks for expected exceptions
- Graceful degradation when errors occur
- Logging for important events and errors
- Null checks before accessing properties/methods

## Threading

- `Dispatcher` is used for UI thread operations
- `ConcurrentDictionary` is used for thread-safe collections
- Volatile fields for frequently accessed shared state
- Interlocked operations for atomic updates when possible

## Documentation

- Summary tags are used to describe purpose
- Param tags document method parameters
- Returns tags document return values
- Inline comments for complex logic

## Specific Conventions

- StringComparison.OrdinalIgnoreCase for case-insensitive comparisons
- StringBuilder for efficient string manipulation
- ReadOnlySpan<char> for efficient string processing
