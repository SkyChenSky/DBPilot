-- V0.5.5.01：dbpilot_instance 增实例查询命令超时列（默认 30 秒；增量迁移，存量库补列）
-- MySQL 无 ADD COLUMN IF NOT EXISTS：重复列错误(1060)由迁移执行器按已应用处理
ALTER TABLE dbpilot_instance ADD COLUMN command_timeout_seconds INT NOT NULL DEFAULT 30
