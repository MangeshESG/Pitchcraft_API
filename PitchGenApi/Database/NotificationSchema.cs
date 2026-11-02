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
        UnsubscribedContactId int NOT NULL,
        CONSTRAINT FK_Notification_UnsubscribedContacts FOREIGN KEY (UnsubscribedContactId)
            REFERENCES dbo.UnsubscribedContacts(Id) ON DELETE CASCADE,
        CONSTRAINT CK_Notification_Source CHECK (Source IN (N'UnsubscribeByToken', N'OneClick'))
    );
    CREATE UNIQUE INDEX IX_Notification_UnsubscribedContactId
        ON dbo.Notification(UnsubscribedContactId);
    CREATE INDEX IX_Notification_ClientId_Id
        ON dbo.Notification(ClientId, Id DESC) INCLUDE (ReadAt);
END;
COMMIT TRANSACTION;
");
    }
}
