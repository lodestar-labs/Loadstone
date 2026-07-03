namespace Loadstone.SqlServer;

/// <summary>DDL for Loadstone's own tables. Every statement is idempotent.</summary>
internal static class SystemSchema
{
    public const string SchemaName = "loadstone";

    public static readonly string[] Statements =
    [
        """
        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'loadstone')
            EXEC(N'CREATE SCHEMA loadstone');
        """,
        """
        IF OBJECT_ID(N'loadstone.Jobs') IS NULL
        CREATE TABLE loadstone.Jobs (
            Id              uniqueidentifier NOT NULL CONSTRAINT PK_loadstone_Jobs PRIMARY KEY,
            Dataset         nvarchar(200)    NOT NULL,
            QueueName       nvarchar(200)    NOT NULL,
            FileName        nvarchar(400)    NOT NULL,
            FileReference   nvarchar(400)    NOT NULL,
            Format          nvarchar(20)     NOT NULL,
            RequestedBy     nvarchar(200)    NULL,
            CorrelationId   nvarchar(64)     NOT NULL,
            Status          nvarchar(40)     NOT NULL,
            Attempt         int              NOT NULL CONSTRAINT DF_loadstone_Jobs_Attempt DEFAULT (0),
            MaxAttempts     int              NOT NULL,
            CreatedAt       datetimeoffset   NOT NULL,
            NextAttemptAt   datetimeoffset   NULL,
            StartedAt       datetimeoffset   NULL,
            CompletedAt     datetimeoffset   NULL,
            Error           nvarchar(max)    NULL,
            RecordsRead     bigint           NOT NULL CONSTRAINT DF_loadstone_Jobs_Read DEFAULT (0),
            RecordsRejected bigint           NOT NULL CONSTRAINT DF_loadstone_Jobs_Rejected DEFAULT (0),
            RowsInserted    bigint           NOT NULL CONSTRAINT DF_loadstone_Jobs_Inserted DEFAULT (0),
            RowsUpdated     bigint           NOT NULL CONSTRAINT DF_loadstone_Jobs_Updated DEFAULT (0)
        );
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_loadstone_Jobs_Queue' AND object_id = OBJECT_ID(N'loadstone.Jobs'))
            CREATE INDEX IX_loadstone_Jobs_Queue ON loadstone.Jobs (QueueName, Status, NextAttemptAt, CreatedAt);
        """,
        """
        IF OBJECT_ID(N'loadstone.JobEvents') IS NULL
        CREATE TABLE loadstone.JobEvents (
            Id        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_loadstone_JobEvents PRIMARY KEY,
            JobId     uniqueidentifier NOT NULL,
            At        datetimeoffset   NOT NULL,
            Stage     nvarchar(60)     NOT NULL,
            Message   nvarchar(max)    NOT NULL,
            ElapsedMs float            NULL
        );
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_loadstone_JobEvents_Job' AND object_id = OBJECT_ID(N'loadstone.JobEvents'))
            CREATE INDEX IX_loadstone_JobEvents_Job ON loadstone.JobEvents (JobId, At);
        """,
        """
        IF OBJECT_ID(N'loadstone.RejectedRows') IS NULL
        CREATE TABLE loadstone.RejectedRows (
            Id         bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_loadstone_RejectedRows PRIMARY KEY,
            JobId      uniqueidentifier NOT NULL,
            Entity     nvarchar(200)    NOT NULL,
            SourceLine bigint           NULL,
            SourcePath nvarchar(400)    NULL,
            Field      nvarchar(200)    NOT NULL,
            Reason     nvarchar(2000)   NOT NULL,
            RawValue   nvarchar(2000)   NULL
        );
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_loadstone_RejectedRows_Job' AND object_id = OBJECT_ID(N'loadstone.RejectedRows'))
            CREATE INDEX IX_loadstone_RejectedRows_Job ON loadstone.RejectedRows (JobId);
        """,
        """
        IF OBJECT_ID(N'loadstone.CodeLists') IS NULL
        CREATE TABLE loadstone.CodeLists (
            Id   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_loadstone_CodeLists PRIMARY KEY,
            Name nvarchar(200) NOT NULL CONSTRAINT UQ_loadstone_CodeLists_Name UNIQUE
        );
        """,
        """
        IF OBJECT_ID(N'loadstone.Codes') IS NULL
        CREATE TABLE loadstone.Codes (
            Id          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_loadstone_Codes PRIMARY KEY,
            CodeListId  int            NOT NULL CONSTRAINT FK_loadstone_Codes_CodeLists REFERENCES loadstone.CodeLists (Id),
            Code        nvarchar(400)  NOT NULL,
            Description nvarchar(1000) NULL,
            CONSTRAINT UQ_loadstone_Codes UNIQUE (CodeListId, Code)
        );
        """,
    ];
}
