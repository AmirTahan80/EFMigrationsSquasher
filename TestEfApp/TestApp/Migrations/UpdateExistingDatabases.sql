-- ===============================================
-- EF Core Migration Squash - Database Update Script
-- Generated: 2025-11-30 22:32:19 UTC
-- Migration: ConsolidatedMigration
-- Author: AmirTahan81
-- ===============================================
--
-- This script updates existing databases after migration squashing.
-- 
-- ⚠️  IMPORTANT INSTRUCTIONS:
-- 1. BACKUP your database before running this script
-- 2. This script is for databases that already have your schema
-- 3. Do NOT run this on new/empty databases
-- 4. Test on a development database first
--
-- ===============================================

PRINT 'Starting EF Core Migration History Update...';
PRINT 'Migration: ConsolidatedMigration';
PRINT 'Generated: 2025-11-30 22:32:19 UTC';
PRINT '';

-- Step 1: Check current migration state
PRINT '=== Current Migration History ===';
IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__EFMigrationsHistory')
BEGIN
    SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;
END
ELSE
BEGIN
    PRINT 'No __EFMigrationsHistory table found. This might be a new database.';
END

PRINT '';
PRINT '=== Updating Migration History ===';

-- Step 2: Clear old migration history (UNCOMMENT AFTER BACKUP!)
-- ⚠️  UNCOMMENT THE NEXT LINE ONLY AFTER YOU'VE BACKED UP YOUR DATABASE
-- DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]_%';

-- Step 3: Add the new consolidated migration as 'applied'
-- This tells EF Core that this migration has already been applied to this database
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251130223219_ConsolidatedMigration', '9.0.0'); -- Update ProductVersion to match your EF Core version

-- Step 4: Verify the update
PRINT '';
PRINT '=== Updated Migration History ===';
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;

PRINT '';
PRINT 'Migration history update completed successfully!';
PRINT 'Your database now recognizes the consolidated migration: ConsolidatedMigration';

-- ===============================================
-- VERIFICATION QUERIES
-- ===============================================
-- Run these to verify your database state:

-- Check table count
SELECT COUNT(*) as TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE';

-- Check migration history
SELECT COUNT(*) as MigrationCount FROM __EFMigrationsHistory;

-- ===============================================
-- NOTES:
-- - The consolidated migration file will handle new database creation
-- - This script only updates the migration tracking for existing databases  
-- - If you encounter issues, restore from backup and contact: AmirTahan81
-- - Test thoroughly before applying to production!
-- ===============================================
