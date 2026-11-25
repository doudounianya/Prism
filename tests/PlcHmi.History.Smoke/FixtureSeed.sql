INSERT INTO plc_alarm_event
    (EventId, DeviceId, AlarmKey, AlarmIndex, Message, Severity, StartObservedUtc, EndObservedUtc,
     StartKind, EndKind, HasGap, LastValidUtc, Status, ConfigurationVersion, BindingFingerprint, CreatedUtc, UpdatedUtc)
VALUES
    ('00000000-0000-4000-8000-000000000051', 'PLC01', 'Alarm[51]', 51, 'Synthetic release alarm', '警告',
     '2000-01-03 00:10:00', '2000-01-03 00:11:00', 'ObservedRise', 'ObservedFall', 0,
     '2000-01-03 00:11:00', 'Closed', 'fixture', REPEAT('a', 64), '2000-01-03 00:10:00', '2000-01-03 00:11:00');

INSERT INTO plc_alarm_segment (SegmentId, EventId, StartUtc, EndUtc, LastValidUtc, CloseReason)
VALUES ('00000000-0000-4000-8000-000000000052', '00000000-0000-4000-8000-000000000051',
        '2000-01-03 00:10:00', '2000-01-03 00:11:00', '2000-01-03 00:11:00', 'ObservedFall');

-- Beijing production day 2000-01-03. The second interval begins at 20:00 Beijing time.
INSERT INTO plc_production_hour
    (DeviceId, ProductionDate, ShiftCode, PeriodStartUtc, PeriodEndUtc, CounterIndex, Quantity,
     SourceId, SourceEpoch, SourceRevision, ContentHash, FirstSyncedUtc, LastSyncedUtc)
VALUES
    ('PLC01', '2000-01-03', 'Day', '2000-01-03 00:00:00', '2000-01-03 01:00:00', 0, 41,
     'CapacityDay0.Observed', 'fixture', 1, REPEAT('b', 64), '2000-01-04 01:00:00', '2000-01-04 01:00:00'),
    ('PLC01', '2000-01-03', 'Night', '2000-01-03 12:00:00', '2000-01-03 13:00:00', 0, 17,
     'CapacityDay0.Observed', 'fixture', 1, REPEAT('c', 64), '2000-01-04 01:00:00', '2000-01-04 01:00:00');
