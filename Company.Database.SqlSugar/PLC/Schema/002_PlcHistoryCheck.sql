-- 只读检查；不会创建、修改或删除数据库对象或数据。
SELECT DATABASE() AS CurrentDatabase, VERSION() AS ServerVersion;
SELECT TABLE_NAME, ENGINE, TABLE_COLLATION
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN
('plc_alarm_event','plc_alarm_segment','plc_alarm_state','plc_monitoring_gap',
 'plc_production_hour','plc_production_sync_state','plc_persistence_checkpoint')
ORDER BY TABLE_NAME;
SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN
('plc_alarm_event','plc_alarm_segment','plc_alarm_state','plc_monitoring_gap',
 'plc_production_hour','plc_production_sync_state','plc_persistence_checkpoint')
ORDER BY TABLE_NAME, ORDINAL_POSITION;
SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME
FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN
('plc_alarm_event','plc_alarm_segment','plc_alarm_state','plc_monitoring_gap',
 'plc_production_hour','plc_production_sync_state','plc_persistence_checkpoint')
ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX;
SELECT TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
FROM information_schema.KEY_COLUMN_USAGE
WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
AND TABLE_NAME IN ('plc_alarm_segment','plc_alarm_state');
