-- WorkerDock tenant desired state through 0010_remove_builtin_v1.
-- Fresh, empty SQL Server 2022+ database only; compatibility level 160+.
-- Render the required tenant GUID token below before execution.
-- Execute this entire file as ONE batch. No SQLCMD mode or migration runner is needed.
-- History rows represent equivalent satisfied state, not historical script execution.
-- Maintain this snapshot alongside manifest.json; live TenantSnapshotTests are the parity gate.
-- Quartz tables derive from Quartz.NET 4.5.1 (Apache-2.0); see ../upstream/quartz-4.5.1/NOTICE.txt.
-- The developer may add CREATE USER / ALTER ROLE statements at the marked location below
-- to bind their chosen runtime login during this same rendered deployment action.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @InstanceText nvarchar(100) = N'__WORKERDOCK_INSTANCE_ID__';
DECLARE @InstanceId uniqueidentifier = TRY_CONVERT(uniqueidentifier, @InstanceText);
IF DATALENGTH(@InstanceText) <> 72 OR @InstanceId IS NULL
   OR @InstanceId = '00000000-0000-0000-0000-000000000000'
    THROW 51070, 'SnapshotInstanceIdRequired', 1;
IF CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')) < 16
    THROW 51072, 'UnsupportedSqlServerVersion', 1;
IF (SELECT compatibility_level FROM sys.databases WHERE name=DB_NAME()) < 160
    THROW 51073, 'UnsupportedCompatibilityLevel', 1;
IF @@TRANCOUNT <> 0
    THROW 51074, 'SnapshotRequiresOwnTransaction', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'WorkerDock.Schema',
        @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
    IF @LockResult < 0 THROW 51075, 'MigrationLockUnavailable', 1;

    -- Provisioned SQL users are allowed; application objects, schemas and roles are not.
    -- Every guard executes before the first WorkerDock DDL, including its compilation.
    IF EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped=0)
       OR EXISTS (SELECT 1 FROM sys.schemas WHERE name NOT IN (N'dbo',N'guest',N'sys',N'INFORMATION_SCHEMA',
           N'db_owner',N'db_accessadmin',N'db_securityadmin',N'db_ddladmin',N'db_backupoperator',
           N'db_datareader',N'db_datawriter',N'db_denydatareader',N'db_denydatawriter'))
       OR EXISTS (SELECT 1 FROM sys.database_principals WHERE type=N'R' AND is_fixed_role=0 AND name<>N'public')
       OR EXISTS (SELECT 1 FROM sys.types WHERE is_user_defined=1)
       OR EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined=1)
        THROW 51071, 'SnapshotRequiresEmptyDatabase', 1;

    EXEC sys.sp_executesql N'CREATE SCHEMA [workerdock] AUTHORIZATION [dbo];';
    EXEC sys.sp_executesql N'CREATE ROLE WorkerDockRuntime;';

    -- Final table definitions, including every later column/check addition.
    EXEC sys.sp_executesql N'CREATE TABLE [workerdock].TenantSettings (
    InstanceId uniqueidentifier NOT NULL CONSTRAINT PK_TenantSettings PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    DisplayTimeZone nvarchar(100) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantSettings_Name CHECK (LEN(TRIM(DisplayName)) > 0),
    CONSTRAINT CK_TenantSettings_Zone CHECK (LEN(TRIM(DisplayTimeZone)) > 0)
);

CREATE TABLE [workerdock].SchemaHistory (
    MigrationId nvarchar(100) NOT NULL CONSTRAINT PK_SchemaHistory PRIMARY KEY,
    UpChecksum char(64) NOT NULL,
    DownChecksum char(64) NOT NULL,
    AppliedAtUtc datetime2(7) NOT NULL
);

CREATE TABLE [workerdock].AuditEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_AuditEvents PRIMARY KEY,
    EventType nvarchar(100) NOT NULL,
    ActorSubject nvarchar(300) NOT NULL,
    AuthenticationMode nvarchar(40) NOT NULL,
    CorrelationId uniqueidentifier NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL,
    DetailsJson nvarchar(4000) NOT NULL,
    CONSTRAINT CK_AuditEvents_Json CHECK (ISJSON(DetailsJson) = 1)
);

CREATE TABLE [workerdock].Outbox (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    EventType nvarchar(100) NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    AvailableAtUtc datetime2(7) NOT NULL,
    DispatchedAtUtc datetime2(7) NULL,
    CONSTRAINT CK_Outbox_Json CHECK (ISJSON(PayloadJson) = 1),
    SchemaVersion int NOT NULL CONSTRAINT DF_Outbox_SchemaVersion DEFAULT 1,
    EntityId uniqueidentifier NULL,
    EntityRevision bigint NULL,
    PayloadDigest char(64) NULL,
    ProjectionState nvarchar(20) NOT NULL CONSTRAINT DF_Outbox_ProjectionState DEFAULT N''Pending'',
    RetryAtUtc datetime2(7) NULL,
    LastErrorCode nvarchar(40) NULL,
    ReviewAtUtc datetime2(7) NULL
);

CREATE TABLE [workerdock].[WDS_QRTZ_EXECUTION_HISTORY] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [FIRED_TIME] bigint NOT NULL,
    [RUN_TIME] bigint NOT NULL,
    [SUCCEEDED] bit NOT NULL,
    [ERROR_MESSAGE] nvarchar(1000) NULL,
    [RETRY_ATTEMPT] int NOT NULL DEFAULT 0,
    [RETRY_SCHEDULED] bit NOT NULL DEFAULT 0,
    [EXECUTION_LOG] nvarchar(max) NULL,
    [RESULT] int NULL,
    [SUMMARY] nvarchar(1000) NULL,
    [METRICS] nvarchar(max) NULL,
    [MANUAL] bit NULL,
    [FIRE_INSTANCE_ID] nvarchar(140) NULL,
    [JOB_INPUT] nvarchar(max) NULL,
    [JOB_INPUT_TOO_LARGE] bit NULL,
    CONSTRAINT [PK_WDS_QRTZ_EXECUTION_HISTORY] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_MISFIRE_HISTORY] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NULL,
    [JOB_GROUP] nvarchar(150) NULL,
    [MISFIRE_TIME] bigint NOT NULL,
    [SCHED_TIME] bigint NULL,
    [REASON] int NULL,
    CONSTRAINT [PK_WDS_QRTZ_MISFIRE_HISTORY] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_JOB_STATUS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [FIRST_FIRED_TIME] bigint NOT NULL,
    [LAST_FIRED_TIME] bigint NOT NULL,
    [LAST_RESULT] int NOT NULL,
    [LAST_RUN_TIME] bigint NOT NULL,
    [LAST_INSTANCE_NAME] nvarchar(200) NOT NULL,
    [LAST_ENTRY_ID] nvarchar(140) NULL,
    [LAST_SUMMARY] nvarchar(1000) NULL,
    [LAST_SUCCESS_TIME] bigint NULL,
    [LAST_FAILURE_TIME] bigint NULL,
    [LAST_FAILURE_MESSAGE] nvarchar(1000) NULL,
    [CONSECUTIVE_FAILURES] int NOT NULL DEFAULT 0,
    [RUN_COUNT] bigint NOT NULL DEFAULT 0,
    [FAILURE_COUNT] bigint NOT NULL DEFAULT 0,
    CONSTRAINT [PK_WDS_QRTZ_JOB_STATUS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_GROUP],
    [JOB_NAME]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_CALENDARS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [CALENDAR_NAME] nvarchar(200) NOT NULL,
    [CALENDAR] varbinary(max) NOT NULL,
    CONSTRAINT [PK_WDS_QRTZ_CALENDARS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [CALENDAR_NAME]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_CRON_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [CRON_EXPRESSION] nvarchar(120) NOT NULL,
    [TIME_ZONE_ID] nvarchar(80),
    CONSTRAINT [PK_WDS_QRTZ_CRON_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_FIRED_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [FIRED_TIME] bigint NOT NULL,
    [SCHED_TIME] bigint NOT NULL,
    [PRIORITY] int NOT NULL,
    [STATE] nvarchar(16) NOT NULL,
    [JOB_NAME] nvarchar(150) NULL,
    [JOB_GROUP] nvarchar(150) NULL,
    [IS_NONCONCURRENT] bit NULL,
    [REQUESTS_RECOVERY] bit NULL,
    [EXECUTION_GROUP] nvarchar(200) NULL,
    [PROGRESS] int NULL,
    [PROGRESS_MESSAGE] nvarchar(250) NULL,
    CONSTRAINT [PK_WDS_QRTZ_FIRED_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_PAUSED_TRIGGER_GRPS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    CONSTRAINT [PK_WDS_QRTZ_PAUSED_TRIGGER_GRPS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_PAUSED_JOB_GRPS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    CONSTRAINT [PK_WDS_QRTZ_PAUSED_JOB_GRPS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_SCHEDULER_STATE] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [LAST_CHECKIN_TIME] bigint NOT NULL,
    [CHECKIN_INTERVAL] bigint NOT NULL,
    CONSTRAINT [PK_WDS_QRTZ_SCHEDULER_STATE] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [INSTANCE_NAME]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_LOCKS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [LOCK_NAME] nvarchar(40) NOT NULL,
    CONSTRAINT [PK_WDS_QRTZ_LOCKS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [LOCK_NAME]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_JOB_DETAILS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [DESCRIPTION] nvarchar(250) NULL,
    [JOB_CLASS_NAME] nvarchar(250) NOT NULL,
    [IS_DURABLE] bit NOT NULL,
    [IS_NONCONCURRENT] bit NOT NULL,
    [IS_UPDATE_DATA] bit NOT NULL,
    [REQUESTS_RECOVERY] bit NOT NULL,
    [JOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDS_QRTZ_JOB_DETAILS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_SIMPLE_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [REPEAT_COUNT] int NOT NULL,
    [REPEAT_INTERVAL] bigint NOT NULL,
    [TIMES_TRIGGERED] int NOT NULL,
    CONSTRAINT [PK_WDS_QRTZ_SIMPLE_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_SIMPROP_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [STR_PROP_1] nvarchar(512) NULL,
    [STR_PROP_2] nvarchar(512) NULL,
    [STR_PROP_3] nvarchar(512) NULL,
    [INT_PROP_1] int NULL,
    [INT_PROP_2] int NULL,
    [LONG_PROP_1] bigint NULL,
    [LONG_PROP_2] bigint NULL,
    [DEC_PROP_1] numeric(13,4) NULL,
    [DEC_PROP_2] numeric(13,4) NULL,
    [BOOL_PROP_1] bit NULL,
    [BOOL_PROP_2] bit NULL,
    [TIME_ZONE_ID] nvarchar(80) NULL,
    CONSTRAINT [PK_WDS_QRTZ_SIMPROP_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_BLOB_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [BLOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDS_QRTZ_BLOB_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDS_QRTZ_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [DESCRIPTION] nvarchar(250) NULL,
    [NEXT_FIRE_TIME] bigint NULL,
    [PREV_FIRE_TIME] bigint NULL,
    [PRIORITY] int NULL,
    [TRIGGER_STATE] nvarchar(16) NOT NULL,
    [TRIGGER_TYPE] nvarchar(8) NOT NULL,
    [START_TIME] bigint NOT NULL,
    [END_TIME] bigint NULL,
    [CALENDAR_NAME] nvarchar(200) NULL,
    [MISFIRE_INSTR] int NULL,
    [MISFIRE_ORIG_FIRE_TIME] bigint NULL,
    [EXECUTION_GROUP] nvarchar(200) NULL,
    [PREFERRED_NODE] nvarchar(200) NULL,
    [PREFERRED_NODE_AUTO] bit NOT NULL DEFAULT 0,
    [RETRY_POLICY] nvarchar(250) NULL,
    [RETRY_ATTEMPT] int NULL,
    [CONTINUES_TRIGGER_NAME] nvarchar(150) NULL,
    [CONTINUES_TRIGGER_GROUP] nvarchar(150) NULL,
    [CONTINUATION_CONDITION] int NULL,
    [OVERLAP_POLICY] int NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    [JOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDS_QRTZ_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_EXECUTION_HISTORY] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [FIRED_TIME] bigint NOT NULL,
    [RUN_TIME] bigint NOT NULL,
    [SUCCEEDED] bit NOT NULL,
    [ERROR_MESSAGE] nvarchar(1000) NULL,
    [RETRY_ATTEMPT] int NOT NULL DEFAULT 0,
    [RETRY_SCHEDULED] bit NOT NULL DEFAULT 0,
    [EXECUTION_LOG] nvarchar(max) NULL,
    [RESULT] int NULL,
    [SUMMARY] nvarchar(1000) NULL,
    [METRICS] nvarchar(max) NULL,
    [MANUAL] bit NULL,
    [FIRE_INSTANCE_ID] nvarchar(140) NULL,
    [JOB_INPUT] nvarchar(max) NULL,
    [JOB_INPUT_TOO_LARGE] bit NULL,
    CONSTRAINT [PK_WDE_QRTZ_EXECUTION_HISTORY] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_MISFIRE_HISTORY] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NULL,
    [JOB_GROUP] nvarchar(150) NULL,
    [MISFIRE_TIME] bigint NOT NULL,
    [SCHED_TIME] bigint NULL,
    [REASON] int NULL,
    CONSTRAINT [PK_WDE_QRTZ_MISFIRE_HISTORY] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_JOB_STATUS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [FIRST_FIRED_TIME] bigint NOT NULL,
    [LAST_FIRED_TIME] bigint NOT NULL,
    [LAST_RESULT] int NOT NULL,
    [LAST_RUN_TIME] bigint NOT NULL,
    [LAST_INSTANCE_NAME] nvarchar(200) NOT NULL,
    [LAST_ENTRY_ID] nvarchar(140) NULL,
    [LAST_SUMMARY] nvarchar(1000) NULL,
    [LAST_SUCCESS_TIME] bigint NULL,
    [LAST_FAILURE_TIME] bigint NULL,
    [LAST_FAILURE_MESSAGE] nvarchar(1000) NULL,
    [CONSECUTIVE_FAILURES] int NOT NULL DEFAULT 0,
    [RUN_COUNT] bigint NOT NULL DEFAULT 0,
    [FAILURE_COUNT] bigint NOT NULL DEFAULT 0,
    CONSTRAINT [PK_WDE_QRTZ_JOB_STATUS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_GROUP],
    [JOB_NAME]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_CALENDARS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [CALENDAR_NAME] nvarchar(200) NOT NULL,
    [CALENDAR] varbinary(max) NOT NULL,
    CONSTRAINT [PK_WDE_QRTZ_CALENDARS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [CALENDAR_NAME]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_CRON_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [CRON_EXPRESSION] nvarchar(120) NOT NULL,
    [TIME_ZONE_ID] nvarchar(80),
    CONSTRAINT [PK_WDE_QRTZ_CRON_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_FIRED_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [ENTRY_ID] nvarchar(140) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [FIRED_TIME] bigint NOT NULL,
    [SCHED_TIME] bigint NOT NULL,
    [PRIORITY] int NOT NULL,
    [STATE] nvarchar(16) NOT NULL,
    [JOB_NAME] nvarchar(150) NULL,
    [JOB_GROUP] nvarchar(150) NULL,
    [IS_NONCONCURRENT] bit NULL,
    [REQUESTS_RECOVERY] bit NULL,
    [EXECUTION_GROUP] nvarchar(200) NULL,
    [PROGRESS] int NULL,
    [PROGRESS_MESSAGE] nvarchar(250) NULL,
    CONSTRAINT [PK_WDE_QRTZ_FIRED_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [ENTRY_ID]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_PAUSED_TRIGGER_GRPS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    CONSTRAINT [PK_WDE_QRTZ_PAUSED_TRIGGER_GRPS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_PAUSED_JOB_GRPS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    CONSTRAINT [PK_WDE_QRTZ_PAUSED_JOB_GRPS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_SCHEDULER_STATE] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [INSTANCE_NAME] nvarchar(200) NOT NULL,
    [LAST_CHECKIN_TIME] bigint NOT NULL,
    [CHECKIN_INTERVAL] bigint NOT NULL,
    CONSTRAINT [PK_WDE_QRTZ_SCHEDULER_STATE] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [INSTANCE_NAME]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_LOCKS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [LOCK_NAME] nvarchar(40) NOT NULL,
    CONSTRAINT [PK_WDE_QRTZ_LOCKS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [LOCK_NAME]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_JOB_DETAILS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [DESCRIPTION] nvarchar(250) NULL,
    [JOB_CLASS_NAME] nvarchar(250) NOT NULL,
    [IS_DURABLE] bit NOT NULL,
    [IS_NONCONCURRENT] bit NOT NULL,
    [IS_UPDATE_DATA] bit NOT NULL,
    [REQUESTS_RECOVERY] bit NOT NULL,
    [JOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDE_QRTZ_JOB_DETAILS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_SIMPLE_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [REPEAT_COUNT] int NOT NULL,
    [REPEAT_INTERVAL] bigint NOT NULL,
    [TIMES_TRIGGERED] int NOT NULL,
    CONSTRAINT [PK_WDE_QRTZ_SIMPLE_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_SIMPROP_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [STR_PROP_1] nvarchar(512) NULL,
    [STR_PROP_2] nvarchar(512) NULL,
    [STR_PROP_3] nvarchar(512) NULL,
    [INT_PROP_1] int NULL,
    [INT_PROP_2] int NULL,
    [LONG_PROP_1] bigint NULL,
    [LONG_PROP_2] bigint NULL,
    [DEC_PROP_1] numeric(13,4) NULL,
    [DEC_PROP_2] numeric(13,4) NULL,
    [BOOL_PROP_1] bit NULL,
    [BOOL_PROP_2] bit NULL,
    [TIME_ZONE_ID] nvarchar(80) NULL,
    CONSTRAINT [PK_WDE_QRTZ_SIMPROP_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_BLOB_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [BLOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDE_QRTZ_BLOB_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].[WDE_QRTZ_TRIGGERS] (
    [SCHED_NAME] nvarchar(120) NOT NULL,
    [TRIGGER_NAME] nvarchar(150) NOT NULL,
    [TRIGGER_GROUP] nvarchar(150) NOT NULL,
    [JOB_NAME] nvarchar(150) NOT NULL,
    [JOB_GROUP] nvarchar(150) NOT NULL,
    [DESCRIPTION] nvarchar(250) NULL,
    [NEXT_FIRE_TIME] bigint NULL,
    [PREV_FIRE_TIME] bigint NULL,
    [PRIORITY] int NULL,
    [TRIGGER_STATE] nvarchar(16) NOT NULL,
    [TRIGGER_TYPE] nvarchar(8) NOT NULL,
    [START_TIME] bigint NOT NULL,
    [END_TIME] bigint NULL,
    [CALENDAR_NAME] nvarchar(200) NULL,
    [MISFIRE_INSTR] int NULL,
    [MISFIRE_ORIG_FIRE_TIME] bigint NULL,
    [EXECUTION_GROUP] nvarchar(200) NULL,
    [PREFERRED_NODE] nvarchar(200) NULL,
    [PREFERRED_NODE_AUTO] bit NOT NULL DEFAULT 0,
    [RETRY_POLICY] nvarchar(250) NULL,
    [RETRY_ATTEMPT] int NULL,
    [CONTINUES_TRIGGER_NAME] nvarchar(150) NULL,
    [CONTINUES_TRIGGER_GROUP] nvarchar(150) NULL,
    [CONTINUATION_CONDITION] int NULL,
    [OVERLAP_POLICY] int NULL,
    [PAUSE_REASON] nvarchar(250) NULL,
    [PAUSED_BY] nvarchar(200) NULL,
    [PAUSED_AT] bigint NULL,
    [JOB_DATA] varbinary(max) NULL,
    CONSTRAINT [PK_WDE_QRTZ_TRIGGERS] PRIMARY KEY  CLUSTERED
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  )
);

CREATE TABLE [workerdock].TemplateCatalog (
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    MetadataDigest char(64) NOT NULL,
    Enabled bit NOT NULL CONSTRAINT DF_TemplateCatalog_Enabled DEFAULT 1,
    CONSTRAINT PK_TemplateCatalog PRIMARY KEY (TemplateId, TemplateVersion),
    DescriptorJson nvarchar(max) NULL,
    Retired bit NOT NULL CONSTRAINT DF_TemplateCatalog_Retired DEFAULT 0,
    CONSTRAINT CK_TemplateCatalog_Descriptor CHECK (DescriptorJson IS NULL OR ISJSON(DescriptorJson)=1),
    CONSTRAINT CK_TemplateCatalog_Retired CHECK (Retired=0 OR Enabled=0)
);

CREATE TABLE [workerdock].TemplatePolicies (
    TemplateId nvarchar(100) NOT NULL CONSTRAINT PK_TemplatePolicies PRIMARY KEY,
    DesiredLimit int NOT NULL CONSTRAINT DF_TemplatePolicies_Desired DEFAULT 0,
    EffectiveLimit int NOT NULL CONSTRAINT DF_TemplatePolicies_Effective DEFAULT 0,
    DesiredRevision bigint NOT NULL CONSTRAINT DF_TemplatePolicies_DesiredRevision DEFAULT 0,
    EffectiveRevision bigint NOT NULL CONSTRAINT DF_TemplatePolicies_EffectiveRevision DEFAULT 0,
    StartGateClosed bit NOT NULL CONSTRAINT DF_TemplatePolicies_Gate DEFAULT 1,
    CONSTRAINT CK_TemplatePolicies_Limits CHECK (DesiredLimit >= 0 AND EffectiveLimit >= 0)
);

CREATE TABLE [workerdock].Jobs (
    JobId uniqueidentifier NOT NULL CONSTRAINT PK_Jobs PRIMARY KEY,
    CurrentRevision bigint NULL,
    Enabled bit NOT NULL CONSTRAINT DF_Jobs_Enabled DEFAULT 0,
    Archived bit NOT NULL CONSTRAINT DF_Jobs_Archived DEFAULT 0,
    ConcurrencyLimit int NOT NULL CONSTRAINT DF_Jobs_Concurrency DEFAULT 1,
    Version rowversion NOT NULL,
    CONSTRAINT CK_Jobs_Concurrency CHECK (ConcurrencyLimit >= 0),
    ActivationBoundaryUtc datetime2(7) NULL,
    DisabledAtUtc datetime2(7) NULL
);

CREATE TABLE [workerdock].JobRevisions (
    JobId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    Name nvarchar(200) NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    ProfileId uniqueidentifier NULL,
    PolicyJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_JobRevisions PRIMARY KEY (JobId, Revision),
    CONSTRAINT CK_JobRevisions_Revision CHECK (Revision > 0),
    CONSTRAINT CK_JobRevisions_Configuration CHECK (ISJSON(ConfigurationJson) = 1),
    CONSTRAINT CK_JobRevisions_Policy CHECK (ISJSON(PolicyJson) = 1),
    DynamicValuesJson nvarchar(max) NULL,
    CONSTRAINT CK_JobRevisions_DynamicValuesJson CHECK (DynamicValuesJson IS NULL OR ISJSON(DynamicValuesJson)=1)
);

CREATE TABLE [workerdock].Workers (
    InstallationId uniqueidentifier NOT NULL CONSTRAINT PK_Workers PRIMARY KEY,
    BootId nvarchar(100) NOT NULL,
    CatalogDigest char(64) NOT NULL,
    CapacityCeiling int NOT NULL,
    LiveLimit int NOT NULL,
    LastHeartbeatUtc datetime2(7) NOT NULL,
    CONSTRAINT CK_Workers_Capacity CHECK (CapacityCeiling >= 0 AND LiveLimit BETWEEN 0 AND CapacityCeiling),
    AcknowledgedRevision bigint NOT NULL CONSTRAINT DF_Workers_Ack DEFAULT -1,
    State nvarchar(30) NOT NULL CONSTRAINT DF_Workers_State DEFAULT N''Joining'',
    ReservationsRecovered bit NOT NULL CONSTRAINT DF_Workers_Recovered DEFAULT 0
);

CREATE TABLE [workerdock].WorkerCapabilities (
    InstallationId uniqueidentifier NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    CONSTRAINT PK_WorkerCapabilities PRIMARY KEY (InstallationId,TemplateId,TemplateVersion)
);

CREATE TABLE [workerdock].ServiceHealth (
    ServiceId uniqueidentifier NOT NULL CONSTRAINT PK_ServiceHealth PRIMARY KEY,
    ServiceKind nvarchar(40) NOT NULL,
    BootId nvarchar(100) NOT NULL,
    LastHeartbeatUtc datetime2(7) NOT NULL,
    Ready bit NOT NULL
);

CREATE TABLE [workerdock].PolicyRevisions (
    PolicyId uniqueidentifier NOT NULL CONSTRAINT PK_PolicyRevisions PRIMARY KEY,
    PolicyKind nvarchar(40) NOT NULL,
    DesiredRevision bigint NOT NULL,
    EffectiveRevision bigint NOT NULL,
    State nvarchar(30) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CatalogDigest char(64) NULL
);

CREATE TABLE [workerdock].Executions (
    ExecutionId uniqueidentifier NOT NULL CONSTRAINT PK_Executions PRIMARY KEY,
    JobId uniqueidentifier NOT NULL,
    JobRevision bigint NOT NULL,
    ProfileRevisionId uniqueidentifier NULL,
    TriggerKind nvarchar(20) NOT NULL,
    ScheduleId uniqueidentifier NULL,
    ScheduleRevision bigint NULL,
    ScheduledForUtc datetime2(7) NULL,
    RequestKey nvarchar(200) NULL,
    IdempotencyKey nvarchar(100) NOT NULL,
    State nvarchar(30) NOT NULL,
    Certainty nvarchar(10) NOT NULL,
    AvailableAtUtc datetime2(7) NOT NULL,
    CorrelationId uniqueidentifier NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT CK_Executions_State CHECK (State IN (N''Queued'',N''Running'',N''RetryWaiting'',N''NeedsReview'',N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped'')),
    CONSTRAINT CK_Executions_Certainty CHECK (Certainty IN (N''Known'',N''Unknown'')),
    StopRequested bit NOT NULL CONSTRAINT DF_Executions_Stop DEFAULT 0,
    ForceStop bit NOT NULL CONSTRAINT DF_Executions_Force DEFAULT 0,
    AdmissionSequence bigint IDENTITY(1,1) NOT NULL,
    CONSTRAINT CK_Executions_Occurrence CHECK((TriggerKind=N''Scheduled'' AND ScheduleId IS NOT NULL AND ScheduleRevision>0 AND ScheduledForUtc IS NOT NULL) OR
        (TriggerKind<>N''Scheduled'' AND ScheduleId IS NULL AND ScheduleRevision IS NULL AND ScheduledForUtc IS NULL)),
    AggregatedAtUtc datetime2(7) NULL,
    ValueContextJson nvarchar(max) NULL,
    FirstStartedAtUtc datetime2(7) NULL,
    ValueSnapshotJson nvarchar(max) NULL,
    ValueResolutionError nvarchar(80) NULL,
    CONSTRAINT CK_Executions_ValueContextJson CHECK (ValueContextJson IS NULL OR ISJSON(ValueContextJson)=1),
    CONSTRAINT CK_Executions_ValueSnapshotJson CHECK (ValueSnapshotJson IS NULL OR ISJSON(ValueSnapshotJson)=1),
    CONSTRAINT CK_Executions_ValueSnapshotError CHECK (ValueSnapshotJson IS NULL OR ValueResolutionError IS NULL)
);

CREATE TABLE [workerdock].Attempts (
    AttemptId uniqueidentifier NOT NULL CONSTRAINT PK_Attempts PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptNumber int NOT NULL,
    InstallationId uniqueidentifier NULL,
    WorkerBootId nvarchar(100) NULL,
    Generation bigint NOT NULL CONSTRAINT DF_Attempts_Generation DEFAULT 0,
    State nvarchar(30) NOT NULL,
    LeaseExpiresUtc datetime2(7) NULL,
    StartedAtUtc datetime2(7) NULL,
    DeadlineUtc datetime2(7) NULL,
    EndedAtUtc datetime2(7) NULL,
    SafeCode nvarchar(100) NULL,
    CONSTRAINT UQ_Attempts_Ordinal UNIQUE (ExecutionId,AttemptNumber),
    CONSTRAINT CK_Attempts_Ordinal CHECK (AttemptNumber > 0),
    LocalExitConfirmed bit NOT NULL CONSTRAINT DF_Attempts_Exit DEFAULT 0,
    EffectsMayHaveOccurred bit NOT NULL CONSTRAINT DF_Attempts_Effects DEFAULT 0
);

CREATE TABLE [workerdock].JobPermits (
    PermitId uniqueidentifier NOT NULL CONSTRAINT PK_JobPermits PRIMARY KEY,
    JobId uniqueidentifier NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    Mode nvarchar(20) NOT NULL,
    AcquiredAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_JobPermits_Execution UNIQUE (ExecutionId)
);

CREATE TABLE [workerdock].Commands (
    CommandId uniqueidentifier NOT NULL CONSTRAINT PK_Commands PRIMARY KEY,
    TargetId uniqueidentifier NOT NULL,
    CommandKind nvarchar(40) NOT NULL,
    State nvarchar(20) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    PolicyRevision bigint NULL
);

CREATE TABLE [workerdock].ManualRequestDedup (
    JobId uniqueidentifier NOT NULL,
    ActorScopeHash char(64) NOT NULL,
    RequestKeyHash char(64) NOT NULL,
    ActorMode nvarchar(40) NOT NULL,
    ActorSubject nvarchar(300) NOT NULL,
    RequestKey nvarchar(200) NOT NULL,
    RequestHash char(64) NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    CommandId uniqueidentifier NOT NULL,
    ResponseState nvarchar(20) NOT NULL,
    AcceptedAtUtc datetime2(7) NOT NULL,
    ExpiresAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_ManualRequestDedup PRIMARY KEY (JobId,ActorScopeHash,RequestKeyHash)
);

CREATE TABLE [workerdock].AttemptStatusEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_AttemptStatusEvents PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptId uniqueidentifier NULL,
    State nvarchar(40) NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL
);

CREATE TABLE [workerdock].ProjectionMaintenance (
    Id tinyint NOT NULL CONSTRAINT PK_ProjectionMaintenance PRIMARY KEY CONSTRAINT CK_ProjectionMaintenance_Id CHECK(Id=1),
    AfterJobId uniqueidentifier NULL
);

CREATE TABLE [workerdock].ConnectionProfiles (
    ProfileId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfiles PRIMARY KEY,
    Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CurrentRevisionId uniqueidentifier NULL,
    Retired bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT UQ_ConnectionProfiles_Name UNIQUE(Name),
    CONSTRAINT CK_ConnectionProfiles_Name CHECK(LEN(TRIM(Name))>0)
);

CREATE TABLE [workerdock].ConnectionProfileRevisions (
    ProfileRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfileRevisions PRIMARY KEY,
    ProfileId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_ConnectionProfileRevisions_Revision UNIQUE(ProfileId,Revision),
    CONSTRAINT UQ_ConnectionProfileRevisions_Identity UNIQUE(ProfileId,ProfileRevisionId),
    CONSTRAINT CK_ConnectionProfileRevisions_Json CHECK(ISJSON(ConfigurationJson)=1 AND DATALENGTH(ConfigurationJson)<=8192),
    CONSTRAINT CK_ConnectionProfileRevisions_Revision CHECK(Revision>0)
);

CREATE TABLE [workerdock].IntegrationSettings (
    SettingsId tinyint NOT NULL CONSTRAINT PK_IntegrationSettings PRIMARY KEY,
    VaultRevision bigint NOT NULL,
    ProviderId nvarchar(128) NOT NULL,
    PublicOptionsJson nvarchar(4000) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_IntegrationSettings_Singleton CHECK(SettingsId=1),
    CONSTRAINT CK_IntegrationSettings_Revision CHECK(VaultRevision>0),
    CONSTRAINT CK_IntegrationSettings_Options CHECK(ISJSON(PublicOptionsJson)=1 AND DATALENGTH(PublicOptionsJson)<=8000)
);

CREATE TABLE [workerdock].TenantEgressPolicies (
    Name nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT PK_TenantEgressPolicies PRIMARY KEY,
    Revision bigint NOT NULL,
    HostsJson nvarchar(4000) NOT NULL,
    PortsJson nvarchar(4000) NOT NULL,
    PrivateCidrsJson nvarchar(4000) NOT NULL,
    AllowHttp bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantEgressPolicies_Revision CHECK(Revision>0),
    CONSTRAINT CK_TenantEgressPolicies_Name CHECK(Name IN(N''tenant-jobs'',N''tenant-alerts'')),
    CONSTRAINT CK_TenantEgressPolicies_Json CHECK(ISJSON(HostsJson)=1 AND ISJSON(PortsJson)=1 AND ISJSON(PrivateCidrsJson)=1)
);

CREATE TABLE [workerdock].IntegrationSettingsRevisions (
    RevisionId uniqueidentifier NOT NULL CONSTRAINT PK_IntegrationSettingsRevisions PRIMARY KEY,
    SettingsKind nvarchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SettingsName nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_IntegrationSettingsRevisions_Revision UNIQUE(SettingsKind,SettingsName,Revision),
    CONSTRAINT CK_IntegrationSettingsRevisions_Kind CHECK(SettingsKind IN(N''Vault'',N''Egress'')),
    CONSTRAINT CK_IntegrationSettingsRevisions_Name CHECK((SettingsKind=N''Vault'' AND SettingsName=N''vault'') OR (SettingsKind=N''Egress'' AND SettingsName IN(N''tenant-jobs'',N''tenant-alerts''))),
    CONSTRAINT CK_IntegrationSettingsRevisions_Json CHECK(ISJSON(ConfigurationJson)=1)
);

CREATE TABLE [workerdock].Schedules (
    ScheduleId uniqueidentifier NOT NULL CONSTRAINT PK_Schedules PRIMARY KEY,
    JobId uniqueidentifier NOT NULL CONSTRAINT UQ_Schedules_Job UNIQUE,
    Revision bigint NOT NULL,
    Kind nvarchar(16) NOT NULL,
    Cron nvarchar(120) NULL,
    ZoneId nvarchar(100) NOT NULL,
    OneTimeUtc datetime2(7) NULL,
    ActivatedUtc datetime2(7) NOT NULL,
    Paused bit NOT NULL CONSTRAINT DF_Schedules_Paused DEFAULT 0,
    Removed bit NOT NULL CONSTRAINT DF_Schedules_Removed DEFAULT 0,
    PausedAtUtc datetime2(7) NULL,
    CursorUtc datetime2(7) NOT NULL,
    MissedMode nvarchar(16) NOT NULL,
    LatenessToleranceMs bigint NOT NULL,
    CatchUpCount int NOT NULL,
    CatchUpAgeMs bigint NOT NULL,
    CatchUpRecoveryThroughUtc datetime2(7) NULL,
    CatchUpRecoveryCreated int NOT NULL CONSTRAINT DF_Schedules_CatchUpRecoveryCreated DEFAULT 0,
    ZoneRulesFingerprint char(64) NOT NULL,
    ProjectionRevision bigint NOT NULL CONSTRAINT DF_Schedules_ProjectionRevision DEFAULT 0,
    CONSTRAINT CK_Schedules_Revision CHECK(Revision>0 AND ProjectionRevision>=0),
    CONSTRAINT CK_Schedules_Kind CHECK((Kind=N''Cron'' AND Cron IS NOT NULL AND OneTimeUtc IS NULL) OR (Kind=N''Once'' AND Cron IS NULL AND OneTimeUtc IS NOT NULL)),
    CONSTRAINT CK_Schedules_Pause CHECK((Paused=0 AND PausedAtUtc IS NULL) OR (Paused=1 AND PausedAtUtc IS NOT NULL)),
    CONSTRAINT CK_Schedules_Policy CHECK(MissedMode IN(N''Skip'',N''Coalesce'',N''CatchUp'') AND LatenessToleranceMs>=0 AND
        ((MissedMode=N''CatchUp'' AND CatchUpCount BETWEEN 1 AND 100 AND CatchUpAgeMs>0) OR (MissedMode<>N''CatchUp'' AND CatchUpCount=0 AND CatchUpAgeMs=0))),
    CONSTRAINT CK_Schedules_CatchUpRecovery CHECK((CatchUpRecoveryThroughUtc IS NULL AND CatchUpRecoveryCreated=0) OR (CatchUpRecoveryThroughUtc IS NOT NULL AND CatchUpRecoveryCreated BETWEEN 0 AND 100)),
    CONSTRAINT CK_Schedules_Fingerprint CHECK(LEN(ZoneRulesFingerprint)=64 AND ZoneRulesFingerprint NOT LIKE ''%[^0-9A-F]%'')
);

CREATE TABLE [workerdock].ScheduleRecoveryEvents (
    RecoveryEventId uniqueidentifier NOT NULL CONSTRAINT PK_ScheduleRecoveryEvents PRIMARY KEY,
    ScheduleId uniqueidentifier NOT NULL,
    ScheduleRevision bigint NOT NULL,
    FromUtc datetime2(7) NOT NULL,
    ThroughUtc datetime2(7) NOT NULL,
    Policy nvarchar(16) NOT NULL,
    CreatedExecutions int NOT NULL,
    ExactMissedCount bigint NULL,
    SkippedOccurrences bigint NOT NULL CONSTRAINT DF_ScheduleRecoveryEvents_Skipped DEFAULT 0,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ScheduleRecoveryEvents_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ScheduleRecoveryEvents_Range CHECK(ThroughUtc>=FromUtc AND CreatedExecutions>=0 AND SkippedOccurrences>=0 AND (ExactMissedCount IS NULL OR ExactMissedCount>=0)),
    CONSTRAINT CK_ScheduleRecoveryEvents_Policy CHECK(Policy IN(N''Skip'',N''Coalesce'',N''CatchUp''))
);

CREATE TABLE [workerdock].ScheduleRuleScanState (
    ScanId tinyint NOT NULL CONSTRAINT PK_ScheduleRuleScanState PRIMARY KEY,
    LastScheduleId uniqueidentifier NULL,
    CONSTRAINT CK_ScheduleRuleScanState_Singleton CHECK(ScanId=1)
);

CREATE TABLE [workerdock].ExecutionEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_ExecutionEvents PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptId uniqueidentifier NULL,
    AttemptScopeId AS ISNULL(AttemptId,CONVERT(uniqueidentifier,''00000000-0000-0000-0000-000000000000'')) PERSISTED,
    Source nvarchar(20) NOT NULL,
    Sequence bigint NOT NULL,
    AtUtc datetime2(7) NOT NULL,
    Code nvarchar(80) NOT NULL,
    SafeFieldsJson nvarchar(2048) NOT NULL,
    CONSTRAINT CK_ExecutionEvents_Source CHECK(Source IN(N''Lifecycle'',N''Supervisor'',N''Handler'')),
    CONSTRAINT CK_ExecutionEvents_Sequence CHECK(Sequence>0),
    CONSTRAINT CK_ExecutionEvents_Code CHECK(LEN(Code)>0),
    CONSTRAINT CK_ExecutionEvents_Json CHECK(ISJSON(SafeFieldsJson)=1),
    CONSTRAINT UQ_ExecutionEvents_Scope UNIQUE(ExecutionId,AttemptScopeId,Source,Sequence)
);

CREATE TABLE [workerdock].ExecutionMetricAggregates (
    WindowStartUtc datetime2(7) NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    Outcome nvarchar(30) NOT NULL,
    ExecutionCount bigint NOT NULL,
    RetryCount bigint NOT NULL,
    TimeoutCount bigint NOT NULL,
    DurationMsTotal bigint NOT NULL,
    TriggerDelayMsTotal bigint NOT NULL,
    QueueDelayMsTotal bigint NOT NULL,
    UpdatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_ExecutionMetricAggregates PRIMARY KEY(WindowStartUtc,TemplateId,Outcome),
    CONSTRAINT CK_ExecutionMetricAggregates_Counts CHECK(ExecutionCount>=0 AND RetryCount>=0 AND TimeoutCount>=0 AND DurationMsTotal>=0 AND TriggerDelayMsTotal>=0 AND QueueDelayMsTotal>=0)
);

CREATE TABLE [workerdock].AlertSinks (
    SinkId uniqueidentifier NOT NULL CONSTRAINT PK_AlertSinks PRIMARY KEY,
    Name nvarchar(150) NOT NULL,
    CurrentRevision bigint NOT NULL,
    Enabled bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_AlertSinks_Name CHECK(LEN(Name)>0 AND DATALENGTH(Name)<=300),
    CONSTRAINT CK_AlertSinks_Revision CHECK(CurrentRevision>0)
);

CREATE TABLE [workerdock].AlertSinkRevisions (
    SinkRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_AlertSinkRevisions PRIMARY KEY,
    SinkId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    SinkType nvarchar(80) NOT NULL,
    ConfigurationJson nvarchar(4000) NOT NULL,
    SecretReferencesJson nvarchar(2000) NOT NULL,
    MaxAttempts int NOT NULL,
    RetryMinSeconds int NOT NULL,
    RetryMaxSeconds int NOT NULL,
    TimeoutSeconds int NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_AlertSinkRevisions_Revision UNIQUE(SinkId,Revision),
    CONSTRAINT UQ_AlertSinkRevisions_IdSink UNIQUE(SinkRevisionId,SinkId),
    CONSTRAINT CK_AlertSinkRevisions_Revision CHECK(Revision>0),
    CONSTRAINT CK_AlertSinkRevisions_Type CHECK(LEN(SinkType)>0 AND DATALENGTH(SinkType)<=160),
    CONSTRAINT CK_AlertSinkRevisions_Json CHECK(ISJSON(ConfigurationJson)=1 AND ISJSON(SecretReferencesJson)=1 AND LEFT(LTRIM(SecretReferencesJson),1)=N''[''),
    CONSTRAINT CK_AlertSinkRevisions_Retry CHECK(MaxAttempts BETWEEN 1 AND 20 AND RetryMinSeconds BETWEEN 0 AND 86400 AND RetryMaxSeconds BETWEEN RetryMinSeconds AND 86400 AND TimeoutSeconds BETWEEN 1 AND 120)
);

CREATE TABLE [workerdock].AlertRules (
    RuleId uniqueidentifier NOT NULL CONSTRAINT PK_AlertRules PRIMARY KEY,
    Name nvarchar(150) NOT NULL,
    Condition nvarchar(16) NOT NULL,
    Threshold int NOT NULL,
    WindowSeconds int NOT NULL,
    GraceSeconds int NOT NULL,
    ReminderSeconds int NULL,
    SinkId uniqueidentifier NOT NULL,
    Enabled bit NOT NULL,
    CurrentRevision bigint NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_AlertRules_Name CHECK(LEN(Name)>0 AND DATALENGTH(Name)<=300),
    CONSTRAINT CK_AlertRules_Condition CHECK(Condition IN(N''RepeatedFailure'',N''RetryExhausted'',N''Timeout'',N''LongRunning'',N''MissedExecution'',N''WorkerUnavailable'') AND (Condition NOT IN(N''LongRunning'',N''WorkerUnavailable'') OR Threshold=1)),
    CONSTRAINT CK_AlertRules_Threshold CHECK(Threshold BETWEEN 1 AND 1000),
    CONSTRAINT CK_AlertRules_Window CHECK(WindowSeconds BETWEEN 1 AND 2592000 AND GraceSeconds BETWEEN 0 AND 2592000 AND (ReminderSeconds IS NULL OR ReminderSeconds BETWEEN 0 AND 2592000)),
    CONSTRAINT CK_AlertRules_Revision CHECK(CurrentRevision>0)
);

CREATE TABLE [workerdock].AlertRuleRevisions (
    RuleRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_AlertRuleRevisions PRIMARY KEY,
    RuleId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    SnapshotJson nvarchar(4000) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_AlertRuleRevisions_Revision UNIQUE(RuleId,Revision),
    CONSTRAINT CK_AlertRuleRevisions_Revision CHECK(Revision>0),
    CONSTRAINT CK_AlertRuleRevisions_Json CHECK(ISJSON(SnapshotJson)=1 AND DATALENGTH(SnapshotJson)<=8000)
);

CREATE TABLE [workerdock].AlertIncidents (
    IncidentId uniqueidentifier NOT NULL CONSTRAINT PK_AlertIncidents PRIMARY KEY,
    RuleId uniqueidentifier NOT NULL,
    SubjectKey nvarchar(256) NOT NULL,
    Condition nvarchar(16) NOT NULL,
    State nvarchar(16) NOT NULL,
    OpenedAtUtc datetime2(7) NOT NULL,
    LastObservedAtUtc datetime2(7) NOT NULL,
    ResolvedAtUtc datetime2(7) NULL,
    NextReminderAtUtc datetime2(7) NULL,
    CONSTRAINT CK_AlertIncidents_Subject CHECK(LEN(SubjectKey)>0 AND DATALENGTH(SubjectKey)<=512),
    CONSTRAINT CK_AlertIncidents_Condition CHECK(Condition IN(N''RepeatedFailure'',N''RetryExhausted'',N''Timeout'',N''LongRunning'',N''MissedExecution'',N''WorkerUnavailable'',N''Unknown'')),
    CONSTRAINT CK_AlertIncidents_State CHECK(State IN(N''Open'',N''Unknown'',N''Resolved'')),
    CONSTRAINT CK_AlertIncidents_Times CHECK(LastObservedAtUtc>=OpenedAtUtc AND ((State=N''Resolved'' AND ResolvedAtUtc IS NOT NULL AND ResolvedAtUtc>=OpenedAtUtc AND NextReminderAtUtc IS NULL) OR (State IN(N''Open'',N''Unknown'') AND ResolvedAtUtc IS NULL)))
);

CREATE TABLE [workerdock].AlertDeliveries (
    DeliveryId uniqueidentifier NOT NULL CONSTRAINT PK_AlertDeliveries PRIMARY KEY,
    IncidentId uniqueidentifier NOT NULL,
    SinkRevisionId uniqueidentifier NOT NULL,
    Kind nvarchar(16) NOT NULL,
    Status nvarchar(16) NOT NULL,
    AttemptOrdinal int NOT NULL,
    AttemptCount int NOT NULL,
    AttemptBudget int NOT NULL,
    ManualRetryCount int NOT NULL CONSTRAINT DF_AlertDeliveries_ManualRetries DEFAULT 0,
    NextAvailableAtUtc datetime2(7) NOT NULL,
    LeaseOwner nvarchar(128) NULL,
    LeaseGeneration bigint NOT NULL,
    LeaseStartedAtUtc datetime2(7) NULL,
    LeaseExpiresAtUtc datetime2(7) NULL,
    SafeCode nvarchar(80) NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CompletedAtUtc datetime2(7) NULL,
    CONSTRAINT CK_AlertDeliveries_Kind CHECK(Kind IN(N''Open'',N''Reminder'',N''Resolve'')),
    CONSTRAINT CK_AlertDeliveries_Status CHECK(Status IN(N''Pending'',N''Leased'',N''Delivered'',N''Failed'',N''Cancelled'')),
    CONSTRAINT CK_AlertDeliveries_Attempts CHECK(AttemptCount>=0 AND AttemptBudget BETWEEN 1 AND 200 AND AttemptCount<=AttemptBudget AND ManualRetryCount BETWEEN 0 AND 10 AND AttemptOrdinal>=1 AND ((Status=N''Pending'' AND AttemptOrdinal=AttemptCount+1) OR (Status=N''Leased'' AND AttemptOrdinal=AttemptCount) OR (Status IN(N''Delivered'',N''Failed'',N''Cancelled'') AND AttemptOrdinal=AttemptCount))),
    CONSTRAINT CK_AlertDeliveries_Lease CHECK(LeaseGeneration>=0 AND ((Status=N''Leased'' AND LeaseOwner IS NOT NULL AND LEN(LeaseOwner)>0 AND LeaseStartedAtUtc IS NOT NULL AND LeaseExpiresAtUtc IS NOT NULL) OR (Status<>N''Leased'' AND LeaseOwner IS NULL AND LeaseStartedAtUtc IS NULL AND LeaseExpiresAtUtc IS NULL))),
    CONSTRAINT CK_AlertDeliveries_SafeCode CHECK(SafeCode IS NULL OR (LEN(SafeCode)>0 AND DATALENGTH(SafeCode)<=160)),
    CONSTRAINT CK_AlertDeliveries_Completed CHECK((Status IN(N''Delivered'',N''Failed'',N''Cancelled'') AND CompletedAtUtc IS NOT NULL) OR (Status IN(N''Pending'',N''Leased'') AND CompletedAtUtc IS NULL))
);

CREATE TABLE [workerdock].AlertDeliveryAttempts (
    DeliveryId uniqueidentifier NOT NULL,
    AttemptOrdinal int NOT NULL,
    LeaseGeneration bigint NOT NULL,
    Result nvarchar(16) NOT NULL,
    SafeCode nvarchar(80) NULL,
    StartedAtUtc datetime2(7) NOT NULL,
    CompletedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_AlertDeliveryAttempts PRIMARY KEY(DeliveryId,AttemptOrdinal),
    CONSTRAINT CK_AlertDeliveryAttempts_Ordinal CHECK(AttemptOrdinal>0 AND LeaseGeneration>0),
    CONSTRAINT CK_AlertDeliveryAttempts_Result CHECK(Result IN(N''Succeeded'',N''Retry'',N''PermanentFailure'',N''TimedOut'')),
    CONSTRAINT CK_AlertDeliveryAttempts_Times CHECK(CompletedAtUtc>=StartedAtUtc),
    CONSTRAINT CK_AlertDeliveryAttempts_SafeCode CHECK(SafeCode IS NULL OR (LEN(SafeCode)>0 AND DATALENGTH(SafeCode)<=160))
);

CREATE TABLE [workerdock].AlertEvaluationState (
    StateId tinyint NOT NULL CONSTRAINT PK_AlertEvaluationState PRIMARY KEY,
    LastEvaluatedAtUtc datetime2(7) NULL,
    LastRuleId uniqueidentifier NULL,
    LastSubjectKey nvarchar(256) NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_AlertEvaluationState_Singleton CHECK(StateId=1 AND (LastSubjectKey IS NULL OR LastRuleId IS NOT NULL))
);

CREATE TABLE [workerdock].RetentionPolicy (
    PolicyId tinyint NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY,
    LogDays int NOT NULL,
    ExecutionDays int NOT NULL,
    AuditDays int NOT NULL,
    ArchiveBeforeDelete bit NOT NULL,
    ArchiveDays int NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_RetentionPolicy_Singleton CHECK(PolicyId=1),
    CONSTRAINT CK_RetentionPolicy_Days CHECK(LogDays BETWEEN 1 AND 3650 AND ExecutionDays BETWEEN 1 AND 3650 AND AuditDays BETWEEN 1 AND 3650 AND ArchiveDays BETWEEN 0 AND 3650 AND (ArchiveBeforeDelete=0 OR ArchiveDays>0))
);

CREATE TABLE [workerdock].ArchiveBatches (
    BatchId uniqueidentifier NOT NULL CONSTRAINT PK_ArchiveBatches PRIMARY KEY,
    RelativePath nvarchar(500) NOT NULL,
    Sha256 char(64) NOT NULL,
    [RowCount] bigint NOT NULL,
    EligibleThroughUtc datetime2(7) NOT NULL,
    LogCutoffUtc datetime2(7) NOT NULL,
    State nvarchar(16) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    FinalizedAtUtc datetime2(7) NULL,
    ArchiveRemovedAtUtc datetime2(7) NULL,
    CONSTRAINT UQ_ArchiveBatches_Path UNIQUE(RelativePath),
    CONSTRAINT CK_ArchiveBatches_Path CHECK(LEN(RelativePath)>0 AND LEFT(RelativePath,1)<>N''/'' AND RelativePath NOT LIKE N''%..%''),
    CONSTRAINT CK_ArchiveBatches_Checksum CHECK(LEN(Sha256)=64 AND Sha256 NOT LIKE ''%[^0-9A-Fa-f]%''),
    CONSTRAINT CK_ArchiveBatches_Rows CHECK([RowCount]>=0),
    CONSTRAINT CK_ArchiveBatches_State CHECK((State=N''Writing'' AND FinalizedAtUtc IS NULL) OR (State=N''Finalized'' AND FinalizedAtUtc IS NOT NULL AND FinalizedAtUtc>=CreatedAtUtc) OR (State=N''Failed'' AND FinalizedAtUtc IS NULL))
);

CREATE TABLE [workerdock].ArchiveBatchItems (
    BatchId uniqueidentifier NOT NULL,
    RecordKind nvarchar(32) NOT NULL,
    RecordId uniqueidentifier NOT NULL,
    CONSTRAINT PK_ArchiveBatchItems PRIMARY KEY(BatchId,RecordKind,RecordId),
    CONSTRAINT CK_ArchiveBatchItems_Kind CHECK(RecordKind IN(N''AuditEvent'',N''Execution'',N''ExecutionAttempt'',N''ExecutionEvent'',N''AttemptStatusEvent'',N''JobPermit'',N''ExecutionDispatchOutbox'',N''ManualRequestDedup''))
);

CREATE TABLE [workerdock].RetentionRequestTombstones (
    JobId uniqueidentifier NOT NULL,
    ActorScopeHash char(64) NOT NULL,
    RequestKeyHash char(64) NOT NULL,
    RequestHash char(64) NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    CommandId uniqueidentifier NOT NULL,
    ResponseState nvarchar(20) NOT NULL,
    AcceptedAtUtc datetime2(7) NOT NULL,
    ExpiresAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_RetentionRequestTombstones PRIMARY KEY(JobId,ActorScopeHash,RequestKeyHash),
    CONSTRAINT CK_RetentionRequestTombstones_Hashes CHECK(ActorScopeHash NOT LIKE ''%[^0-9A-Fa-f]%'' AND RequestKeyHash NOT LIKE ''%[^0-9A-Fa-f]%'' AND RequestHash NOT LIKE ''%[^0-9A-Fa-f]%''),
    CONSTRAINT CK_RetentionRequestTombstones_Times CHECK(ExpiresAtUtc>=AcceptedAtUtc)
);

CREATE TABLE [workerdock].RetentionPurgeAudit (
    PurgeId uniqueidentifier NOT NULL CONSTRAINT PK_RetentionPurgeAudit PRIMARY KEY,
    PurgeKind nvarchar(32) NOT NULL,
    CutoffUtc datetime2(7) NOT NULL,
    PurgedRowCount int NOT NULL,
    PurgedAtUtc datetime2(7) NOT NULL,
    ArchiveBatchId uniqueidentifier NULL,
    CONSTRAINT CK_RetentionPurgeAudit_Kind CHECK(PurgeKind IN(N''AuditEvents'',N''RequestTombstones'',N''RetentionItems'')),
    CONSTRAINT CK_RetentionPurgeAudit_Count CHECK(PurgedRowCount>=0)
);';

    -- Final foreign keys and indexes.
    EXEC sys.sp_executesql N'ALTER TABLE [workerdock].[WDS_QRTZ_CRON_TRIGGERS] ADD CONSTRAINT [FK_WDS_QRTZ_CRON_TRIGGERS_WDS_QRTZ_TRIGGERS] FOREIGN KEY
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDS_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDS_QRTZ_SIMPLE_TRIGGERS] ADD CONSTRAINT [FK_WDS_QRTZ_SIMPLE_TRIGGERS_WDS_QRTZ_TRIGGERS] FOREIGN KEY
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDS_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDS_QRTZ_SIMPROP_TRIGGERS] ADD CONSTRAINT [FK_WDS_QRTZ_SIMPROP_TRIGGERS_WDS_QRTZ_TRIGGERS] FOREIGN KEY
  (
	[SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDS_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDS_QRTZ_TRIGGERS] ADD CONSTRAINT [FK_WDS_QRTZ_TRIGGERS_WDS_QRTZ_JOB_DETAILS] FOREIGN KEY
  (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  ) REFERENCES [workerdock].[WDS_QRTZ_JOB_DETAILS] (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  );
ALTER TABLE [workerdock].[WDE_QRTZ_CRON_TRIGGERS] ADD CONSTRAINT [FK_WDE_QRTZ_CRON_TRIGGERS_WDE_QRTZ_TRIGGERS] FOREIGN KEY
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDE_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDE_QRTZ_SIMPLE_TRIGGERS] ADD CONSTRAINT [FK_WDE_QRTZ_SIMPLE_TRIGGERS_WDE_QRTZ_TRIGGERS] FOREIGN KEY
  (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDE_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDE_QRTZ_SIMPROP_TRIGGERS] ADD CONSTRAINT [FK_WDE_QRTZ_SIMPROP_TRIGGERS_WDE_QRTZ_TRIGGERS] FOREIGN KEY
  (
	[SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) REFERENCES [workerdock].[WDE_QRTZ_TRIGGERS] (
    [SCHED_NAME],
    [TRIGGER_NAME],
    [TRIGGER_GROUP]
  ) ON DELETE CASCADE;
ALTER TABLE [workerdock].[WDE_QRTZ_TRIGGERS] ADD CONSTRAINT [FK_WDE_QRTZ_TRIGGERS_WDE_QRTZ_JOB_DETAILS] FOREIGN KEY
  (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  ) REFERENCES [workerdock].[WDE_QRTZ_JOB_DETAILS] (
    [SCHED_NAME],
    [JOB_NAME],
    [JOB_GROUP]
  );
ALTER TABLE [workerdock].Jobs ADD CONSTRAINT FK_Jobs_CurrentRevision FOREIGN KEY (JobId,CurrentRevision)
    REFERENCES [workerdock].JobRevisions(JobId,Revision);
ALTER TABLE [workerdock].JobRevisions ADD CONSTRAINT FK_JobRevisions_Jobs FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId);
ALTER TABLE [workerdock].JobRevisions ADD CONSTRAINT FK_JobRevisions_Catalog FOREIGN KEY (TemplateId,TemplateVersion) REFERENCES [workerdock].TemplateCatalog(TemplateId,TemplateVersion);
ALTER TABLE [workerdock].WorkerCapabilities ADD CONSTRAINT FK_WorkerCapabilities_Worker FOREIGN KEY (InstallationId) REFERENCES [workerdock].Workers(InstallationId);
ALTER TABLE [workerdock].WorkerCapabilities ADD CONSTRAINT FK_WorkerCapabilities_Catalog FOREIGN KEY (TemplateId,TemplateVersion) REFERENCES [workerdock].TemplateCatalog(TemplateId,TemplateVersion);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT FK_Executions_JobRevision FOREIGN KEY (JobId,JobRevision) REFERENCES [workerdock].JobRevisions(JobId,Revision);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT FK_Executions_ProfileRevision FOREIGN KEY(ProfileRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileRevisionId);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT FK_Executions_Schedules FOREIGN KEY(ScheduleId) REFERENCES [workerdock].Schedules(ScheduleId);
ALTER TABLE [workerdock].Attempts ADD CONSTRAINT FK_Attempts_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId);
ALTER TABLE [workerdock].Attempts ADD CONSTRAINT FK_Attempts_Worker FOREIGN KEY (InstallationId) REFERENCES [workerdock].Workers(InstallationId);
ALTER TABLE [workerdock].JobPermits ADD CONSTRAINT FK_JobPermits_Job FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId);
ALTER TABLE [workerdock].JobPermits ADD CONSTRAINT FK_JobPermits_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId);
ALTER TABLE [workerdock].ManualRequestDedup ADD CONSTRAINT FK_ManualRequestDedup_Job FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId);
ALTER TABLE [workerdock].ManualRequestDedup ADD CONSTRAINT FK_ManualRequestDedup_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId);
ALTER TABLE [workerdock].ManualRequestDedup ADD CONSTRAINT FK_ManualRequestDedup_Command FOREIGN KEY (CommandId) REFERENCES [workerdock].Commands(CommandId);
ALTER TABLE [workerdock].AttemptStatusEvents ADD CONSTRAINT FK_AttemptStatusEvents_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId);
ALTER TABLE [workerdock].AttemptStatusEvents ADD CONSTRAINT FK_AttemptStatusEvents_Attempt FOREIGN KEY (AttemptId) REFERENCES [workerdock].Attempts(AttemptId);
ALTER TABLE [workerdock].ConnectionProfiles ADD CONSTRAINT FK_ConnectionProfiles_Current FOREIGN KEY(ProfileId,CurrentRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileId,ProfileRevisionId);
ALTER TABLE [workerdock].ConnectionProfileRevisions ADD CONSTRAINT FK_ConnectionProfileRevisions_Profile FOREIGN KEY(ProfileId) REFERENCES [workerdock].ConnectionProfiles(ProfileId);
ALTER TABLE [workerdock].Schedules ADD CONSTRAINT FK_Schedules_Jobs FOREIGN KEY(JobId) REFERENCES [workerdock].Jobs(JobId);
ALTER TABLE [workerdock].ExecutionEvents ADD CONSTRAINT FK_ExecutionEvents_Execution FOREIGN KEY(ExecutionId) REFERENCES [workerdock].Executions(ExecutionId);
ALTER TABLE [workerdock].ExecutionEvents ADD CONSTRAINT FK_ExecutionEvents_Attempt FOREIGN KEY(AttemptId) REFERENCES [workerdock].Attempts(AttemptId);
ALTER TABLE [workerdock].AlertSinkRevisions ADD CONSTRAINT FK_AlertSinkRevisions_Sink FOREIGN KEY(SinkId) REFERENCES [workerdock].AlertSinks(SinkId);
ALTER TABLE [workerdock].AlertRules ADD CONSTRAINT FK_AlertRules_Sink FOREIGN KEY(SinkId) REFERENCES [workerdock].AlertSinks(SinkId);
ALTER TABLE [workerdock].AlertRuleRevisions ADD CONSTRAINT FK_AlertRuleRevisions_Rule FOREIGN KEY(RuleId) REFERENCES [workerdock].AlertRules(RuleId);
ALTER TABLE [workerdock].AlertIncidents ADD CONSTRAINT FK_AlertIncidents_Rule FOREIGN KEY(RuleId) REFERENCES [workerdock].AlertRules(RuleId);
ALTER TABLE [workerdock].AlertDeliveries ADD CONSTRAINT FK_AlertDeliveries_Incident FOREIGN KEY(IncidentId) REFERENCES [workerdock].AlertIncidents(IncidentId);
ALTER TABLE [workerdock].AlertDeliveries ADD CONSTRAINT FK_AlertDeliveries_SinkRevision FOREIGN KEY(SinkRevisionId) REFERENCES [workerdock].AlertSinkRevisions(SinkRevisionId);
ALTER TABLE [workerdock].AlertDeliveryAttempts ADD CONSTRAINT FK_AlertDeliveryAttempts_Delivery FOREIGN KEY(DeliveryId) REFERENCES [workerdock].AlertDeliveries(DeliveryId);
ALTER TABLE [workerdock].AlertEvaluationState ADD CONSTRAINT FK_AlertEvaluationState_Rule FOREIGN KEY(LastRuleId) REFERENCES [workerdock].AlertRules(RuleId);
ALTER TABLE [workerdock].ArchiveBatchItems ADD CONSTRAINT FK_ArchiveBatchItems_Batch FOREIGN KEY(BatchId) REFERENCES [workerdock].ArchiveBatches(BatchId);
ALTER TABLE [workerdock].RetentionPurgeAudit ADD CONSTRAINT FK_RetentionPurgeAudit_ArchiveBatch FOREIGN KEY(ArchiveBatchId) REFERENCES [workerdock].ArchiveBatches(BatchId);

CREATE INDEX IX_Outbox_Pending ON [workerdock].Outbox(AvailableAtUtc, CreatedAtUtc) WHERE DispatchedAtUtc IS NULL;
CREATE INDEX [IDX_WDS_QRTZ_J_G_N]                 ON [workerdock].[WDS_QRTZ_JOB_DETAILS](SCHED_NAME, JOB_GROUP, JOB_NAME);
CREATE INDEX [IDX_WDS_QRTZ_T_J]                   ON [workerdock].[WDS_QRTZ_TRIGGERS](SCHED_NAME, JOB_NAME, JOB_GROUP);
CREATE INDEX [IDX_WDS_QRTZ_T_G_N]                 ON [workerdock].[WDS_QRTZ_TRIGGERS](SCHED_NAME, TRIGGER_GROUP, TRIGGER_NAME);
CREATE INDEX [IDX_WDS_QRTZ_T_C]                   ON [workerdock].[WDS_QRTZ_TRIGGERS](SCHED_NAME, CALENDAR_NAME);
CREATE INDEX [IDX_WDS_QRTZ_T_NFT_ST]              ON [workerdock].[WDS_QRTZ_TRIGGERS](SCHED_NAME, TRIGGER_STATE, NEXT_FIRE_TIME ASC, PRIORITY DESC, MISFIRE_INSTR);
CREATE INDEX [IDX_WDS_QRTZ_FT_INST_JOB_REQ_RCVRY] ON [workerdock].[WDS_QRTZ_FIRED_TRIGGERS](SCHED_NAME, INSTANCE_NAME, REQUESTS_RECOVERY);
CREATE INDEX [IDX_WDS_QRTZ_FT_J_G]                ON [workerdock].[WDS_QRTZ_FIRED_TRIGGERS](SCHED_NAME, JOB_NAME, JOB_GROUP);
CREATE INDEX [IDX_WDS_QRTZ_FT_T_G]                ON [workerdock].[WDS_QRTZ_FIRED_TRIGGERS](SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP);
CREATE INDEX [IDX_WDS_QRTZ_EH_FIRED_TIME]         ON [workerdock].[WDS_QRTZ_EXECUTION_HISTORY](SCHED_NAME, FIRED_TIME);
CREATE INDEX [IDX_WDS_QRTZ_EH_INST]               ON [workerdock].[WDS_QRTZ_EXECUTION_HISTORY](SCHED_NAME, INSTANCE_NAME);
CREATE INDEX [IDX_WDS_QRTZ_EH_JOB_TIME]           ON [workerdock].[WDS_QRTZ_EXECUTION_HISTORY](SCHED_NAME, JOB_GROUP, JOB_NAME, FIRED_TIME);
CREATE INDEX [IDX_WDS_QRTZ_MH_MISFIRE_TIME]       ON [workerdock].[WDS_QRTZ_MISFIRE_HISTORY](SCHED_NAME, MISFIRE_TIME);
CREATE INDEX [IDX_WDS_QRTZ_MH_INST]               ON [workerdock].[WDS_QRTZ_MISFIRE_HISTORY](SCHED_NAME, INSTANCE_NAME);
CREATE INDEX [IDX_WDE_QRTZ_J_G_N]                 ON [workerdock].[WDE_QRTZ_JOB_DETAILS](SCHED_NAME, JOB_GROUP, JOB_NAME);
CREATE INDEX [IDX_WDE_QRTZ_T_J]                   ON [workerdock].[WDE_QRTZ_TRIGGERS](SCHED_NAME, JOB_NAME, JOB_GROUP);
CREATE INDEX [IDX_WDE_QRTZ_T_G_N]                 ON [workerdock].[WDE_QRTZ_TRIGGERS](SCHED_NAME, TRIGGER_GROUP, TRIGGER_NAME);
CREATE INDEX [IDX_WDE_QRTZ_T_C]                   ON [workerdock].[WDE_QRTZ_TRIGGERS](SCHED_NAME, CALENDAR_NAME);
CREATE INDEX [IDX_WDE_QRTZ_T_NFT_ST]              ON [workerdock].[WDE_QRTZ_TRIGGERS](SCHED_NAME, TRIGGER_STATE, NEXT_FIRE_TIME ASC, PRIORITY DESC, MISFIRE_INSTR);
CREATE INDEX [IDX_WDE_QRTZ_FT_INST_JOB_REQ_RCVRY] ON [workerdock].[WDE_QRTZ_FIRED_TRIGGERS](SCHED_NAME, INSTANCE_NAME, REQUESTS_RECOVERY);
CREATE INDEX [IDX_WDE_QRTZ_FT_J_G]                ON [workerdock].[WDE_QRTZ_FIRED_TRIGGERS](SCHED_NAME, JOB_NAME, JOB_GROUP);
CREATE INDEX [IDX_WDE_QRTZ_FT_T_G]                ON [workerdock].[WDE_QRTZ_FIRED_TRIGGERS](SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP);
CREATE INDEX [IDX_WDE_QRTZ_EH_FIRED_TIME]         ON [workerdock].[WDE_QRTZ_EXECUTION_HISTORY](SCHED_NAME, FIRED_TIME);
CREATE INDEX [IDX_WDE_QRTZ_EH_INST]               ON [workerdock].[WDE_QRTZ_EXECUTION_HISTORY](SCHED_NAME, INSTANCE_NAME);
CREATE INDEX [IDX_WDE_QRTZ_EH_JOB_TIME]           ON [workerdock].[WDE_QRTZ_EXECUTION_HISTORY](SCHED_NAME, JOB_GROUP, JOB_NAME, FIRED_TIME);
CREATE INDEX [IDX_WDE_QRTZ_MH_MISFIRE_TIME]       ON [workerdock].[WDE_QRTZ_MISFIRE_HISTORY](SCHED_NAME, MISFIRE_TIME);
CREATE INDEX [IDX_WDE_QRTZ_MH_INST]               ON [workerdock].[WDE_QRTZ_MISFIRE_HISTORY](SCHED_NAME, INSTANCE_NAME);
CREATE UNIQUE INDEX UX_Executions_Occurrence ON [workerdock].Executions(ScheduleId,ScheduleRevision,ScheduledForUtc) WHERE ScheduleId IS NOT NULL;
CREATE INDEX IX_Executions_JobStateTime ON [workerdock].Executions(JobId,State,AvailableAtUtc);
CREATE INDEX IX_Attempts_Lease ON [workerdock].Attempts(State,LeaseExpiresUtc) WHERE LeaseExpiresUtc IS NOT NULL;
CREATE INDEX IX_JobPermits_Job ON [workerdock].JobPermits(JobId,AcquiredAtUtc);
CREATE INDEX IX_ManualRequestDedup_Expiry ON [workerdock].ManualRequestDedup(ExpiresAtUtc);
CREATE INDEX IX_Outbox_Projection ON [workerdock].Outbox(ProjectionState,AvailableAtUtc,CreatedAtUtc) INCLUDE(EntityId,EntityRevision);
CREATE INDEX IX_Outbox_Review ON [workerdock].Outbox(ProjectionState,ReviewAtUtc) INCLUDE(EventId,DispatchedAtUtc);
CREATE INDEX IX_AttemptStatusEvents_Execution ON [workerdock].AttemptStatusEvents(ExecutionId,OccurredAtUtc,EventId);
CREATE INDEX IX_Executions_Admission ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State);
CREATE UNIQUE INDEX UX_Workers_Boot ON [workerdock].Workers(BootId);
CREATE INDEX IX_Executions_Maintenance ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State) WHERE State IN(N''Queued'',N''RetryWaiting'');
CREATE INDEX IX_Commands_PendingTarget ON [workerdock].Commands(TargetId,CommandKind) INCLUDE(CommandId) WHERE State=N''Pending'';
CREATE INDEX IX_Executions_AdmissionActive ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State,JobRevision) WHERE State IN(N''Queued'',N''Running'',N''RetryWaiting'',N''NeedsReview'');
CREATE INDEX IX_Outbox_Execution ON [workerdock].Outbox(EntityId,EventType) INCLUDE(ProjectionState,DispatchedAtUtc,EntityRevision,PayloadDigest);
CREATE INDEX IX_Schedules_Cursor ON [workerdock].Schedules(Paused,CursorUtc,ScheduleId) INCLUDE(JobId,Revision,ActivatedUtc,Kind,Cron,ZoneId,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs);
CREATE INDEX IX_ScheduleRecoveryEvents_Schedule ON [workerdock].ScheduleRecoveryEvents(ScheduleId,ScheduleRevision,ThroughUtc);
CREATE INDEX IX_Executions_Aggregation ON [workerdock].Executions(AggregatedAtUtc,CreatedAtUtc,ExecutionId)
    INCLUDE(JobId,JobRevision,State,AvailableAtUtc,ScheduledForUtc) WHERE AggregatedAtUtc IS NULL;
CREATE INDEX IX_ExecutionEvents_Page ON [workerdock].ExecutionEvents(ExecutionId,AtUtc,EventId)
    INCLUDE(AttemptId,Source,Sequence,Code,SafeFieldsJson);
CREATE UNIQUE INDEX UX_AlertIncidents_OpenSubject ON [workerdock].AlertIncidents(RuleId,Condition,SubjectKey) WHERE State IN(N''Open'',N''Unknown'');
CREATE INDEX IX_AlertIncidents_RuleObserved ON [workerdock].AlertIncidents(RuleId,LastObservedAtUtc DESC,IncidentId) INCLUDE(SubjectKey,Condition,State,OpenedAtUtc,ResolvedAtUtc,NextReminderAtUtc);
CREATE INDEX IX_AlertIncidents_Reminders ON [workerdock].AlertIncidents(NextReminderAtUtc,IncidentId) INCLUDE(RuleId,SubjectKey,Condition,OpenedAtUtc,LastObservedAtUtc) WHERE State=N''Open'' AND NextReminderAtUtc IS NOT NULL;
CREATE INDEX IX_AlertDeliveries_Due ON [workerdock].AlertDeliveries(Status,NextAvailableAtUtc,DeliveryId) INCLUDE(IncidentId,SinkRevisionId,Kind,AttemptOrdinal,AttemptCount,LeaseGeneration) WHERE Status IN(N''Pending'',N''Leased'');
CREATE INDEX IX_AlertDeliveries_Incident ON [workerdock].AlertDeliveries(IncidentId,CreatedAtUtc,DeliveryId) INCLUDE(SinkRevisionId,Kind,Status,AttemptCount,NextAvailableAtUtc,SafeCode);
CREATE INDEX IX_AlertDeliveryAttempts_Timing ON [workerdock].AlertDeliveryAttempts(DeliveryId,CompletedAtUtc,AttemptOrdinal) INCLUDE(LeaseGeneration,Result,SafeCode);
CREATE INDEX IX_ArchiveBatches_State ON [workerdock].ArchiveBatches(State,EligibleThroughUtc,FinalizedAtUtc);
CREATE INDEX IX_ArchiveBatchItems_Record ON [workerdock].ArchiveBatchItems(RecordKind,RecordId,BatchId);
CREATE INDEX IX_RetentionRequestTombstones_Expiry ON [workerdock].RetentionRequestTombstones(ExpiresAtUtc);';

    -- Final immutable triggers and retention procedures.
    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].AuditEvents_AppendOnly ON [workerdock].AuditEvents AFTER UPDATE, DELETE AS
BEGIN
    IF USER_NAME() <> N''dbo''
        THROW 51001, ''Audit events are append-only'', 1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].JobRevisions_Immutable ON [workerdock].JobRevisions AFTER UPDATE, DELETE AS
BEGIN
    THROW 51003, ''Job revisions are immutable'', 1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].TemplateCatalog_Immutable ON [workerdock].TemplateCatalog AFTER UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion WHERE i.TemplateId IS NULL)
        THROW 51021,''MetadataVersionConflict'',1;
    IF EXISTS(
        SELECT 1 FROM deleted d JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion
        WHERE d.Retired=1 AND i.Retired=0
            OR (d.DescriptorJson IS NOT NULL AND (i.DescriptorJson IS NULL OR i.DescriptorJson COLLATE Latin1_General_100_BIN2<>d.DescriptorJson COLLATE Latin1_General_100_BIN2 OR i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2))
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NULL AND i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2)
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NOT NULL AND NOT (
                d.TemplateVersion COLLATE Latin1_General_100_BIN2=N''1.0.0''
                AND d.TemplateId COLLATE Latin1_General_100_BIN2 IN(N''workerdock.http'',N''workerdock.sql-server'')
                AND d.MetadataDigest COLLATE Latin1_General_100_BIN2=CONVERT(char(64),HASHBYTES(''SHA2_256'',d.TemplateId+N''|''+d.TemplateVersion),2)
            )))
        THROW 51021,''MetadataVersionConflict'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].ConnectionProfileRevisions_Immutable ON [workerdock].ConnectionProfileRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51023,''ProfileRevisionImmutable'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].IntegrationSettingsRevisions_Immutable ON [workerdock].IntegrationSettingsRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51025,''IntegrationSettingsRevisionImmutable'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].AlertSinkRevisions_Immutable ON [workerdock].AlertSinkRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51027,''AlertSinkRevisionImmutable'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].AlertRuleRevisions_Immutable ON [workerdock].AlertRuleRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51028,''AlertRuleRevisionImmutable'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].ArchiveBatchItems_Immutable ON [workerdock].ArchiveBatchItems AFTER INSERT,UPDATE,DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1 FROM inserted i
        WHERE NOT
        (
            (i.RecordKind=N''AuditEvent'' AND EXISTS(SELECT 1 FROM [workerdock].AuditEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N''Execution'' AND EXISTS(SELECT 1 FROM [workerdock].Executions r WHERE r.ExecutionId=i.RecordId)) OR
            (i.RecordKind=N''ExecutionAttempt'' AND EXISTS(SELECT 1 FROM [workerdock].Attempts r WHERE r.AttemptId=i.RecordId)) OR
            (i.RecordKind=N''ExecutionEvent'' AND EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N''AttemptStatusEvent'' AND EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N''JobPermit'' AND EXISTS(SELECT 1 FROM [workerdock].JobPermits r WHERE r.PermitId=i.RecordId)) OR
            (i.RecordKind=N''ExecutionDispatchOutbox'' AND EXISTS(SELECT 1 FROM [workerdock].Outbox r WHERE r.EventId=i.RecordId AND r.EventType=N''ExecutionDispatch'')) OR
            (i.RecordKind=N''ManualRequestDedup'' AND EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId) AND (SELECT COUNT(*) FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId)=1)
        )
    ) THROW 51044,''Archive batch item does not identify a source row'',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i
        JOIN [workerdock].ArchiveBatches b ON b.BatchId=i.BatchId
        WHERE (i.RecordKind=N''ExecutionEvent'' AND EXISTS
            (SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.EventId=i.RecordId AND r.AtUtc>b.LogCutoffUtc))
           OR (i.RecordKind=N''AttemptStatusEvent'' AND EXISTS
            (SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.EventId=i.RecordId AND r.OccurredAtUtc>b.LogCutoffUtc))
           OR (i.RecordKind=N''Execution'' AND
               (EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.ExecutionId=i.RecordId AND r.AtUtc>b.LogCutoffUtc)
                OR EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.ExecutionId=i.RecordId AND r.OccurredAtUtc>b.LogCutoffUtc)))
    ) THROW 51045,''Archive log item is newer than the batch log cutoff'',1;
    IF EXISTS(SELECT 1 FROM inserted i JOIN [workerdock].ArchiveBatches b ON b.BatchId=i.BatchId WHERE b.State<>N''Writing'')
        THROW 51034,''Archive batch items are immutable after finalization'',1;
    IF EXISTS
    (
        SELECT 1 FROM deleted d JOIN [workerdock].ArchiveBatches b ON b.BatchId=d.BatchId
        WHERE b.State<>N''Writing''
          AND NOT (b.State=N''Finalized'' AND b.ArchiveRemovedAtUtc IS NOT NULL AND USER_NAME()=N''dbo''
              AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''WDArchivePurgeBatchId''))=b.BatchId)
    ) THROW 51047,''Finalized archive items can be removed only by the archive expiry procedure'',1;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].ArchiveBatches_Immutable ON [workerdock].ArchiveBatches AFTER UPDATE,DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1 FROM deleted d
        WHERE d.State=N''Finalized''
          AND NOT EXISTS
          (
              SELECT 1 FROM inserted i
              WHERE i.BatchId=d.BatchId AND d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NOT NULL
                AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.[RowCount]=d.[RowCount]
                AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
                AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
                AND USER_NAME()=N''dbo'' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''WDArchivePurgeBatchId''))=d.BatchId
          )
          AND NOT (d.ArchiveRemovedAtUtc IS NOT NULL AND USER_NAME()=N''dbo''
              AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''WDArchivePurgeBatchId''))=d.BatchId
              AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems x WHERE x.BatchId=d.BatchId)
              AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit a WHERE a.ArchiveBatchId=d.BatchId)
              AND NOT EXISTS(SELECT 1 FROM inserted i WHERE i.BatchId=d.BatchId))
    ) THROW 51035,''Finalized archive manifests are immutable except for archive expiry cleanup'',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i JOIN deleted d ON d.BatchId=i.BatchId
        WHERE d.State=N''Finalized''
          AND NOT (d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NOT NULL
              AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.[RowCount]=d.[RowCount]
              AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
              AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
              AND USER_NAME()=N''dbo'' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''WDArchivePurgeBatchId''))=d.BatchId)
    ) THROW 51048,''Finalized manifest fields are immutable'',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i JOIN deleted d ON d.BatchId=i.BatchId
        WHERE d.ArchiveRemovedAtUtc IS NOT NULL AND (i.ArchiveRemovedAtUtc IS NULL OR i.ArchiveRemovedAtUtc<>d.ArchiveRemovedAtUtc)
    ) THROW 51046,''Archive expiry can be marked only once'',1;
    IF EXISTS(SELECT 1 FROM inserted b WHERE b.State=N''Finalized'' AND b.[RowCount]<>(SELECT COUNT_BIG(*) FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=b.BatchId))
        THROW 51036,''Archive row count must match its staged items'',1;
END;';

    EXEC sys.sp_executesql N'CREATE PROCEDURE [workerdock].PurgeAuditEvents
    @CutoffUtc datetime2(7),
    @MaxRows int,
    @ArchiveBatchId uniqueidentifier = NULL,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51030, ''Invalid audit purge bounds'', 1;

    DECLARE @ArchiveEnabled bit;
    BEGIN TRANSACTION;
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveEnabled IS NULL THROW 51043,''Retention policy is missing'',1;
    IF @ArchiveEnabled=1
    BEGIN
        IF @ArchiveBatchId IS NULL OR NOT EXISTS
        (
            SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
            WHERE BatchId=@ArchiveBatchId AND State=N''Finalized'' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CutoffUtc
        )
            THROW 51031, ''A finalized archive covering the purge cutoff is required'', 1;
    END;
    ELSE IF @ArchiveBatchId IS NOT NULL AND NOT EXISTS
    (
        SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
        WHERE BatchId=@ArchiveBatchId AND State=N''Finalized'' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CutoffUtc
    ) THROW 51039,''The supplied archive manifest does not cover the purge cutoff'',1;

    DECLARE @Purged TABLE(EventId uniqueidentifier NOT NULL PRIMARY KEY);
    IF @ArchiveEnabled=1
        DELETE TOP (@MaxRows) FROM [workerdock].AuditEvents
            OUTPUT DELETED.EventId INTO @Purged(EventId)
            WHERE OccurredAtUtc<@CutoffUtc
              AND EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@ArchiveBatchId AND i.RecordKind=N''AuditEvent'');
    ELSE
        DELETE TOP (@MaxRows) FROM [workerdock].AuditEvents
            OUTPUT DELETED.EventId INTO @Purged(EventId)
            WHERE OccurredAtUtc<@CutoffUtc;
    SELECT @PurgedRows=COUNT(*) FROM @Purged;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N''AuditEvents'',@CutoffUtc,@PurgedRows,SYSUTCDATETIME(),@ArchiveBatchId);
    COMMIT TRANSACTION;
END;';

    EXEC sys.sp_executesql N'CREATE PROCEDURE [workerdock].PurgeRetentionItems
    @LogCutoffUtc datetime2(7),
    @ExecutionCutoffUtc datetime2(7),
    @MaxRows int,
    @BatchId uniqueidentifier = NULL,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LogCutoffUtc IS NULL OR @ExecutionCutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51037,''Invalid retention purge bounds'',1;
    DECLARE @ArchiveEnabled bit,@Remaining int,@Count int,@CoverCutoff datetime2(7);
    DECLARE @Purged TABLE(RecordId uniqueidentifier NOT NULL);
    BEGIN TRANSACTION;
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveEnabled IS NULL THROW 51040,''Retention policy is missing'',1;
    IF @BatchId IS NULL AND @ArchiveEnabled=1 THROW 51041,''Archive batch required by retention policy'',1;
    IF @BatchId IS NOT NULL AND @ArchiveEnabled=0 THROW 51042,''Archive batch is not enabled by retention policy'',1;
    IF @BatchId IS NOT NULL
    BEGIN
        SET @CoverCutoff=CASE WHEN @LogCutoffUtc>@ExecutionCutoffUtc THEN @LogCutoffUtc ELSE @ExecutionCutoffUtc END;
        IF NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
            WHERE BatchId=@BatchId AND State=N''Finalized'' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CoverCutoff AND LogCutoffUtc=@LogCutoffUtc)
            THROW 51038,''A finalized archive manifest covering both retention cutoffs is required'',1;
    END
    SET @Remaining=@MaxRows;
    SET @PurgedRows=0;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].JobPermits
        OUTPUT DELETED.PermitId INTO @Purged(RecordId)
        WHERE (@BatchId IS NULL OR PermitId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''JobPermit''))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=JobPermits.ExecutionId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped''))
          AND (@BatchId IS NULL OR JobPermits.ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''Execution''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=JobPermits.ExecutionId AND a.State=N''Running'')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Outbox o WHERE o.EntityId=JobPermits.ExecutionId AND o.EventType=N''ExecutionDispatch'' AND o.ProjectionState<>N''Retired'')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),JobPermits.ExecutionId) AND i.State IN(N''Open'',N''Unknown''));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].AttemptStatusEvents
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE OccurredAtUtc<=@LogCutoffUtc
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''AttemptStatusEvent''))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=AttemptStatusEvents.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=AttemptStatusEvents.ExecutionId AND a.State=N''Running'')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=AttemptStatusEvents.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),AttemptStatusEvents.ExecutionId) AND i.State IN(N''Open'',N''Unknown''));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].ExecutionEvents
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE AtUtc<=@LogCutoffUtc
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''ExecutionEvent''))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=ExecutionEvents.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=ExecutionEvents.ExecutionId AND a.State=N''Running'')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=ExecutionEvents.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),ExecutionEvents.ExecutionId) AND i.State IN(N''Open'',N''Unknown''));
    SELECT @Count=COUNT(*) FROM @Purged;
    SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Attempts
        OUTPUT DELETED.AttemptId INTO @Purged(RecordId)
        WHERE (@BatchId IS NULL OR AttemptId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''ExecutionAttempt''))
          AND (@BatchId IS NULL OR Attempts.ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''Execution''))
          -- Abandoned attempts are retained diagnostic history; purge them only with an old, aggregated terminal execution.
          AND State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped'',N''Abandoned'')
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=Attempts.ExecutionId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Attempts.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),Attempts.ExecutionId) AND i.State IN(N''Open'',N''Unknown''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents ev WHERE ev.AttemptId=Attempts.AttemptId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents ev WHERE ev.AttemptId=Attempts.AttemptId);
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].ManualRequestDedup
        OUTPUT DELETED.CommandId INTO @Purged(RecordId)
        WHERE ExpiresAtUtc<SYSUTCDATETIME()
          AND (@BatchId IS NULL OR (CommandId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''ManualRequestDedup'') AND ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''Execution'')));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Outbox
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE EventType=N''ExecutionDispatch'' AND ProjectionState=N''Retired''
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''ExecutionDispatchOutbox''))
          AND (@BatchId IS NULL OR Outbox.EntityId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''Execution''))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=Outbox.EntityId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Outbox.EntityId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),Outbox.EntityId) AND i.State IN(N''Open'',N''Unknown''));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Executions
        OUTPUT DELETED.ExecutionId INTO @Purged(RecordId)
        WHERE CreatedAtUtc<=@ExecutionCutoffUtc AND AggregatedAtUtc IS NOT NULL AND State IN(N''Succeeded'',N''Failed'',N''Stopped'',N''TimedOut'',N''Skipped'')
          AND (@BatchId IS NULL OR ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N''Execution''))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents ev WHERE ev.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents ev WHERE ev.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup d WHERE d.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones t WHERE t.ExecutionId=Executions.ExecutionId AND t.ExpiresAtUtc>=SYSUTCDATETIME())
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Outbox o WHERE o.EntityId=Executions.ExecutionId AND o.EventType=N''ExecutionDispatch'' AND o.ProjectionState<>N''Retired'')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N''execution:''+CONVERT(nvarchar(36),Executions.ExecutionId) AND i.State IN(N''Open'',N''Unknown''));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N''RetentionItems'',@ExecutionCutoffUtc,@PurgedRows,SYSUTCDATETIME(),@BatchId);
    COMMIT TRANSACTION;
END;';

    EXEC sys.sp_executesql N'CREATE PROCEDURE [workerdock].PurgeRetentionRequestTombstones
    @CutoffUtc datetime2(7),
    @MaxRows int,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51033, ''Invalid tombstone purge bounds'', 1;
    DECLARE @Purged TABLE(JobId uniqueidentifier NOT NULL,ActorScopeHash char(64) NOT NULL,RequestKeyHash char(64) NOT NULL);
    BEGIN TRANSACTION;
    DELETE TOP (@MaxRows) FROM [workerdock].RetentionRequestTombstones
        OUTPUT DELETED.JobId,DELETED.ActorScopeHash,DELETED.RequestKeyHash INTO @Purged(JobId,ActorScopeHash,RequestKeyHash)
        WHERE ExpiresAtUtc<@CutoffUtc;
    SELECT @PurgedRows=COUNT(*) FROM @Purged;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N''RequestTombstones'',@CutoffUtc,@PurgedRows,SYSUTCDATETIME(),NULL);
    COMMIT TRANSACTION;
END;';

    EXEC sys.sp_executesql N'CREATE PROCEDURE [workerdock].PurgeExpiredArchiveBatchItems
    @BatchId uniqueidentifier,
    @FinalizedBeforeUtc datetime2(7),
    @AuditCutoffUtc datetime2(7),
    @MaxRows int,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchId IS NULL OR @FinalizedBeforeUtc IS NULL OR @AuditCutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51049,''Invalid archive metadata purge bounds'',1;
    DECLARE @ArchiveDays int,@AuditDays int,@FinalizedAtUtc datetime2(7),@RowCount bigint,@ArchiveRemovedAtUtc datetime2(7);
    DECLARE @Remaining int=@MaxRows,@Count int;
    BEGIN TRANSACTION;
    SELECT @ArchiveDays=ArchiveDays,@AuditDays=AuditDays FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveDays IS NULL OR @AuditDays IS NULL THROW 51040,''Retention policy is missing'',1;
    IF @FinalizedBeforeUtc>SYSUTCDATETIME() OR @AuditCutoffUtc>DATEADD(day,-@AuditDays,SYSUTCDATETIME())
        THROW 51050,''Archive or audit cutoff is newer than configured retention permits'',1;
    SELECT @FinalizedAtUtc=FinalizedAtUtc,@RowCount=[RowCount],@ArchiveRemovedAtUtc=ArchiveRemovedAtUtc
      FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
      WHERE BatchId=@BatchId AND State=N''Finalized'' AND FinalizedAtUtc IS NOT NULL;
    IF @FinalizedAtUtc IS NULL OR @FinalizedAtUtc>=@FinalizedBeforeUtc OR @FinalizedAtUtc>DATEADD(day,-@ArchiveDays,SYSUTCDATETIME())
        THROW 51051,''Archive batch has not reached configured file retention age'',1;
    EXEC sys.sp_set_session_context @key=N''WDArchivePurgeBatchId'',@value=@BatchId;
    IF @ArchiveRemovedAtUtc IS NULL
    BEGIN
        IF @RowCount>COALESCE((SELECT SUM(CONVERT(bigint,PurgedRowCount)) FROM [workerdock].RetentionPurgeAudit WITH(HOLDLOCK)
            WHERE ArchiveBatchId=@BatchId AND PurgeKind IN(N''AuditEvents'',N''RetentionItems'')),0)
            THROW 51052,''Archive batch purge summaries do not cover its manifest row count'',1;
        UPDATE [workerdock].ArchiveBatches SET ArchiveRemovedAtUtc=SYSUTCDATETIME() WHERE BatchId=@BatchId AND ArchiveRemovedAtUtc IS NULL;
    END;

    DELETE TOP (@Remaining) FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId;
    SET @Count=@@ROWCOUNT; SET @Remaining-=@Count; SET @PurgedRows=@Count;

    IF @Remaining>0
       AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId)
       AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId AND PurgedAtUtc>=@AuditCutoffUtc)
       AND @FinalizedAtUtc<@AuditCutoffUtc
    BEGIN
        DELETE TOP (@Remaining) FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId AND PurgedAtUtc<@AuditCutoffUtc;
        SET @Count=@@ROWCOUNT; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    END;
    IF @Remaining>0
       AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId)
       AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId)
       AND @FinalizedAtUtc<@AuditCutoffUtc
    BEGIN
        DELETE FROM [workerdock].ArchiveBatches WHERE BatchId=@BatchId AND ArchiveRemovedAtUtc IS NOT NULL;
        SET @PurgedRows+=@@ROWCOUNT;
    END;
    EXEC sys.sp_set_session_context @key=N''WDArchivePurgeBatchId'',@value=NULL;
    COMMIT TRANSACTION;
END;';

    EXEC sys.sp_executesql N'CREATE TRIGGER [workerdock].Executions_ValueStateImmutable ON [workerdock].Executions AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.ExecutionId=i.ExecutionId
        WHERE (d.ValueContextJson IS NOT NULL AND (i.ValueContextJson IS NULL OR CONVERT(varbinary(max),i.ValueContextJson)<>CONVERT(varbinary(max),d.ValueContextJson)))
           OR (d.FirstStartedAtUtc IS NOT NULL AND (i.FirstStartedAtUtc IS NULL OR i.FirstStartedAtUtc<>d.FirstStartedAtUtc))
           OR (d.ValueSnapshotJson IS NOT NULL AND (i.ValueSnapshotJson IS NULL OR CONVERT(varbinary(max),i.ValueSnapshotJson)<>CONVERT(varbinary(max),d.ValueSnapshotJson)))
           OR (d.ValueResolutionError IS NOT NULL AND (i.ValueResolutionError IS NULL OR CONVERT(varbinary(max),i.ValueResolutionError)<>CONVERT(varbinary(max),d.ValueResolutionError)))
    ) THROW 51061, ''Execution value state is immutable once populated'', 1;
END;';

    -- Required initialization data. TemplateCatalog is empty after removal of built-in 1.0.0.
    EXEC sys.sp_executesql N'INSERT INTO [workerdock].TenantSettings (InstanceId, DisplayName, DisplayTimeZone) VALUES (@InstanceId, N''WorkerDock'', N''UTC'');

INSERT INTO [workerdock].TemplatePolicies(TemplateId) VALUES (N''workerdock.sql-server''),(N''workerdock.http'');

INSERT [workerdock].PolicyRevisions(PolicyId,PolicyKind,DesiredRevision,EffectiveRevision,State,CreatedAtUtc)
VALUES(''c9188146-bcab-43ec-95ee-c4713f46f90d'',N''Capacity'',0,0,N''Pending'',SYSUTCDATETIME());

INSERT [workerdock].ProjectionMaintenance(Id,AfterJobId) VALUES(1,NULL);

INSERT [workerdock].IntegrationSettings(SettingsId,VaultRevision,ProviderId,PublicOptionsJson)
VALUES(1,1,N''workerdock.environment'',N''{}'');

INSERT [workerdock].TenantEgressPolicies(Name,Revision,HostsJson,PortsJson,PrivateCidrsJson,AllowHttp)
VALUES(N''tenant-jobs'',1,N''[]'',N''[443]'',N''[]'',0),(N''tenant-alerts'',1,N''[]'',N''[443]'',N''[]'',0);

INSERT [workerdock].IntegrationSettingsRevisions(RevisionId,SettingsKind,SettingsName,Revision,ConfigurationJson,CreatedAtUtc)
VALUES(NEWID(),N''Vault'',N''vault'',1,N''{"providerId":"workerdock.environment","publicOptions":{}}'',SYSUTCDATETIME()),
      (NEWID(),N''Egress'',N''tenant-jobs'',1,N''{"name":"tenant-jobs","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}'',SYSUTCDATETIME()),
      (NEWID(),N''Egress'',N''tenant-alerts'',1,N''{"name":"tenant-alerts","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}'',SYSUTCDATETIME());

INSERT [workerdock].ScheduleRuleScanState(ScanId,LastScheduleId) VALUES(1,NULL);

INSERT [workerdock].AlertEvaluationState(StateId,LastEvaluatedAtUtc,LastRuleId) VALUES(1,NULL,NULL);

INSERT [workerdock].RetentionPolicy(PolicyId,LogDays,ExecutionDays,AuditDays,ArchiveBeforeDelete,ArchiveDays)
VALUES(1,30,90,365,0,0);', N'@InstanceId uniqueidentifier', @InstanceId=@InstanceId;

    -- Equivalent-state migration baseline; both checksums come from the canonical manifest.
    EXEC sys.sp_executesql N'INSERT [workerdock].SchemaHistory(MigrationId,UpChecksum,DownChecksum,AppliedAtUtc) VALUES
    (N''0001_bootstrap'',''ED5D87FF51564B2FCFC4E377DDB2FAF26ABCC02AA3DD72F415B6531F0D8A50C8'',''3A35D2062BFC418D681C221EDAE3565A37CBC50F9C4255BFFE09FE01DE8296B4'',SYSUTCDATETIME()),
    (N''0002_quartz'',''8FBC51F57505D79B11E391580A00D6ABE1862F1A348D1C8C5D9E59F00108826F'',''C4588AB540694360347EED20DF1E70CE68E99108B4CAD3DAFF2A92343A94A9E6'',SYSUTCDATETIME()),
    (N''0003_execution'',''E759062EC09010CCA39E20B76292F60FD8CE8F1A302D035AD691E9EC3F833051'',''520115E07A41E001CDCD1601ADA1DD2021DC311A7A569105DC3B78511EB45A3E'',SYSUTCDATETIME()),
    (N''0003a_projection'',''840BA84755D8DA885B8AE23F301C8EB176761E5CFBA72B9C55151538362C84F8'',''29DB1BBCAA96CB76EA572E285A957CB6AC1F7F1C3C1BD638EE3C1B404C0F46B9'',SYSUTCDATETIME()),
    (N''0003b_attempts'',''2536B00ED194832C9516E6EA803F36EDE712091ED03F16DA79ACDD8037195646'',''B7BBFA27B088BA3BCB0DC281D5FA8EB893AD8449BF4CD31CDAA36233B21B9D50'',SYSUTCDATETIME()),
    (N''0003c_capacity'',''7DE90D1CD7F4A8030D7D262B3FA0F56354274F4E2ED5B051D44B36036765A3A0'',''287FB45D3B9A15AAF3BD317F97B6183E057D6630201D1E23D02AC0381B58DECD'',SYSUTCDATETIME()),
    (N''0003d_dispatch'',''026E4DF0D115A58BF50A17B60B0861C55FC7C37812EA87401DB2A80B311023E9'',''CBAC5937238150BF1C7207CB7CC8555D0A1A77D04ED6F5ADE9BD7628FCA75B32'',SYSUTCDATETIME()),
    (N''0004_templates'',''DC7835B49005E6E5E25F6A95D6D396807E8738E337AD8E0CFAEAADE837F4DC82'',''FF96B5B99F47D191E934808EF9F7C95F20EE79765C53ED372EC7BA673E947E6C'',SYSUTCDATETIME()),
    (N''0004a_profiles'',''273B482991DEC3E917B06F690C0A8BCC709DB0B94F7B44E99620CA23A16F0EB3'',''9407F612D898E034E3C9513E86BFA730CA6FB13DB2C89906DF393F9D3822770E'',SYSUTCDATETIME()),
    (N''0004b_integration_settings'',''91469EB10137B347418F41488BE03A221BAD16131FD87DF113D2A07DD38B67D5'',''B1A84221D9C15C19C9671B222AB5EFB6D213EBFE4339FEB3AD31378FC3C31859'',SYSUTCDATETIME()),
    (N''0005_scheduling'',''3E870F50CADE07CD020E12C689A8FB8263DCFD73F7ECF61F419CFDBC88E1420A'',''1502D9B78EEA227939760384A8DB7C81FA91E8E78CD0BC4FA21CFE87AAA8A0FC'',SYSUTCDATETIME()),
    (N''0006_observability'',''6CD9995EEFF27BACCE140546D3F26A46E0A8CD4D4C6650100EDD88561411414B'',''3075B7A49AFC804AC6D697E4352E8312503E71ECBE08C83913EEAC2FBC58C604'',SYSUTCDATETIME()),
    (N''0007_alerts'',''4CC89FDD9A2DDC5E9B07C6FE33C2BC6C4782E22C0D5456991B208061442EB561'',''89020F17B2B3F185E6F8EC3A8484DE556662D96B0CC4A98E8135A0B45456FE16'',SYSUTCDATETIME()),
    (N''0008_retention'',''5028AC7BABD242B8BC02CCC04034F39E98776F0B4CD9A47F15AE8F2FB9479791'',''6870B0995EA1FFD30EED25AE69C5CB4A748BE665412555E900DDEB5C61EFC4FD'',SYSUTCDATETIME()),
    (N''0009_dynamic_values'',''A2B10404556787FAEF7E54E6F883E48D735DEA3FD4B4676A37B4E79F71FB940A'',''BC4F28E9212C2EDB56CA6F57627BC49F2E6F2929484C58D054381112F7D8B0F4'',SYSUTCDATETIME()),
    (N''0010_remove_builtin_v1'',''E14C3F4FA9A97E57EACBD01DCB94FFF8A885ED1E1464297911D6C935351F8D4E'',''0329B134B864D7D059689D06F0757DB0190DBE0A0183061816CC059B454B8906'',SYSUTCDATETIME());';

    -- Effective runtime permissions, including column grants and retention DENYs.
    EXEC sys.sp_executesql N'GRANT INSERT ON [workerdock].AlertDeliveries TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertDeliveries TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].AlertDeliveries TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertDeliveryAttempts TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertDeliveryAttempts TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertEvaluationState TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertEvaluationState TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].AlertEvaluationState TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertIncidents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertIncidents TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].AlertIncidents TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertRuleRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertRuleRevisions TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertRules TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertRules TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].AlertRules TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertSinkRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertSinkRevisions TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AlertSinks TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AlertSinks TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].AlertSinks TO WorkerDockRuntime;
DENY DELETE ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime;
DENY DELETE ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AttemptStatusEvents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AttemptStatusEvents TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Attempts TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Attempts TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Attempts TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AuditEvents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].AuditEvents TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Commands TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Commands TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Commands TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ConnectionProfileRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ConnectionProfileRevisions TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
GRANT UPDATE(Name,CurrentRevisionId,Retired) ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ExecutionEvents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ExecutionEvents TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ExecutionMetricAggregates TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ExecutionMetricAggregates TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].ExecutionMetricAggregates TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Executions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Executions TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Executions TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].IntegrationSettings TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].IntegrationSettings TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].IntegrationSettings TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].IntegrationSettingsRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].IntegrationSettingsRevisions TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].JobPermits TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].JobPermits TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].JobPermits TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Jobs TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Jobs TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Jobs TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Outbox TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Outbox TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Outbox TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].PolicyRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].PolicyRevisions TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].PolicyRevisions TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ProjectionMaintenance TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].ProjectionMaintenance TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeAuditEvents TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeExpiredArchiveBatchItems TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeRetentionItems TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeRetentionRequestTombstones TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].RetentionPolicy TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].RetentionPolicy TO WorkerDockRuntime;
DENY DELETE ON [workerdock].RetentionPurgeAudit TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].RetentionPurgeAudit TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].RetentionRequestTombstones TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].RetentionRequestTombstones TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].RetentionRequestTombstones TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].ScheduleRecoveryEvents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ScheduleRecoveryEvents TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].ScheduleRuleScanState TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].ScheduleRuleScanState TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].Schedules TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Schedules TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Schedules TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Schedules TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].SchemaHistory TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT UPDATE (DescriptorJson,MetadataDigest,Enabled,Retired) ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].TemplatePolicies TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].TemplatePolicies TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].TemplatePolicies TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].TenantSettings TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].TenantSettings TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDE_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDE_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDE_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDE_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_BLOB_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_CALENDARS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_CRON_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_EXECUTION_HISTORY TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_FIRED_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_JOB_DETAILS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_JOB_STATUS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_LOCKS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_MISFIRE_HISTORY TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_PAUSED_JOB_GRPS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_PAUSED_TRIGGER_GRPS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_SCHEDULER_STATE TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_SIMPLE_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_SIMPROP_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WDS_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WDS_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WDS_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].WDS_QRTZ_TRIGGERS TO WorkerDockRuntime;
GRANT DELETE ON [workerdock].WorkerCapabilities TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].WorkerCapabilities TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].WorkerCapabilities TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].Workers TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].Workers TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].Workers TO WorkerDockRuntime;';

    -- Optional operator-supplied binding belongs here, inside this deployment transaction:
    -- CREATE USER [chosen_database_user] FOR LOGIN [chosen_sql_login];
    -- ALTER ROLE WorkerDockRuntime ADD MEMBER [chosen_database_user];

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
