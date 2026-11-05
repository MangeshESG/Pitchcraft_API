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
        name nvarchar(120) NOT NULL,
        created_at datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_crm_contact_list_categories_client_id_name
        ON dbo.crm_contact_list_categories(client_id, name);
END;

IF COL_LENGTH(N'dbo.data_files', N'category_id') IS NULL
    ALTER TABLE dbo.data_files ADD category_id int NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.data_files')
    AND name = N'FK_data_files_contact_list_category')
    ALTER TABLE dbo.data_files ADD CONSTRAINT FK_data_files_contact_list_category
        FOREIGN KEY (category_id) REFERENCES dbo.crm_contact_list_categories(id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.data_files')
    AND name = N'IX_data_files_category_id')
    CREATE INDEX IX_data_files_category_id ON dbo.data_files(category_id);

COMMIT TRANSACTION;
");
    }
}
