IF COL_LENGTH('dbo.ClientDetails', 'IsRfcUnsubscribeAllowed') IS NULL
BEGIN
    ALTER TABLE dbo.ClientDetails
        ADD IsRfcUnsubscribeAllowed bit NOT NULL
            CONSTRAINT DF_ClientDetails_IsRfcUnsubscribeAllowed DEFAULT (0);
END;
