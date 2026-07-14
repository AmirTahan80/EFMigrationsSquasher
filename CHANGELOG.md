# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
