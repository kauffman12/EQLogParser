# Project Rules & Guidelines

You are an expert AI assistant tasked with maintaining this C#/WPF/.net 8.0 project.

## Core Principles
- **Follow Coding Standards** read and follow the stanrds under docs/CodingStandards.md
- **File structure**: Prefer small files and atomic commits.
- **Searching**: All files are under the current directoy. 
- **Do not** add heavy dependencies without explicit user approval.

## Testing Guidelines
- **Always** run `dotnet build` after completing work

## Post-Implementation Checklist
After completing work, verify:
- **Method visibility ordering**: public → internal → private (per CodingStandards.md)
- **Pattern matching**: use `is not T` / `is T var` instead of `as T` + null check (per CodingStandards.md)
- **Unused usings**: no leftover or unnecessary `using` statements

## Agent Behavior
- **Ask for Clarification**: If a feature requirement is ambiguous, ask before implementing.
- **Read the Syncfusion PDF files**: If there's a question about Syncfusion APIs look under syncfusion-wpf for info.

## Important: Never Touch
- `BackupUtil`
- `MaterialDarkCustom`
