# EF Core Migrations Squasher

A powerful CLI tool to consolidate multiple Entity Framework Core migrations into a single migration. Perfect for cleaning up your migration history while maintaining full schema integrity and rollback capability.

## Features

✨ **Consolidate Migrations** - Combine multiple migrations into one clean migration  
🔄 **Smart Down Methods** - Automatically extracts or generates proper rollback operations  
💾 **Automatic Backup** - Creates timestamped backups before any modifications  
📊 **SQL Scripts** - Generates database update scripts for existing databases  
✅ **Comprehensive** - Handles CreateTable, AddColumn, CreateIndex, and more  
🎯 **Easy to Use** - Simple CLI interface with clear feedback  

## Installation

### As a CLI Tool (Global Install)

```bash
dotnet tool install --global EFMigrationsSquasher
```

### As a NuGet Package

```bash
dotnet add package EFMigrationsSquasher
```

## Quick Start

### Basic Usage

```bash
ef-migrations-squash --project "./MyApp/MyApp.csproj" --context "MyDbContext" --name "ConsolidatedMigration"
```

### Options

- `--project` (required): Path to your .csproj file
- `--context` (required): Your DbContext class name
- `--name` (optional): Name for the new migration (default: "ConsolidatedMigration")
- `--dry-run` (optional): Preview changes without applying them

### Example

```bash
# Preview what would happen
ef-migrations-squash --project "./Data/AppContext.csproj" --context "ApplicationDbContext" --dry-run

# Actually perform the squash
ef-migrations-squash --project "./Data/AppContext.csproj" --context "ApplicationDbContext" --name "InitialSchema"
```

## How It Works

1. **Backup Creation** - Creates a timestamped backup of all migration files
2. **Schema Extraction** - Extracts all `Up` methods from existing migrations in chronological order
3. **Down Method Extraction** - Extracts actual `Down` methods or auto-generates inverse operations
4. **File Cleanup** - Removes old migration files
5. **Consolidation** - Creates a new migration with combined Up/Down logic
6. **Database Script** - Generates SQL for updating existing databases

## Supported Operations

The tool automatically handles:

- ✅ `CreateTable` / `DropTable`
- ✅ `AddColumn` / `DropColumn`
- ✅ `CreateIndex` / `DropIndex`
- ✅ Foreign Keys and Constraints
- ✅ Custom property configurations

## Example

### Before (Multiple Migrations)
```
Migrations/
  20251122201757_init.cs
  20251122201820_AddUserAge.cs
  20251122201836_AddProductDescription.cs
  20251122201844_AddUserEmailIndex.cs
```

### After (Consolidated)
```
Migrations/
  20251130223219_ConsolidatedMigration.cs (contains all operations)
  20251130223219_ConsolidatedMigration.Designer.cs
  TestDbContextModelSnapshot.cs
```

## Important Notes

⚠️ **Always backup your project first** - While the tool creates automatic backups, ensure you have your own backup  
⚠️ **Test thoroughly** - Test the consolidated migration on a development database before production  
⚠️ **Review generated code** - Check the generated migration file to ensure it matches your schema  
⚠️ **Existing databases** - Use the generated `UpdateExistingDatabases.sql` script to update migration history  

## For Existing Databases

After running the squash tool:

1. A file `UpdateExistingDatabases.sql` is generated in your Migrations folder
2. This script updates the EF Core migration history table for existing databases
3. New databases will automatically use the consolidated migration

```sql
-- Example: Run this on your existing databases
UPDATE __EFMigrationsHistory 
SET MigrationId = 'your_new_migration_id'
WHERE MigrationId IN ('old_migration_1', 'old_migration_2', ...);
```

## Troubleshooting

### "No migrations found to squash"
- Ensure your Migrations folder contains at least one migration
- Check that migration files follow the standard EF Core naming pattern

### "Down method is empty"
- The tool automatically generates inverse operations from Up methods
- Review the generated Down method to ensure it's correct

### Build fails after consolidation
- Check that the ModelSnapshot.cs file is valid
- Ensure all entity configurations are correct
- Review the Designer.cs file for issues

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Author

Created by **AmirTahan80**

## Support

If you encounter issues or have suggestions, please open an issue on GitHub.

---

**Made with ❤️ for the .NET community**
