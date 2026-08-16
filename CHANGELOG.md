# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-08-16

### Changed
- **Merge into the LAST existing migration instead of creating a new one.** The last
  migration keeps its original id/timestamp, so databases that already have that id in
  `__EFMigrationsHistory` are never affected (no pending migration is created). This
  fixes the previous behavior where a brand-new migration id made EF try to re-apply
  the whole schema against existing databases.
- Add `--update-database` flag (and an interactive prompt) to run
  `dotnet ef database update` *before* merging files.
- Each merged block is prefixed with `/* These operations come from <migrationId> */`.
- Strip the 14-digit timestamp from the generated class name (kept only in the
  `[Migration]` attribute), matching EF's own convention so the output compiles.

### Added
- Exact-duplicate operation removal (e.g. the same `CreateIndex` in two migrations).
- Create/drop cancellation: a `CreateX` immediately followed by `DropX` on the same
  object nets to nothing and is dropped from both `Up` and `Down`.

## [1.1.0] - 2026-07-14

### Changed
- Target .NET 10 and EF Core 10.0.3
- Package the application as a .NET global tool with the documented command name
- Refresh installation, usage, and safety documentation

### Fixed
- Return a non-zero process exit code for invalid input and runtime failures
- Use the same migration ID in generated C# and SQL files
- Preserve model snapshot namespaces instead of hard-coding the sample project namespace
- Prevent backup migration files from being compiled by SDK-style projects
- Override the sample app's vulnerable transitive Microsoft.OpenApi dependency
- Correct package author references and corrupted README text

## [1.0.0] - 2025-12-01

### Added
- Initial release of EF Core Migrations Squasher
- CLI tool to consolidate multiple Entity Framework Core migrations
- Automatic Up method extraction from all migrations in chronological order
- Smart Down method extraction with automatic inverse operation generation
- Automatic backup creation before any file modifications
- SQL script generation for updating existing databases
- Support for all standard EF Core operations (CreateTable, AddColumn, CreateIndex, etc.)
- Comprehensive error handling and user-friendly console output
- Dry-run mode to preview changes without applying them
- Designer file generation with proper BuildTargetModel extraction

### Features
- ✨ Consolidates multiple migrations into a single migration
- 🔄 Intelligently extracts or generates Down methods
- 💾 Creates timestamped backups automatically
- 📊 Generates SQL scripts for migration history updates
- ✅ Supports all standard EF Core migration operations
- 🎯 Simple and intuitive CLI interface
