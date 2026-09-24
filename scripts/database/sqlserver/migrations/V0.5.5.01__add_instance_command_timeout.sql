-- V0.5.5.01：dbpilot_instance 增实例查询命令超时列（默认 30 秒；增量迁移，存量库补列）
-- 约定：一个迁移文件 = 一条批次（整文件单次 ExecuteNonQuery）；幂等守卫用 IF NOT EXISTS
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbpilot_instance') AND name = N'command_timeout_seconds')
    ALTER TABLE dbpilot_instance ADD command_timeout_seconds INT NOT NULL CONSTRAINT df_ins_cmdto DEFAULT (30)
