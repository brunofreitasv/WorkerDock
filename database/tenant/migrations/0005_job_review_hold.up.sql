ALTER TABLE [workerdock].Jobs ADD PauseOnNeedsReview bit NOT NULL CONSTRAINT DF_Jobs_PauseOnNeedsReview DEFAULT(0);
EXEC sys.sp_executesql N'UPDATE [workerdock].Jobs SET PauseOnNeedsReview=1;';
DECLARE @ReviewGrant nvarchar(max)=N'GRANT UPDATE (PauseOnNeedsReview) ON [workerdock].Jobs TO '+QUOTENAME(@RuntimePrincipal)+N';';
EXEC sys.sp_executesql @ReviewGrant;
