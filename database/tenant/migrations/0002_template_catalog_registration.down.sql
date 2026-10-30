CREATE OR ALTER TRIGGER [workerdock].TemplateCatalog_Immutable ON [workerdock].TemplateCatalog AFTER UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion WHERE i.TemplateId IS NULL)
        THROW 51021,'MetadataVersionConflict',1;
    IF EXISTS(
        SELECT 1 FROM deleted d JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion
        WHERE d.Retired=1 AND i.Retired=0
            OR (d.DescriptorJson IS NOT NULL AND (i.DescriptorJson IS NULL OR i.DescriptorJson COLLATE Latin1_General_100_BIN2<>d.DescriptorJson COLLATE Latin1_General_100_BIN2 OR i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2))
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NULL AND i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2)
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NOT NULL AND NOT (
                d.TemplateVersion COLLATE Latin1_General_100_BIN2=N'1.0.0'
                AND d.TemplateId COLLATE Latin1_General_100_BIN2 IN(N'workerdock.http',N'workerdock.sql-server')
                AND d.MetadataDigest COLLATE Latin1_General_100_BIN2=CONVERT(char(64),HASHBYTES('SHA2_256',d.TemplateId+N'|'+d.TemplateVersion),2)
            )))
        THROW 51021,'MetadataVersionConflict',1;
END;
