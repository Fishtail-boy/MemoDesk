using MemoDesk.Core;
using MemoDesk.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace MemoDesk.Infrastructure.Storage;

/// <summary>提醒计划与实例的 SQLite 实现；投递领取、恢复次数与动作校验都在事务中完成。</summary>
public sealed class SqliteReminderRepository : IReminderRepository
{
    public IReadOnlyList<ReminderPlan> GetWakeablePlans()
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectReminders + """
             WHERE enabled = 1 AND IFNULL(delivery_owner, 'app') = 'app'
             AND EXISTS (SELECT 1 FROM items i WHERE i.id = reminders.item_id AND i.status = 'Open')
             AND NOT EXISTS (SELECT 1 FROM reminder_occurrences o
                 WHERE o.reminder_id = reminders.id AND o.plan_version = reminders.plan_version
                 AND (o.status IN ('Requested', 'Failed', 'Uncertain')
                     OR (o.status = 'Pending' AND o.delivery_attempts > 0)))
             ORDER BY reminder_at_utc;
            """;
        using var reader = command.ExecuteReader();
        var plans = new List<ReminderPlan>();
        while (reader.Read()) plans.Add(MapReminder(reader));
        return plans;
    }
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteReminderRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public IReadOnlyList<ReminderPlan> GetActiveDue(DateTimeOffset atUtc)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectReminders + " WHERE enabled = 1 AND reminder_at_utc <= $at ORDER BY reminder_at_utc ASC;";
        command.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(atUtc));

        var result = new List<ReminderPlan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapReminder(reader));
        }

        return result;
    }

    public ReminderPlan? GetReminder(Guid reminderId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectReminders + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", reminderId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReminder(reader) : null;
    }

    public IReadOnlyList<ReminderPlan> GetActiveBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectReminders
            + " WHERE enabled = 1 AND reminder_at_utc >= $from AND reminder_at_utc <= $to ORDER BY reminder_at_utc ASC;";
        command.Parameters.AddWithValue("$from", SqliteMapping.ToUtcText(fromUtc));
        command.Parameters.AddWithValue("$to", SqliteMapping.ToUtcText(toUtc));

        var result = new List<ReminderPlan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapReminder(reader));
        }

        return result;
    }

    public ReminderPlan? GetActiveForItem(Guid itemId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectReminders + " WHERE item_id = $itemId AND enabled = 1 LIMIT 1;";
        command.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReminder(reader) : null;
    }

    public ReminderOccurrence? GetOccurrence(Guid occurrenceId)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectOccurrences + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOccurrence(reader) : null;
    }

    public ReminderOccurrence? GetBlockingOccurrenceForPlan(Guid reminderId, int planVersion)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectOccurrences
            + """
             WHERE reminder_id = $reminderId
               AND plan_version = $planVersion
               AND status IN ('Pending', 'Requested', 'Uncertain', 'Failed')
             ORDER BY created_at_utc ASC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$planVersion", planVersion);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapOccurrence(reader) : null;
    }

    public IReadOnlyList<ReminderOccurrence> GetPendingDue(DateTimeOffset atUtc)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectOccurrences + " WHERE status = 'Pending' AND scheduled_at_utc <= $at ORDER BY scheduled_at_utc ASC;";
        command.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(atUtc));

        var result = new List<ReminderOccurrence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapOccurrence(reader));
        }

        return result;
    }

    public IReadOnlyList<ReminderOccurrence> GetRecoverable(DateTimeOffset atUtc)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = SelectOccurrences + """
             WHERE scheduled_at_utc <= $at
               AND (delivery_lease_until_utc IS NULL OR delivery_lease_until_utc <= $at)
               AND (
                    (status = 'Pending' AND delivery_attempts >= 1 AND recovery_attempts < 1)
                    OR (status = 'Uncertain' AND recovery_attempts < 1)
               )
             ORDER BY scheduled_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(atUtc));

        var result = new List<ReminderOccurrence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapOccurrence(reader));
        }

        return result;
    }

    public bool TryAddOccurrence(ReminderOccurrence occurrence)
    {
        try
        {
            using var connection = _connectionFactory.Create();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reminder_occurrences (
                    id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                    options_snapshot_json, requested_at_utc, action, action_at_utc,
                    attempt_count, last_error, created_at_utc, updated_at_utc,
                    handed_off_at_utc, channel, plan_version, delivery_attempts,
                    recovery_attempts, delivery_lease_until_utc, delivery_owner, cancel_reason,
                    manual_attempts)
                VALUES (
                    $id, $reminderId, $itemId, $scheduledAt, $status, $tag,
                    $options, $requestedAt, $action, $actionAt,
                    $attempts, $lastError, $createdAt, $updatedAt,
                    $handedOffAt, $channel, $planVersion, $deliveryAttempts,
                    $recoveryAttempts, $leaseUntil, $owner, $cancelReason,
                    $manualAttempts);
                """;
            command.Parameters.AddWithValue("$id", occurrence.Id.ToString("N"));
            command.Parameters.AddWithValue("$reminderId", occurrence.ReminderId.ToString("N"));
            command.Parameters.AddWithValue("$itemId", occurrence.ItemId.ToString("N"));
            command.Parameters.AddWithValue("$scheduledAt", SqliteMapping.ToUtcText(occurrence.ScheduledAtUtc));
            command.Parameters.AddWithValue("$status", occurrence.Status.ToString());
            command.Parameters.AddWithValue("$tag", occurrence.NotificationTag);
            command.Parameters.AddWithValue("$options", JsonDefaults.Serialize(occurrence.OptionsSnapshot));
            command.Parameters.AddWithValue("$requestedAt", SqliteMapping.ToDb(occurrence.RequestedAtUtc));
            command.Parameters.AddWithValue("$action", (object?)occurrence.Action ?? DBNull.Value);
            command.Parameters.AddWithValue("$actionAt", SqliteMapping.ToDb(occurrence.ActionAtUtc));
            command.Parameters.AddWithValue("$attempts", occurrence.AttemptCount);
            command.Parameters.AddWithValue("$lastError", (object?)occurrence.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", SqliteMapping.ToUtcText(occurrence.CreatedAtUtc));
            command.Parameters.AddWithValue("$updatedAt", SqliteMapping.ToUtcText(occurrence.UpdatedAtUtc));
            command.Parameters.AddWithValue("$handedOffAt", SqliteMapping.ToDb(occurrence.HandedOffAtUtc));
            command.Parameters.AddWithValue("$channel", (object?)occurrence.Channel ?? DBNull.Value);
            command.Parameters.AddWithValue("$planVersion", occurrence.PlanVersion);
            command.Parameters.AddWithValue("$deliveryAttempts", occurrence.DeliveryAttempts);
            command.Parameters.AddWithValue("$recoveryAttempts", occurrence.RecoveryAttempts);
            command.Parameters.AddWithValue("$leaseUntil", SqliteMapping.ToDb(occurrence.DeliveryLeaseUntilUtc));
            command.Parameters.AddWithValue("$owner", (object?)occurrence.DeliveryOwner ?? DBNull.Value);
            command.Parameters.AddWithValue("$cancelReason", (object?)occurrence.CancelReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$manualAttempts", occurrence.ManualAttempts);
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 同计划版本已有正版本实例（唯一索引）。
            return false;
        }
    }

    public bool TryClaimDelivery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)
        => TryClaimDeliveryCore(occurrenceId, owner, now, leaseDuration, elapsed: false);

    public bool TryClaimElapsedDelivery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)
        => TryClaimDeliveryCore(occurrenceId, owner, now, leaseDuration, elapsed: true);

    private bool TryClaimDeliveryCore(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration, bool elapsed)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET delivery_owner = $owner,
                delivery_lease_until_utc = $leaseUntil,
                delivery_attempts = delivery_attempts + 1,
                updated_at_utc = $now
            WHERE id = $id
              AND status = 'Pending'
              AND delivery_attempts = 0
              AND (scheduled_at_utc <= $now OR ($elapsed = 1 AND EXISTS (
                  SELECT 1 FROM reminders r JOIN items i ON i.id = r.item_id
                  WHERE r.id = reminder_occurrences.reminder_id AND r.enabled = 1
                    AND r.plan_version = reminder_occurrences.plan_version
                    AND r.source = 'LocalExplicit' AND r.relative_seconds BETWEEN 1 AND 300
                    AND (r.delivery_owner IS NULL OR r.delivery_owner = 'app') AND i.status = 'Open')))
              AND (delivery_lease_until_utc IS NULL OR delivery_lease_until_utc <= $now);
            """;
        AddClaimParameters(command, occurrenceId, owner, now, leaseDuration);
        command.Parameters.AddWithValue("$elapsed", elapsed ? 1 : 0);
        return command.ExecuteNonQuery() == 1;
    }

    public bool TryClaimRecovery(Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET status = 'Uncertain',
                recovery_attempts = recovery_attempts + 1,
                delivery_owner = $owner,
                delivery_lease_until_utc = $leaseUntil,
                updated_at_utc = $now
            WHERE id = $id
              AND recovery_attempts < 1
              AND (delivery_lease_until_utc IS NULL OR delivery_lease_until_utc <= $now)
              AND (
                   status = 'Uncertain'
                   OR (status = 'Pending' AND delivery_attempts >= 1)
              );
            """;
        AddClaimParameters(command, occurrenceId, owner, now, leaseDuration);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkHandedOff(Guid occurrenceId, string owner, string channel, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET status = 'Requested',
                handed_off_at_utc = CASE WHEN handed_off_at_utc IS NULL THEN $now ELSE handed_off_at_utc END,
                channel = $channel,
                requested_at_utc = CASE WHEN requested_at_utc IS NULL THEN $now ELSE requested_at_utc END,
                delivery_owner = NULL,
                delivery_lease_until_utc = NULL,
                updated_at_utc = $now
            WHERE id = $id
              AND delivery_owner = $owner
              AND status IN ('Pending', 'Uncertain');
            """;
        AddOwnerParameters(command, occurrenceId, owner, now);
        command.Parameters.AddWithValue("$channel", channel);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkDeliveryFailed(Guid occurrenceId, string owner, string? error, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET status = 'Failed',
                last_error = $error,
                delivery_owner = NULL,
                delivery_lease_until_utc = NULL,
                updated_at_utc = $now
            WHERE id = $id
              AND delivery_owner = $owner
              AND status IN ('Pending', 'Uncertain');
            """;
        AddOwnerParameters(command, occurrenceId, owner, now);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkDeliveryUncertain(Guid occurrenceId, string owner, string? error, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET status = 'Uncertain',
                last_error = $error,
                delivery_owner = NULL,
                delivery_lease_until_utc = NULL,
                updated_at_utc = $now
            WHERE id = $id
              AND delivery_owner = $owner
              AND status IN ('Pending', 'Uncertain');
            """;
        AddOwnerParameters(command, occurrenceId, owner, now);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkOccurrenceStatus(
        Guid occurrenceId,
        ReminderOccurrenceStatus status,
        string? action,
        DateTimeOffset atUtc)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminder_occurrences
            SET status = $status,
                action = $action,
                action_at_utc = $at,
                delivery_owner = NULL,
                delivery_lease_until_utc = NULL,
                updated_at_utc = $at
            WHERE id = $id AND status IN ('Pending', 'Requested', 'Uncertain');
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$action", (object?)action ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(atUtc));
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
        return command.ExecuteNonQuery() > 0;
    }

    public void SetReminder(Guid itemId, DateTimeOffset? reminderAtUtc, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();
        SetReminderCore(connection, transaction, itemId, reminderAtUtc, now);
        transaction.Commit();
    }

    internal static void SetReminderCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        DateTimeOffset? reminderAtUtc,
        DateTimeOffset now)
    {
        // 未处理实例全部作废：旧按钮/旧动作不得阻塞或覆盖新计划。
        using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Cancelled',
                    cancel_reason = 'reminder-changed',
                    delivery_owner = NULL,
                    delivery_lease_until_utc = NULL,
                    updated_at_utc = $now
                WHERE item_id = $itemId AND status IN ('Pending', 'Requested', 'Uncertain');
                """;
            cancel.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            cancel.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cancel.ExecuteNonQuery();
        }

        string? activeReminderId = null;
        int activePlanVersion = 0;
        string? activeTag = null;
        string? activeScheduleState = null;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, plan_version, scheduled_tag, schedule_state
                FROM reminders
                WHERE item_id = $itemId AND enabled = 1
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                activeReminderId = reader.GetString(0);
                activePlanVersion = reader.GetInt32(1);
                activeTag = reader.IsDBNull(2) ? null : reader.GetString(2);
                activeScheduleState = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        if (activeReminderId is not null
            && !string.IsNullOrWhiteSpace(activeTag)
            && activeScheduleState is "scheduled" or "registering")
        {
            InsertCancellationTask(
                connection,
                transaction,
                Guid.Parse(activeReminderId),
                activePlanVersion,
                activeTag!,
                reminderAtUtc is null ? "reminder-cancelled" : "reminder-changed",
                now);
        }

        if (reminderAtUtc is null)
        {
            using var disable = connection.CreateCommand();
            disable.Transaction = transaction;
            disable.CommandText = """
                UPDATE reminders
                SET enabled = 0,
                    plan_version = plan_version + 1,
                    delivery_owner = NULL,
                    scheduled_tag = NULL,
                    scheduled_at_utc = NULL,
                    schedule_state = NULL,
                    schedule_plan_version = NULL,
                    schedule_error = NULL,
                    schedule_updated_at = $now,
                    updated_at_utc = $now
                WHERE item_id = $itemId AND enabled = 1;
                """;
            disable.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            disable.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            disable.ExecuteNonQuery();
        }
        else if (activeReminderId is not null)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reminders
                SET reminder_at_utc = $at,
                    source = 'Manual', relative_seconds = NULL, captured_at_utc = NULL, source_time_text = NULL,
                    plan_version = plan_version + 1,
                    delivery_owner = NULL,
                    scheduled_tag = NULL,
                    scheduled_at_utc = NULL,
                    schedule_state = NULL,
                    schedule_plan_version = NULL,
                    schedule_error = NULL,
                    schedule_updated_at = $now,
                    updated_at_utc = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(reminderAtUtc.Value));
            update.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            update.Parameters.AddWithValue("$id", activeReminderId);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reminders (id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc, source)
                VALUES ($id, $itemId, $at, 1, 1, $now, $now, 'Manual');
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            insert.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(reminderAtUtc.Value));
            insert.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            insert.ExecuteNonQuery();
        }

    }

    public ManualRetryOutcome TryResetOccurrenceForManualDelivery(Guid occurrenceId, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string statusText;
        int deliveryAttempts;
        int recoveryAttempts;
        int planVersion;
        string reminderId;
        string? leaseText;

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT status, delivery_attempts, recovery_attempts, plan_version,
                       reminder_id, delivery_lease_until_utc
                FROM reminder_occurrences
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return ManualRetryOutcome.NotEligible;
            }

            statusText = reader.GetString(0);
            deliveryAttempts = reader.GetInt32(1);
            recoveryAttempts = reader.GetInt32(2);
            planVersion = reader.GetInt32(3);
            reminderId = reader.GetString(4);
            leaseText = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        var status = Enum.Parse<ReminderOccurrenceStatus>(statusText, ignoreCase: false);
        var leaseFree = leaseText is null
            || DateTimeOffset.Parse(leaseText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind) <= now;

        var eligible = status == ReminderOccurrenceStatus.Failed
            || status == ReminderOccurrenceStatus.Uncertain
            || (status == ReminderOccurrenceStatus.Pending
                && deliveryAttempts >= 1
                && recoveryAttempts >= 1);

        if (!eligible)
        {
            return ManualRetryOutcome.NotEligible;
        }

        if (!leaseFree)
        {
            return ManualRetryOutcome.InFlight;
        }

        using (var plan = connection.CreateCommand())
        {
            plan.Transaction = transaction;
            plan.CommandText = """
                SELECT r.enabled, r.plan_version, i.status, i.type
                FROM reminders r
                JOIN items i ON i.id = r.item_id
                WHERE r.id = $reminderId;
                """;
            plan.Parameters.AddWithValue("$reminderId", reminderId);
            using var reader = plan.ExecuteReader();
            if (!reader.Read())
            {
                return ManualRetryOutcome.PlanChanged;
            }

            if (reader.GetInt32(0) != 1 || reader.GetInt32(1) != planVersion)
            {
                return ManualRetryOutcome.PlanChanged;
            }

            if (reader.GetString(2) != "Open"
                || reader.GetString(3) is not ("Todo" or "Memo"))
            {
                return ManualRetryOutcome.ItemNotOpen;
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Pending',
                    scheduled_at_utc = $now,
                    delivery_attempts = 0,
                    recovery_attempts = MAX(recovery_attempts, 1),
                    manual_attempts = manual_attempts + 1,
                    delivery_owner = NULL,
                    delivery_lease_until_utc = NULL,
                    action = 'manual-retry',
                    action_at_utc = $now,
                    updated_at_utc = $now
                WHERE id = $id
                  AND (
                       status = 'Failed'
                       OR status = 'Uncertain'
                       OR (status = 'Pending' AND delivery_attempts >= 1 AND recovery_attempts >= 1)
                  )
                  AND (delivery_lease_until_utc IS NULL OR delivery_lease_until_utc <= $now)
                  AND reminder_id = $reminderId
                  AND plan_version = $planVersion
                  AND EXISTS (
                      SELECT 1
                      FROM reminders r
                      JOIN items i ON i.id = r.item_id
                      WHERE r.id = reminder_occurrences.reminder_id
                        AND r.enabled = 1
                        AND r.plan_version = reminder_occurrences.plan_version
                        AND i.status = 'Open'
                        AND i.type IN ('Todo', 'Memo')
                  );
                """;
            update.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            update.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
            update.Parameters.AddWithValue("$reminderId", reminderId);
            update.Parameters.AddWithValue("$planVersion", planVersion);

            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return ManualRetryOutcome.NotEligible;
            }
        }

        transaction.Commit();
        return ManualRetryOutcome.Reset;
    }

    public Guid? BeginScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        string notificationTag,
        IReadOnlyList<SnoozeOption> optionsSnapshot,
        DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string itemId;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT item_id, enabled, plan_version, reminder_at_utc FROM reminders WHERE id = $id;";
            read.Parameters.AddWithValue("$id", reminderId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            itemId = reader.GetString(0);
            var enabled = reader.GetInt32(1) == 1;
            var currentVersion = reader.GetInt32(2);
            var currentAt = SqliteMapping.ParseUtc(reader.GetString(3));
            if (!enabled || currentVersion != planVersion || currentAt != scheduledAtUtc)
            {
                return null;
            }
        }

        Guid? occurrenceId = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id FROM reminder_occurrences
                WHERE reminder_id = $reminderId AND plan_version = $planVersion AND plan_version > 0
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
            read.Parameters.AddWithValue("$planVersion", planVersion);
            if (read.ExecuteScalar() is string existing)
            {
                occurrenceId = Guid.Parse(existing);
            }
        }

        if (occurrenceId is null)
        {
            var occurrence = new ReminderOccurrence(
                Guid.NewGuid(),
                reminderId,
                Guid.Parse(itemId),
                scheduledAtUtc,
                ReminderOccurrenceStatus.Pending,
                notificationTag,
                optionsSnapshot,
                null,
                null,
                null,
                0,
                null,
                now,
                now,
                PlanVersion: planVersion);

            try
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO reminder_occurrences (
                        id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
                        options_snapshot_json, created_at_utc, updated_at_utc, plan_version)
                    VALUES ($id, $reminderId, $itemId, $at, 'Pending', $tag,
                            $options, $now, $now, $planVersion);
                    """;
                insert.Parameters.AddWithValue("$id", occurrence.Id.ToString("N"));
                insert.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
                insert.Parameters.AddWithValue("$itemId", itemId);
                insert.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(scheduledAtUtc));
                insert.Parameters.AddWithValue("$tag", notificationTag);
                insert.Parameters.AddWithValue("$options", JsonDefaults.Serialize(optionsSnapshot));
                insert.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
                insert.Parameters.AddWithValue("$planVersion", planVersion);
                insert.ExecuteNonQuery();
                occurrenceId = occurrence.Id;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 并发登记：复用已存在的实例。
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT id FROM reminder_occurrences
                    WHERE reminder_id = $reminderId AND plan_version = $planVersion AND plan_version > 0
                    LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
                read.Parameters.AddWithValue("$planVersion", planVersion);
                if (read.ExecuteScalar() is not string existing)
                {
                    return null;
                }

                occurrenceId = Guid.Parse(existing);
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reminders
                SET delivery_owner = 'windows-pending',
                    scheduled_tag = $tag,
                    scheduled_at_utc = $at,
                    schedule_state = 'registering',
                    schedule_plan_version = $planVersion,
                    schedule_error = NULL,
                    schedule_updated_at = $now
                WHERE id = $id
                  AND enabled = 1
                  AND plan_version = $planVersion
                  AND reminder_at_utc = $at;
                """;
            update.Parameters.AddWithValue("$tag", notificationTag);
            update.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(scheduledAtUtc));
            update.Parameters.AddWithValue("$planVersion", planVersion);
            update.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            update.Parameters.AddWithValue("$id", reminderId.ToString("N"));
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return null;
            }
        }

        transaction.Commit();
        return occurrenceId;
    }

    public bool CompleteScheduleRegistration(
        Guid reminderId,
        int planVersion,
        DateTimeOffset scheduledAtUtc,
        bool success,
        string? error,
        DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET schedule_state = $state,
                delivery_owner = $owner,
                schedule_error = $error,
                schedule_updated_at = $now
            WHERE id = $id
              AND enabled = 1
              AND plan_version = $planVersion
              AND reminder_at_utc = $at;
            """;
        command.Parameters.AddWithValue("$state", success ? "scheduled" : "failed");
        command.Parameters.AddWithValue("$owner", success ? "windows" : "app");
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$at", SqliteMapping.ToUtcText(scheduledAtUtc));
        return command.ExecuteNonQuery() == 1;
    }

    public void EnqueueCancellation(
        Guid reminderId,
        int planVersion,
        string notificationTag,
        string reason,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(notificationTag))
        {
            return;
        }

        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notification_cancellations (
                id, reminder_id, plan_version, notification_tag, reason, state,
                attempts, last_error, created_at_utc, updated_at_utc)
            SELECT $id, $reminderId, $planVersion, $tag, $reason, 'pending', 0, NULL, $now, $now
            WHERE NOT EXISTS (
                SELECT 1 FROM notification_cancellations
                WHERE notification_tag = $tag AND state <> 'cancelled'
            );
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$tag", notificationTag);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<NotificationCancellation> GetPendingCancellations(int limit)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, reminder_id, plan_version, notification_tag, reason, state, attempts, last_error
            FROM notification_cancellations
            WHERE state IN ('pending', 'failed')
            ORDER BY created_at_utc ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<NotificationCancellation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new NotificationCancellation(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public void MarkCancellationResult(Guid cancellationId, bool success, string? error, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notification_cancellations
            SET state = $state,
                attempts = attempts + 1,
                last_error = $error,
                updated_at_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", success ? "cancelled" : "failed");
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", cancellationId.ToString("N"));
        command.ExecuteNonQuery();
    }

    public bool BeginScheduleRevocation(Guid reminderId, string reason, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string? tag;
        string? state;
        int planVersion;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT scheduled_tag, schedule_state, plan_version FROM reminders WHERE id = $id;";
            read.Parameters.AddWithValue("$id", reminderId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            tag = reader.IsDBNull(0) ? null : reader.GetString(0);
            state = reader.IsDBNull(1) ? null : reader.GetString(1);
            planVersion = reader.GetInt32(2);
        }

        if (string.IsNullOrWhiteSpace(tag)
            || state is not ("scheduled" or "registering" or "revoking" or "uncertain"))
        {
            return false;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reminders
                SET schedule_state = 'revoking',
                    schedule_error = $reason,
                    schedule_updated_at = $now
                WHERE id = $id AND scheduled_tag = $tag;
                """;
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            update.Parameters.AddWithValue("$id", reminderId.ToString("N"));
            update.Parameters.AddWithValue("$tag", tag);
            update.ExecuteNonQuery();
        }

        InsertCancellationTask(connection, transaction, reminderId, planVersion, tag, reason, now);
        transaction.Commit();
        return true;
    }

    public bool CompleteScheduleRevocation(Guid reminderId, string notificationTag, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET schedule_state = NULL,
                delivery_owner = 'app',
                scheduled_tag = NULL,
                scheduled_at_utc = NULL,
                schedule_plan_version = NULL,
                schedule_error = NULL,
                schedule_updated_at = $now
            WHERE id = $id
              AND scheduled_tag = $tag
              AND schedule_state IN ('revoking', 'uncertain');
            """;
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$tag", notificationTag);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkScheduleUncertain(Guid reminderId, string notificationTag, string error, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reminders
            SET schedule_state = 'uncertain',
                schedule_error = $error,
                schedule_updated_at = $now
            WHERE id = $id
              AND scheduled_tag = $tag
              AND schedule_state = 'revoking';
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.Parameters.AddWithValue("$id", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$tag", notificationTag);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkScheduledDeliveryUnknown(Guid reminderId, int planVersion, Guid occurrenceId, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        using (var plan = connection.CreateCommand())
        {
            plan.Transaction = transaction;
            plan.CommandText = """
                UPDATE reminders
                SET schedule_state = 'failed',
                    delivery_owner = 'app',
                    schedule_error = 'scheduled-tag-missing',
                    schedule_updated_at = $now
                WHERE id = $id
                  AND enabled = 1
                  AND plan_version = $planVersion
                  AND schedule_state IN ('scheduled', 'registering');
                """;
            plan.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            plan.Parameters.AddWithValue("$id", reminderId.ToString("N"));
            plan.Parameters.AddWithValue("$planVersion", planVersion);
            if (plan.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var occurrence = connection.CreateCommand())
        {
            occurrence.Transaction = transaction;
            occurrence.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Uncertain',
                    delivery_attempts = MAX(delivery_attempts, 1),
                    last_error = 'scheduled-tag-missing',
                    delivery_owner = NULL,
                    delivery_lease_until_utc = NULL,
                    updated_at_utc = $now
                WHERE id = $occurrenceId
                  AND reminder_id = $reminderId
                  AND plan_version = $planVersion
                  AND status = 'Pending';
                """;
            occurrence.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            occurrence.Parameters.AddWithValue("$occurrenceId", occurrenceId.ToString("N"));
            occurrence.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
            occurrence.Parameters.AddWithValue("$planVersion", planVersion);
            if (occurrence.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        return true;
    }

    public bool SnoozeOccurrence(Guid occurrenceId, DateTimeOffset newReminderAtUtc, string optionId, DateTimeOffset now)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        string reminderId;
        int occurrencePlanVersion;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT reminder_id, status, plan_version FROM reminder_occurrences WHERE id = $id;";
            read.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            reminderId = reader.GetString(0);
            var status = Enum.Parse<ReminderOccurrenceStatus>(reader.GetString(1), ignoreCase: false);
            if (status is not (ReminderOccurrenceStatus.Pending
                or ReminderOccurrenceStatus.Requested
                or ReminderOccurrenceStatus.Uncertain))
            {
                return false;
            }

            occurrencePlanVersion = reader.GetInt32(2);
        }

        int planVersion;
        string? planTag;
        string? planState;
        using (var readPlan = connection.CreateCommand())
        {
            readPlan.Transaction = transaction;
            readPlan.CommandText = """
                SELECT r.enabled, r.plan_version, i.status, r.scheduled_tag, r.schedule_state
                FROM reminders r
                JOIN items i ON i.id = r.item_id
                WHERE r.id = $reminderId;
                """;
            readPlan.Parameters.AddWithValue("$reminderId", reminderId);
            using var reader = readPlan.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var enabled = reader.GetInt32(0) == 1;
            planVersion = reader.GetInt32(1);
            var itemStatus = reader.GetString(2);
            planTag = reader.IsDBNull(3) ? null : reader.GetString(3);
            planState = reader.IsDBNull(4) ? null : reader.GetString(4);

            if (!enabled || itemStatus != "Open" || occurrencePlanVersion != planVersion)
            {
                return false;
            }
        }

        // 稍后提醒会推进计划版本；旧 tag 必须独立撤销，不能留给新版本继续压制。
        if (!string.IsNullOrWhiteSpace(planTag)
            && planState is "scheduled" or "registering" or "revoking" or "uncertain")
        {
            InsertCancellationTask(connection, transaction, Guid.Parse(reminderId), planVersion, planTag!, "snoozed", now);
        }

        using (var occurrence = connection.CreateCommand())
        {
            occurrence.Transaction = transaction;
            occurrence.CommandText = """
                UPDATE reminder_occurrences
                SET status = 'Snoozed', action = $action, action_at_utc = $now,
                    delivery_owner = NULL, delivery_lease_until_utc = NULL, updated_at_utc = $now
                WHERE id = $id;
                """;
            occurrence.Parameters.AddWithValue("$action", $"snooze:{optionId}");
            occurrence.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            occurrence.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
            occurrence.ExecuteNonQuery();
        }

        using (var reminder = connection.CreateCommand())
        {
            reminder.Transaction = transaction;
            reminder.CommandText = """
                UPDATE reminders
                SET reminder_at_utc = $next,
                    source = 'Manual', relative_seconds = NULL, captured_at_utc = NULL, source_time_text = NULL,
                    plan_version = plan_version + 1,
                    delivery_owner = NULL,
                    scheduled_tag = NULL,
                    scheduled_at_utc = NULL,
                    schedule_state = NULL,
                    schedule_plan_version = NULL,
                    schedule_error = NULL,
                    schedule_updated_at = $now,
                    updated_at_utc = $now
                WHERE id = $reminderId AND enabled = 1;
                """;
            reminder.Parameters.AddWithValue("$next", SqliteMapping.ToUtcText(newReminderAtUtc));
            reminder.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
            reminder.Parameters.AddWithValue("$reminderId", reminderId);
            if (reminder.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        transaction.Commit();
        return true;
    }

    private static void InsertCancellationTask(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid reminderId,
        int planVersion,
        string notificationTag,
        string reason,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO notification_cancellations (
                id, reminder_id, plan_version, notification_tag, reason, state,
                attempts, last_error, created_at_utc, updated_at_utc)
            SELECT $id, $reminderId, $planVersion, $tag, $reason, 'pending', 0, NULL, $now, $now
            WHERE NOT EXISTS (
                SELECT 1 FROM notification_cancellations
                WHERE notification_tag = $tag AND state <> 'cancelled'
            );
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$reminderId", reminderId.ToString("N"));
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$tag", notificationTag);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
        command.ExecuteNonQuery();
    }

    private static void AddClaimParameters(SqliteCommand command, Guid occurrenceId, string owner, DateTimeOffset now, TimeSpan leaseDuration)    {
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$leaseUntil", SqliteMapping.ToUtcText(now.Add(leaseDuration)));
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
    }

    private static void AddOwnerParameters(SqliteCommand command, Guid occurrenceId, string owner, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("N"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", SqliteMapping.ToUtcText(now));
    }

    private const string SelectReminders = """
        SELECT id, item_id, reminder_at_utc, enabled, plan_version, created_at_utc, updated_at_utc,
               delivery_owner, scheduled_tag, scheduled_at_utc, schedule_state,
               schedule_plan_version, schedule_error, schedule_updated_at,
               source, relative_seconds, captured_at_utc, source_time_text
        FROM reminders
        """;

    private const string SelectOccurrences = """
        SELECT id, reminder_id, item_id, scheduled_at_utc, status, notification_tag,
               options_snapshot_json, requested_at_utc, action, action_at_utc,
               attempt_count, last_error, created_at_utc, updated_at_utc,
               handed_off_at_utc, channel, plan_version, delivery_attempts,
               recovery_attempts, delivery_lease_until_utc, delivery_owner, cancel_reason,
               manual_attempts
        FROM reminder_occurrences
        """;

    private static ReminderPlan MapReminder(SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        ItemId: Guid.Parse(reader.GetString(1)),
        ReminderAtUtc: SqliteMapping.ParseUtc(reader.GetString(2)),
        Enabled: reader.GetInt32(3) == 1,
        PlanVersion: reader.GetInt32(4),
        CreatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(5)),
        UpdatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(6)),
        DeliveryOwner: reader.IsDBNull(7) ? null : reader.GetString(7),
        ScheduledTag: reader.IsDBNull(8) ? null : reader.GetString(8),
        ScheduledAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
        ScheduleState: reader.IsDBNull(10) ? null : reader.GetString(10),
        SchedulePlanVersion: reader.IsDBNull(11) ? null : reader.GetInt32(11),
        ScheduleError: reader.IsDBNull(12) ? null : reader.GetString(12),
        ScheduleUpdatedAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(13) ? null : reader.GetString(13)),
        Source: reader.GetString(14),
        RelativeSeconds: reader.IsDBNull(15) ? null : reader.GetInt64(15),
        CapturedAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(16) ? null : reader.GetString(16)),
        SourceTimeText: reader.IsDBNull(17) ? null : reader.GetString(17));

    private static ReminderOccurrence MapOccurrence(SqliteDataReader reader)
    {
        IReadOnlyList<SnoozeOption> options;
        try
        {
            options = reader.IsDBNull(6)
                ? SnoozeOption.Defaults
                : JsonDefaults.Deserialize<List<SnoozeOption>>(reader.GetString(6)) ?? SnoozeOption.Defaults;
        }
        catch (System.Text.Json.JsonException)
        {
            options = SnoozeOption.Defaults;
        }

        return new ReminderOccurrence(
            Id: Guid.Parse(reader.GetString(0)),
            ReminderId: Guid.Parse(reader.GetString(1)),
            ItemId: Guid.Parse(reader.GetString(2)),
            ScheduledAtUtc: SqliteMapping.ParseUtc(reader.GetString(3)),
            Status: Enum.Parse<ReminderOccurrenceStatus>(reader.GetString(4), ignoreCase: false),
            NotificationTag: reader.GetString(5),
            OptionsSnapshot: options,
            RequestedAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Action: reader.IsDBNull(8) ? null : reader.GetString(8),
            ActionAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(9) ? null : reader.GetString(9)),
            AttemptCount: reader.GetInt32(10),
            LastError: reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(12)),
            UpdatedAtUtc: SqliteMapping.ParseUtc(reader.GetString(13)),
            HandedOffAtUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(14) ? null : reader.GetString(14)),
            Channel: reader.IsDBNull(15) ? null : reader.GetString(15),
            PlanVersion: reader.GetInt32(16),
            DeliveryAttempts: reader.GetInt32(17),
            RecoveryAttempts: reader.GetInt32(18),
            DeliveryLeaseUntilUtc: SqliteMapping.ParseUtcOrNull(reader.IsDBNull(19) ? null : reader.GetString(19)),
            DeliveryOwner: reader.IsDBNull(20) ? null : reader.GetString(20),
            CancelReason: reader.IsDBNull(21) ? null : reader.GetString(21),
            ManualAttempts: reader.GetInt32(22));
    }
}
