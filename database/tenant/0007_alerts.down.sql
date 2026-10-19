IF EXISTS(SELECT 1 FROM [workerdock].AlertSinks)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertSinkRevisions)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertRules)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertRuleRevisions)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertIncidents)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertDeliveries)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertDeliveryAttempts)
    OR EXISTS(SELECT 1 FROM [workerdock].AlertEvaluationState WHERE LastEvaluatedAtUtc IS NOT NULL OR LastRuleId IS NOT NULL OR LastSubjectKey IS NOT NULL)
    THROW 51029,'Cannot remove populated alert records',1;
GO
REVOKE SELECT,INSERT,UPDATE ON [workerdock].AlertEvaluationState FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].AlertDeliveryAttempts FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].AlertDeliveries FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].AlertIncidents FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].AlertRuleRevisions FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].AlertRules FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].AlertSinkRevisions FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].AlertSinks FROM WorkerDockRuntime;
DROP TABLE [workerdock].AlertEvaluationState;
DROP TABLE [workerdock].AlertDeliveryAttempts;
DROP TABLE [workerdock].AlertDeliveries;
DROP TABLE [workerdock].AlertIncidents;
DROP TABLE [workerdock].AlertRuleRevisions;
DROP TABLE [workerdock].AlertRules;
DROP TABLE [workerdock].AlertSinkRevisions;
DROP TABLE [workerdock].AlertSinks;
