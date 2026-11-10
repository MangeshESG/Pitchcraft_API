using Microsoft.EntityFrameworkCore;

namespace PitchGenApi.Database
{
    public static class ContactListCategorySchema
    {
        public static Task EnsureAsync(AppDbContext context) => context.Database.ExecuteSqlRawAsync(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'PitchGenApi.ContactListCategory.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0 THROW 51010, 'Could not acquire contact list category schema lock.', 1;

IF OBJECT_ID(N'dbo.crm_contact_list_categories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.crm_contact_list_categories (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_crm_contact_list_categories PRIMARY KEY,
        client_id int NOT NULL,
        category_type nvarchar(10) NOT NULL CONSTRAINT DF_crm_contact_list_categories_type DEFAULT N'list',
        name nvarchar(120) NOT NULL,
        created_at datetime2 NOT NULL
    );
END;

IF COL_LENGTH(N'dbo.crm_contact_list_categories', N'category_type') IS NULL
    ALTER TABLE dbo.crm_contact_list_categories ADD category_type nvarchar(10) NOT NULL
        CONSTRAINT DF_crm_contact_list_categories_type DEFAULT N'list';

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.crm_contact_list_categories')
    AND name = N'IX_crm_contact_list_categories_client_id_name')
    DROP INDEX IX_crm_contact_list_categories_client_id_name ON dbo.crm_contact_list_categories;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.crm_contact_list_categories')
    AND name = N'IX_crm_contact_list_categories_client_id_category_type_name')
    CREATE UNIQUE INDEX IX_crm_contact_list_categories_client_id_category_type_name
        ON dbo.crm_contact_list_categories(client_id, category_type, name);

IF COL_LENGTH(N'dbo.data_files', N'category_id') IS NULL
    ALTER TABLE dbo.data_files ADD category_id int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.data_files')
    AND name = N'FK_data_files_contact_list_category')
    ALTER TABLE dbo.data_files ADD CONSTRAINT FK_data_files_contact_list_category
        FOREIGN KEY (category_id) REFERENCES dbo.crm_contact_list_categories(id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.data_files')
    AND name = N'IX_data_files_category_id')
    CREATE INDEX IX_data_files_category_id ON dbo.data_files(category_id);

IF COL_LENGTH(N'dbo.crm_views', N'category_id') IS NULL
    ALTER TABLE dbo.crm_views ADD category_id int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.crm_views')
    AND name = N'FK_crm_views_contact_list_category')
    ALTER TABLE dbo.crm_views ADD CONSTRAINT FK_crm_views_contact_list_category
        FOREIGN KEY (category_id) REFERENCES dbo.crm_contact_list_categories(id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.crm_views')
    AND name = N'IX_crm_views_category_id')
    CREATE INDEX IX_crm_views_category_id ON dbo.crm_views(category_id);

COMMIT TRANSACTION;
");
    }
}
