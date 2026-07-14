# EF Core Migrations Squasher

A .NET 10 command-line tool that consolidates multiple Entity Framework Core migrations into one migration while preserving their `Up` and `Down` operations.

## Features

- Combines migrations in chronological order
- Combines rollback operations in reverse chronological order
- Creates a timestamped backup before changing migration files
- Supports a dry run that makes no changes
- Generates a SQL Server script for updating the history of an existing database
- Preserves the current model snapshot in the generated designer file

## Requirements

- .NET 10 SDK
- An EF Core 10 project with conventional timestamp-prefixed migrations in a `Migrations` folder

## Installation

Install the package as a global .NET tool:

```bash
dotnet tool install --global EFMigrationsSquasher
```

To install a locally packed build:

```bash
dotnet pack DotnetEfMigrationsSquashTool/DotnetEfMigrationsSquashTool.csproj
dotnet tool install --global --add-source DotnetEfMigrationsSquashTool/bin/Release EFMigrationsSquasher
```

## Usage

Preview the operation first:

```bash
ef-migrations-squash --project "./MyApp/MyApp.csproj" --context "ApplicationDbContext" --dry-run
```

Create the consolidated migration:

```bash
ef-migrations-squash --project "./MyApp/MyApp.csproj" --context "ApplicationDbContext" --name "InitialSchema"
```

Options:

- `--project` (required): path to the project containing the migrations
- `--context` (required): `DbContext` class name used in the generated designer
- `--name`: consolidated migration class name; defaults to `ConsolidatedMigration`
- `--dry-run`: lists affected files without changing them

## What changes

Given:

```text
Migrations/
  20251122201757_Initial.cs
  20251122201820_AddUserAge.cs
  AppDbContextModelSnapshot.cs
```

the tool:

1. Copies the migration C# files into `MigrationsBackup_<timestamp>` as `.cs.bak` files so SDK compile globs ignore them.
2. Extracts `Up` operations in chronological order.
3. Extracts `Down` operations in reverse chronological order.
4. Removes the old migration and designer files, but keeps the model snapshot.
5. Writes one migration and designer file with a shared migration ID.
6. Writes `Migrations/UpdateExistingDatabases.sql` for SQL Server databases that already contain the schema.

## Safety notes

Migration squashing rewrites migration history. Commit or independently back up the project first, inspect the generated C# and SQL, and test against disposable new and existing databases before production.

The generated history script is intentionally conservative: its deletion statement remains commented out. Review and adapt it for the database provider and deployment policy. Do not run it on an empty database.

## Troubleshooting

### No migrations found to squash

Confirm that the project has a `Migrations` directory and standard migration filenames beginning with a 14-digit timestamp.

### Generated code does not compile

Confirm the `--context` value, inspect the preserved model snapshot, and restore the automatically created `.cs.bak` files if needed. Provider-specific migration code may require provider namespaces already present in the snapshot.

### Existing database still reports pending migrations

Verify that the migration ID in `UpdateExistingDatabases.sql` exactly matches the generated migration filename, back up the database, then update `__EFMigrationsHistory` according to the reviewed script.

## Contributing

Issues and pull requests are welcome.

## License

Licensed under the [MIT License](LICENSE).
