UPDATE "ProcessingRuns" SET "Status" = 'Queued', "ErrorCode" = NULL, "ErrorMessage" = NULL, "CompletedAt" = NULL, "LeaseOwner" = NULL, "LeaseUntil" = NULL, "NextAttemptAt" = NULL,
       "MaxAttempts" = "AttemptCount" + 1,
       "DeadlineAt" = now() + interval '5 hours', "EffectiveDeadlineAt" = now() + interval '5 hours', "HardDeadlineAt" = now() + interval '5 hours'
WHERE "Id" = @r AND "Status" = 'Failed'