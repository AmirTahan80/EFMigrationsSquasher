# EF Core Migrations Squasher

EF Core Migrations Squasher is a .NET 10 command-line tool that **merges** a project's timestamped Entity Framework Core migrations into its **last existing migration**.

Unlike a naive "consolidate into a new migration" approach, this tool keeps the id of the last migration. Because deployed databases already have that id recorded in `__EFMigrationsHistory`, **the database is never affected** by the merge — there is no pending migration for EF to try to re-apply.

It merges `Up` operations in chronological order, merges `Down` operations in reverse order, removes exact-duplicate operations, cancels create/drop pairs that net to nothing, keeps the current model snapshot, marks each block with the source migration, and creates a backup before changing any migration files.

> [!WARNING]
> Merging rewrites migration history. Use source control, run `--dry-run` first, and test the result against disposable databases before using it in production.

## Requirements

- .NET 10 SDK
- An EF Core 10 project
- A conventional `Migrations` directory
- Migration filenames that start with a 14-digit timestamp, such as `20260115123000_InitialCreate.cs`

The generated existing-database helper script targets SQL Server. Projects using another database provider must adapt that script themselves.

## Quick start

### 1. Build the tool

From this repository:

```bash
dotnet build DotnetEfMigrationsSquashTool.sln -c Release
```

### 2. Preview the squash

Always begin with a dry run. It lists the migrations and files that would be replaced without changing the target project.

```bash
dotnet run --project ./DotnetEfMigrationsSquashTool/DotnetEfMigrationsSquashTool.csproj -c Release -- \
  --project "./MyApp/MyApp.csproj" \
  --context "ApplicationDbContext" \
  --migration-root "./MyApp" \
  --name "InitialSchema" \
  --dry-run
```

On PowerShell, the same command can be written on one line:

```powershell
dotnet run --project .\DotnetEfMigrationsSquashTool\DotnetEfMigrationsSquashTool.csproj -c Release -- --project ".\MyApp\MyApp.csproj" --context "ApplicationDbContext" --migration-root ".\MyApp" --name "InitialSchema" --dry-run
```

### 3. Create the consolidated migration

After reviewing the dry-run output, repeat the command without `--dry-run`:

```bash
dotnet run --project ./DotnetEfMigrationsSquashTool/DotnetEfMigrationsSquashTool.csproj -c Release -- \
  --project "./MyApp/MyApp.csproj" \
  --context "ApplicationDbContext" \
  --migration-root "./MyApp" \
  --name "InitialSchema"
```

If the tool is already installed as `ef-migrations-squash`, use the shorter form:

```bash
ef-migrations-squash --project "./MyApp/MyApp.csproj" --context "ApplicationDbContext" --migration-root "./MyApp" --name "InitialSchema"
```

## Command options

| Option | Required | Description |
| --- | --- | --- |
| `--project` | Yes | Path to the target `.csproj` file. |
| `--context` | Yes | `DbContext` class name used by the generated designer. |
| `--migration-root` | Yes | Directory that directly contains the `Migrations` folder. |
| `--name` | No | Ignored for the migration id (the last existing id is always kept). Used only as a label. |
| `--update-database` | No | Update the database (`dotnet ef database update`) *before* merging. Without this flag, the tool asks interactively. |
| `--dry-run` | No | Shows what would change without writing or deleting files. |
| `--help` | No | Displays CLI help. |

For this layout:

```text
MyApp/
├── MyApp.csproj
└── Migrations/
    ├── 20260115123000_InitialCreate.cs
    ├── 20260115123000_InitialCreate.Designer.cs
    └── ApplicationDbContextModelSnapshot.cs
```

use:

```text
--project ./MyApp/MyApp.csproj
--migration-root ./MyApp
--context ApplicationDbContext
```

## What the tool changes

When the merge runs, the tool:

1. Finds conventional timestamp-prefixed migration files, ordered chronologically.
2. (Optional) If you choose to update the database, runs `dotnet ef database update` first so the database is fully up to date **before** any files change.
3. Copies all migration C# files and the snapshot into `MigrationsBackup_<timestamp>` using the `.cs.bak` extension.
4. Combines every migration's non-empty `Up` bodies in chronological order.
5. Combines every migration's non-empty `Down` bodies in reverse chronological order.
6. Removes exact-duplicate operations and cancels create/drop pairs that net to nothing (e.g. `CreateTable(X)` then `DropTable(X)` leaves `X` absent, so both are dropped).
7. Writes a `/* These operations come from <migrationId> */` marker before each source's block.
8. Rewrites the **last** migration (and its designer) in place, keeping its original id/timestamp.
9. Removes the earlier migration and designer files while retaining the model snapshot.
10. Creates `Migrations/UpdateExistingDatabases.sql` that only removes the folded-away ids from `__EFMigrationsHistory` (the kept id stays, so the schema is untouched).

The resulting directory resembles:

```text
MyApp/
├── Migrations/
│   ├── 20260301000003_InitialSchema.cs        ← rewritten, keeps its id
│   ├── 20260301000003_InitialSchema.Designer.cs
│   ├── ApplicationDbContextModelSnapshot.cs
│   └── UpdateExistingDatabases.sql
└── MigrationsBackup_20260301_134500/
    ├── 20260115123000_InitialCreate.cs.bak
    ├── 20260115123000_InitialCreate.Designer.cs.bak
    ├── 20260201000002_AddFoo.cs.bak
    └── ApplicationDbContextModelSnapshot.cs.bak
```

Because the kept migration id already exists in deployment databases' `__EFMigrationsHistory`,
running the application or `dotnet ef database update` afterward reports **no pending migrations**
and the schema is left exactly as it was.

## Validate the result

Do not treat successful file generation as the end of testing. At minimum:

1. Review the consolidated `Up` and `Down` methods.
2. Confirm the migration ID matches in the migration filename, designer attribute, and `UpdateExistingDatabases.sql`.
3. Build the target project:

   ```bash
   dotnet build ./MyApp/MyApp.csproj
   ```

4. Test applying the migration to a new disposable database.
5. Test the reviewed history-update procedure against a disposable copy of an existing database.
6. Verify application startup and the database schema before production use.

## Existing databases

An existing database already contains the schema created by the old migrations. It normally needs migration-history reconciliation so EF Core recognizes the consolidated migration.

The generated `UpdateExistingDatabases.sql` is a reviewable starting point, not an automatically executed script. Its old-history deletion statement is deliberately commented out.

Before using it:

1. Back up the database.
2. Confirm the database already contains the expected schema.
3. Confirm the migration ID and EF Core product version.
4. Review and adapt the history cleanup for your deployment policy.
5. Test it on a disposable database copy.

Never run the existing-database history script on an empty database.

## Data migrations and custom code

Some migrations contain more than `migrationBuilder` operations. They may instantiate a `DbContext`, query data, call external code, or save changes.

The squasher preserves that C# code and its imports, but you must review it carefully. Applying the consolidated migration or generating an EF SQL script can execute migration code and may open configured database connections.

## Recovery

The backup directory contains the original files with a `.bak` suffix. To restore manually:

1. Remove the generated merged migration, designer, and SQL helper script.
2. Copy the backup files into the original `Migrations` directory.
3. Remove only the final `.bak` suffix from each restored filename.
4. Build the project and verify the restored migration list.

If the project is under source control and no wanted migration edits are mixed into the working tree, restoring the migration directory from source control is usually simpler.

## Troubleshooting

### Migration root directory not found

`--migration-root` must point to an existing directory, not to a `.csproj` file or directly to an individual migration file.

### Migrations directory not found

The value supplied to `--migration-root` must directly contain a folder named `Migrations`.

### No migrations found

Confirm that migration code files begin with a 14-digit timestamp and end in `.cs`. Designer files and the model snapshot are not counted as migrations.

### Generated project does not compile

- Confirm the `--context` value exactly matches the `DbContext` class name.
- Check that the model snapshot is current.
- Review provider-specific types and custom code in the consolidated migration.
- Restore from `MigrationsBackup_<timestamp>` if the output needs manual correction.

### Existing database still reports pending migrations

Confirm that the migration ID recorded in `__EFMigrationsHistory` exactly matches the generated migration filename and designer attribute. Review `UpdateExistingDatabases.sql`; do not modify production history without a database backup and a tested rollout plan.

## License

Licensed under the [MIT License](LICENSE).
