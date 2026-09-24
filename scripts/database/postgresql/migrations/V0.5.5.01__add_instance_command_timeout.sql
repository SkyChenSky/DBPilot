-- V0.5.5.01：dbpilot_instance 增实例查询命令超时列（默认 30 秒；增量迁移，存量库补列）
ALTER TABLE dbpilot_instance ADD COLUMN IF NOT EXISTS command_timeout_seconds INTEGER NOT NULL DEFAULT 30
