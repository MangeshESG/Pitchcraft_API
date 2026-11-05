using Microsoft.EntityFrameworkCore;

namespace PitchGenApi.Database
{
    public static class NotificationSchema
    {
        // Additive and safe to repeat, including when multiple API instances start.
        public static Task EnsureAsync(AppDbContext context) => context.Database.ExecuteSqlRawAsync(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'PitchGenApi.UnsubscribeNotifications.Schema',
    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
IF @lockResult < 0
    THROW 51000, 'Could not acquire unsubscribe notification schema lock.', 1;
IF OBJECT_ID(N'dbo.UnsubscribeNotifications', N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.Notification', N'U') IS NOT NULL
    THROW 51001, 'Both old and new notification tables exist. Resolve the table names before starting the API.', 1;
IF OBJECT_ID(N'dbo.UnsubscribeNotifications', N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.Notification', N'U') IS NULL
    EXEC sys.sp_rename N'dbo.UnsubscribeNotifications', N'Notification';
IF OBJECT_ID(N'dbo.Notification', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Notification (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Notification PRIMARY KEY,
        ClientId int NOT NULL,
        ContactId int NULL,
        Email nvarchar(320) NOT NULL,
        Source nvarchar(32) NOT NULL,
        CreatedAt datetime2 NOT NULL,
        ReadAt datetime2 NULL,
        UnsubscribedContactId int NULL,
        CONSTRAINT FK_Notification_UnsubscribedContacts FOREIGN KEY (UnsubscribedContactId)
            REFERENCES dbo.UnsubscribedContacts(Id) ON DELETE CASCADE,
        CONSTRAINT CK_Notification_Source CHECK (Source IN (N'UnsubscribeByToken', N'OneClick', N'WebsiteContact'))
    );
    CREATE UNIQUE INDEX IX_Notification_UnsubscribedContactId
        ON dbo.Notification(UnsubscribedContactId) WHERE UnsubscribedContactId IS NOT NULL;
    CREATE INDEX IX_Notification_ClientId_Id
        ON dbo.Notification(ClientId, Id DESC) INCLUDE (ReadAt);
END;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Notification')
    AND name = N'UnsubscribedContactId' AND is_nullable = 0)
BEGIN
    DECLARE @foreignKey sysname = (SELECT fk.name FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns columnInfo ON columnInfo.object_id = fkc.parent_object_id
            AND columnInfo.column_id = fkc.parent_column_id
        WHERE fk.parent_object_id = OBJECT_ID(N'dbo.Notification')
            AND columnInfo.name = N'UnsubscribedContactId');
    IF @foreignKey IS NOT NULL
    BEGIN
        DECLARE @dropForeignKey nvarchar(max) = N'ALTER TABLE dbo.Notification DROP CONSTRAINT ' + QUOTENAME(@foreignKey);
        EXEC(@dropForeignKey);
    END;

    DECLARE @uniqueIndex sysname = (SELECT TOP (1) indexes.name FROM sys.indexes indexes
        JOIN sys.index_columns columns ON columns.object_id = indexes.object_id
            AND columns.index_id = indexes.index_id
        JOIN sys.columns columnInfo ON columnInfo.object_id = columns.object_id
            AND columnInfo.column_id = columns.column_id
        WHERE indexes.object_id = OBJECT_ID(N'dbo.Notification')
            AND indexes.is_unique = 1 AND columnInfo.name = N'UnsubscribedContactId');
    IF @uniqueIndex IS NOT NULL
    BEGIN
        DECLARE @dropUniqueIndex nvarchar(max) = N'DROP INDEX ' + QUOTENAME(@uniqueIndex) + N' ON dbo.Notification';
        EXEC(@dropUniqueIndex);
    END;

    ALTER TABLE dbo.Notification ALTER COLUMN UnsubscribedContactId int NULL;
    ALTER TABLE dbo.Notification ADD CONSTRAINT FK_Notification_UnsubscribedContacts
        FOREIGN KEY (UnsubscribedContactId) REFERENCES dbo.UnsubscribedContacts(Id) ON DELETE CASCADE;
    CREATE UNIQUE INDEX IX_Notification_UnsubscribedContactId
        ON dbo.Notification(UnsubscribedContactId) WHERE UnsubscribedContactId IS NOT NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.Notification')
    AND definition LIKE N'%WebsiteContact%')
BEGIN
    DECLARE @sourceConstraint sysname = (SELECT TOP (1) name FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.Notification') AND definition LIKE N'%UnsubscribeByToken%');
    IF @sourceConstraint IS NOT NULL
    BEGIN
        DECLARE @dropSourceConstraint nvarchar(max) = N'ALTER TABLE dbo.Notification DROP CONSTRAINT ' + QUOTENAME(@sourceConstraint);
        EXEC(@dropSourceConstraint);
    END;
    ALTER TABLE dbo.Notification ADD CONSTRAINT CK_Notification_Source
        CHECK (Source IN (N'UnsubscribeByToken', N'OneClick', N'WebsiteContact'));
END;
COMMIT TRANSACTION;
");
    }
}
