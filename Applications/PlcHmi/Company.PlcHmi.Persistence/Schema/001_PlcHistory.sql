-- 001_PlcHistory.sql
-- 经人工备份和结构检查后显式执行；HMI不会自动执行。
-- 同名表已存在时应先核对结构；禁止覆盖原表或删表重建。
-- 时间字段由应用写入UTC；ProductionDate为PLC给出的生产日期。
-- 不修改或删除旧AlarmInfo、Member等表；不包含任何业务数据或账号初始化。
USE CRT_Data;

CREATE TABLE plc_alarm_event (
 EventId CHAR(36) NOT NULL,
 DeviceId VARCHAR(64) NOT NULL,
 AlarmKey VARCHAR(128) NOT NULL,
 AlarmIndex INT UNSIGNED NOT NULL,
 AlarmCode VARCHAR(128) NULL,
 Message TEXT NULL,
 Severity VARCHAR(64) NULL,
 Component VARCHAR(128) NULL,
 StartObservedUtc DATETIME(3) NOT NULL,
 EndObservedUtc DATETIME(3) NULL,
 StartKind VARCHAR(32) NOT NULL,
 EndKind VARCHAR(32) NULL,
 HasGap TINYINT(1) NOT NULL DEFAULT 0,
 LastValidUtc DATETIME(3) NULL,
 Status VARCHAR(32) NOT NULL,
 ConfigurationVersion VARCHAR(160) NOT NULL,
 BindingFingerprint CHAR(64) NOT NULL,
 AcknowledgedBy VARCHAR(128) NULL,
 AcknowledgedUtc DATETIME(3) NULL,
 CreatedUtc DATETIME(3) NOT NULL,
 UpdatedUtc DATETIME(3) NOT NULL,
 PRIMARY KEY (EventId),
 KEY ix_alarm_device_start (DeviceId,StartObservedUtc,EventId),
 KEY ix_alarm_key_start (DeviceId,AlarmKey,StartObservedUtc),
 KEY ix_alarm_end (EndObservedUtc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_alarm_segment (
 SegmentId CHAR(36) NOT NULL,
 EventId CHAR(36) NOT NULL,
 StartUtc DATETIME(3) NOT NULL,
 EndUtc DATETIME(3) NULL,
 LastValidUtc DATETIME(3) NOT NULL,
 CloseReason VARCHAR(64) NULL,
 PRIMARY KEY (SegmentId),
 KEY ix_segment_event_start (EventId,StartUtc),
 KEY ix_segment_start (StartUtc),
 CONSTRAINT fk_segment_alarm FOREIGN KEY (EventId) REFERENCES plc_alarm_event(EventId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_alarm_state (
 DeviceId VARCHAR(64) NOT NULL,
 AlarmKey VARCHAR(128) NOT NULL,
 OpenEventId CHAR(36) NULL,
 CurrentActive TINYINT(1) NULL,
 LastKnownActive TINYINT(1) NULL,
 State VARCHAR(32) NOT NULL,
 LastValidUtc DATETIME(3) NULL,
 LastSampleUtc DATETIME(3) NULL,
 LastGapUtc DATETIME(3) NULL,
 RunId CHAR(36) NOT NULL,
 ConnectionEpoch BIGINT NOT NULL,
 BindingFingerprint CHAR(64) NOT NULL,
 UpdatedUtc DATETIME(3) NOT NULL,
 PRIMARY KEY (DeviceId,AlarmKey),
 KEY ix_state_open (OpenEventId),
 CONSTRAINT fk_state_alarm FOREIGN KEY (OpenEventId) REFERENCES plc_alarm_event(EventId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_monitoring_gap (
 GapId CHAR(36) NOT NULL,
 DeviceId VARCHAR(64) NOT NULL,
 AcquisitionGroup VARCHAR(64) NOT NULL,
 StartUtc DATETIME(3) NOT NULL,
 DetectedUtc DATETIME(3) NOT NULL,
 RecoveredUtc DATETIME(3) NULL,
 Reason TEXT NOT NULL,
 ConfigurationVersion VARCHAR(160) NOT NULL,
 PRIMARY KEY (GapId),
 KEY ix_gap_device_group_start (DeviceId,AcquisitionGroup,StartUtc),
 KEY ix_gap_recovery (RecoveredUtc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_production_hour (
 Id BIGINT NOT NULL AUTO_INCREMENT,
 DeviceId VARCHAR(64) NOT NULL,
 ProductionDate DATE NOT NULL,
 ShiftCode VARCHAR(16) NOT NULL,
 PeriodStartUtc DATETIME(3) NOT NULL,
 PeriodEndUtc DATETIME(3) NOT NULL,
 CounterIndex SMALLINT UNSIGNED NOT NULL,
 Quantity BIGINT UNSIGNED NOT NULL,
 SourceId VARCHAR(64) NOT NULL,
 SourceEpoch VARCHAR(64) NOT NULL,
 SourceRevision BIGINT UNSIGNED NOT NULL,
 ContentHash CHAR(64) NOT NULL,
 FirstSyncedUtc DATETIME(3) NOT NULL,
 LastSyncedUtc DATETIME(3) NOT NULL,
 PRIMARY KEY (Id),
 UNIQUE KEY ux_production_hour (DeviceId,PeriodStartUtc,CounterIndex),
 KEY ix_production_day_shift (DeviceId,ProductionDate,ShiftCode,CounterIndex)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_production_sync_state (
 DeviceId VARCHAR(64) NOT NULL,
 SourceId VARCHAR(64) NOT NULL,
 LastAttemptUtc DATETIME(3) NOT NULL,
 LastSuccessUtc DATETIME(3) NULL,
 AvailableFromUtc DATETIME(3) NULL,
 AvailableToUtc DATETIME(3) NULL,
 Added INT NOT NULL,
 Updated INT NOT NULL,
 Unchanged INT NOT NULL,
 Rejected INT NOT NULL,
 Status VARCHAR(32) NOT NULL,
 Error TEXT NULL,
 PRIMARY KEY (DeviceId,SourceId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE plc_persistence_checkpoint (
 StreamId CHAR(36) NOT NULL,
 LastCommittedSequence BIGINT UNSIGNED NOT NULL,
 UpdatedUtc DATETIME(3) NOT NULL,
 PRIMARY KEY (StreamId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
